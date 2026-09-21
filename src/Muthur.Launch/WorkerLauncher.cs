using System.Diagnostics;

namespace Muthur.Launch;

/// <summary>One way to staff a tier: which harness, which model, on whose account.</summary>
public sealed record HarnessCandidate(string Harness, string Model, string? Account, string? ReasoningEffort = null);

/// <param name="Started">
/// Whether a process was actually launched. False only for the cases that never reached one — a harness this build
/// does not know, or a CLI that is not on PATH. A session that started and then failed, crashed or was reaped is a
/// different fault from one that never began, and callers must be able to tell them apart.
/// </param>
public sealed record WorkerAttempt(HarnessCandidate Candidate, WorkerOutcome Outcome, TimeSpan Duration, bool Started = true,
    string? RunId = null, string? FailureKind = null, int? ExitCode = null, CapabilityMatch? CapabilityMatch = null)
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
    TimeProvider? timeProvider = null, Func<string, IHarnessAdapter?>? adapterFor = null)
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
                match = (await new CapabilityEvaluator(processes, timeProvider, _resolve)
                    .InspectAsync((adapterFor ?? Harnesses.Find)(candidate.Harness), requested, ct)).Match;
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

            var runId = Guid.NewGuid().ToString("n");
            var request = SessionWorkspace.ForAttempt(requested, runId);
            var invocation = adapter.Build(request);
            var resolved = requested.Capabilities is { Requirements.Count: > 0 }
                ? CapabilityExecutable.Resolve(invocation.FileName, _resolve) : _resolve(invocation.FileName);
            if (resolved is not { } executable)
            {
                attempts.Add(new(candidate, new WorkerOutcome(false, $"'{invocation.FileName}' is not installed or not on PATH.", false), clock.Elapsed, Started: false));
                continue;
            }

            var result = await processes.RunAsync(executable.FileName, [.. executable.Prefix, .. invocation.Arguments],
                request.WorkingDirectory, invocation.Stdin, timeout, ct, Scrubbed, request.GitEnvironment);
            var outcome = adapter.Interpret(request, result);
            attempts.Add(new(candidate, outcome, clock.Elapsed, RunId: runId,
                FailureKind: SessionWorkspace.FailureKind(result, outcome), ExitCode: result.ExitCode, CapabilityMatch: match));

            if (!outcome.RateLimited) break;
            await onRateLimited(candidate);
        }
        return attempts;
    }
}
