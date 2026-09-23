using System.Diagnostics;

namespace Muthur.Launch;

/// <summary>One way to staff a tier: which harness, which model, on whose account.</summary>
/// <param name="MaxTurns">A cap on the harness's agentic turns for one session, where the harness has one; null means the harness's own default.</param>
/// <param name="ContextWindow">The model's context window in tokens, for a harness that must be told it; null means the adapter's default.</param>
public sealed record HarnessCandidate(string Harness, string Model, string? Account, string? ReasoningEffort = null, int? MaxTurns = null, int? ContextWindow = null);

/// <param name="Started">
/// Whether a process was actually launched. False only for the cases that never reached one — a harness this build
/// does not know, or a CLI that is not on PATH. A session that started and then failed, crashed or was reaped is a
/// different fault from one that never began, and callers must be able to tell them apart.
/// </param>
/// <param name="PromptBytes">The prompt handed to a process that started, in UTF-8 bytes: what the model read before its first action.</param>
public sealed record WorkerAttempt(HarnessCandidate Candidate, WorkerOutcome Outcome, TimeSpan Duration, bool Started = true,
    string? RunId = null, string? FailureKind = null, int? ExitCode = null, CapabilityMatch? CapabilityMatch = null, string? ReservationId = null, WorkerCleanup? Cleanup = null,
    int? PromptBytes = null)
{
    public int FullStarts => Started ? 1 : 0;
    public int FullSessionsAvoided => !Started && FailureKind == "capability_mismatch" ? 1 : 0;
    public int ProbeStarts => 0;
}

/// <summary>
/// Runs one headless worker, falling through the tier's candidates when one is unavailable
/// (not installed, or its account is out of quota). A worker that ran and failed is not retried
/// elsewhere: its half-done work is in the worktree and needs the orchestrator's eyes.
/// </summary>
public sealed class WorkerLauncher(IProcessRunner processes, Func<string, (string FileName, IReadOnlyList<string> Prefix)?>? resolve = null,
    TimeProvider? timeProvider = null, Func<string, IHarnessAdapter?>? adapterFor = null,
    IWorkerProcessRunner? workerProcesses = null, WorkerAdmissionContext? admission = null,
    Func<CancellationToken, Task>? prepare = null, Func<bool>? setupCleanupConfirmed = null)
{
    private readonly Func<string, (string FileName, IReadOnlyList<string> Prefix)?> _resolve = resolve ?? ExecutableResolver.Resolve;

    /// <summary>Hub identity never reaches a worker: workers have no authority in the organization.</summary>
    private static readonly string[] Scrubbed = ["MUTHUR_AGENT", "MUTHUR_TOKEN"];

    public async Task<IReadOnlyList<WorkerAttempt>> RunAsync(
        IReadOnlyList<HarnessCandidate> candidates,
        Func<HarnessCandidate, WorkerRequest> requestFor,
        TimeSpan timeout,
        Func<HarnessCandidate, Task> onRateLimited,
        CancellationToken ct = default)
    {
        var attempts = new List<WorkerAttempt>();
        foreach (var candidate in candidates)
        {
            var clock = Stopwatch.StartNew();
            var requested = requestFor(candidate);
            CapabilityMatch? match = null;
            if (requested.Capabilities is { Requirements.Count: > 0 })
            {
                var inspectionRequest = !Directory.Exists(requested.WorkingDirectory) && admission is not null
                    ? requested with { WorkingDirectory = admission.Repository } : requested;
                match = (await new CapabilityEvaluator(processes, timeProvider, _resolve)
                    .InspectAsync((adapterFor ?? Harnesses.Find)(candidate.Harness), inspectionRequest, ct)).Match;
                if (!match.Allowed)
                {
                    attempts.Add(new(candidate, new(false, CapabilityEvaluator.Explain(match), false), clock.Elapsed,
                        Started: false, FailureKind: "capability_mismatch", CapabilityMatch: match));
                    continue;
                }
            }
            if ((adapterFor ?? Harnesses.Find)(candidate.Harness) is not { } adapter)
            {
                attempts.Add(new(candidate, new WorkerOutcome(false, $"Unknown harness '{candidate.Harness}'.", false), clock.Elapsed, Started: false));
                continue;
            }

            var contained = workerProcesses ?? new WorkerProcessRunner();
            var executableName = adapter.CapabilityExecutable;
            var resolved = executableName is null ? null : CapabilityExecutable.Resolve(executableName, _resolve);
            if (resolved is not { } executable || !contained.Supported)
            {
                attempts.Add(new(candidate, new(false, "Worker executable is not installed or process containment is unavailable.", false), clock.Elapsed, Started: false));
                continue;
            }
            var runId = Guid.NewGuid().ToString("N");
            Muthur.Contracts.WorkerAdmissionDto? reservation = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (admission is null) throw new InvalidOperationException("A task-bound full-worker admission client is required.");
                var binding = admission.Bind(candidate, requested, runId, timeout);
                reservation = await admission.Client.AdmitAsync(binding, ct);
                if (!Guid.TryParseExact(reservation.ReservationId, "N", out _) || reservation.Task != binding.Task || reservation.RunId != runId)
                    throw new InvalidOperationException("Malformed or mismatched full-worker admission response; execution refused.");
                if (!reservation.MayExecute)
                    throw new InvalidOperationException("Admission replay does not grant execution or release permission.");
            }
            catch (Exception ex)
            {
                attempts.Add(new(candidate, new(false, ex.Message, false), clock.Elapsed, Started: false, RunId: runId,
                    FailureKind: "worker_admission_failed", ReservationId: reservation?.ReservationId));
                break;
            }

            var cleanup = WorkerCleanup.NotStarted;
            var started = false;
            var outcome = new WorkerOutcome(false, "Worker did not execute.", false);
            string? failure = null;
            int? exitCode = null, promptBytes = null;
            var preparing = false;
            var verification = new WorkerSetupRunner(contained);
            try
            {
                ct.ThrowIfCancellationRequested();
                preparing = true;
                if (prepare is not null) await prepare(ct);
                preparing = false;
                ct.ThrowIfCancellationRequested();
                if (requested.Capabilities is { Requirements.Count: > 0 } && prepare is not null)
                {
                    match = (await new CapabilityEvaluator(verification, timeProvider, _resolve).InspectAsync(adapter, requested, ct)).Match;
                    if (!verification.CleanupConfirmed) throw new InvalidOperationException("Capability identity subprocess cleanup is uncertain.");
                    if (!match.Allowed) throw new WorkerDispatchException("capability_mismatch", CapabilityEvaluator.Explain(match));
                }
                var request = SessionWorkspace.ForAttempt(requested, runId);
                var invocation = adapter.Build(request);
                promptBytes = System.Text.Encoding.UTF8.GetByteCount(invocation.Stdin);
                // Build is intentionally after reservation: adapters may write settings files.
                cleanup = WorkerCleanup.CleanupUncertain;
                var environment = new Dictionary<string, string>(request.GitEnvironment ?? new Dictionary<string, string>());
                foreach (var pair in invocation.Environment ?? new Dictionary<string, string>()) environment[pair.Key] = pair.Value;
                var result = await contained.RunAsync(executable.FileName, [.. executable.Prefix, .. invocation.Arguments],
                    request.WorkingDirectory, invocation.Stdin, timeout, ct, Scrubbed, environment);
                started = result.Started; cleanup = result.Cleanup; exitCode = result.Result.ExitCode;
                outcome = adapter.Interpret(request, result.Result);
                failure = SessionWorkspace.FailureKind(result.Result, outcome);
            }
            catch (Exception ex)
            {
                if (preparing && setupCleanupConfirmed?.Invoke() != true) cleanup = WorkerCleanup.CleanupUncertain;
                if (!verification.CleanupConfirmed) cleanup = WorkerCleanup.CleanupUncertain;
                outcome = new(false, ex.Message, false);
                failure = ex is OperationCanceledException ? "cancelled" : ex is WorkerDispatchException dispatch ? dispatch.Code : "worker_setup_failed";
            }
            if (cleanup == WorkerCleanup.CleanupUncertain)
            {
                failure = "worker_cleanup_uncertain";
                outcome = new(false, outcome.Report + $" Cleanup unconfirmed; reservation {reservation.ReservationId} retained.", false);
            }
            else
            {
                using var releaseBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await admission!.Client.ReleaseAsync(new(reservation.ReservationId, true), releaseBudget.Token); }
                catch (Exception ex)
                {
                    outcome = new(false, outcome.Report + $" Release failed for {reservation.ReservationId}: {ex.Message}", false);
                    failure = "worker_release_failed";
                }
            }
            attempts.Add(new(candidate, outcome, clock.Elapsed, Started: started, RunId: runId,
                FailureKind: failure, ExitCode: exitCode, CapabilityMatch: match, ReservationId: reservation.ReservationId, Cleanup: cleanup,
                PromptBytes: started ? promptBytes : null));
            if (!outcome.RateLimited) break;
            try { await onRateLimited(candidate); }
            catch (Exception ex)
            {
                attempts[^1] = attempts[^1] with
                {
                    Outcome = new(false, outcome.Report + " Account update failed: " + ex.Message, false),
                    FailureKind = "worker_account_update_failed",
                };
                break;
            }
        }
        return attempts;
    }
}
