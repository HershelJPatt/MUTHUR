using System.Diagnostics;

namespace Muthur.Launch;

/// <summary>Who a launched session acts as. The token is the same kind any registered agent holds.</summary>
public sealed record AgentIdentity(string Name, string Token);

/// <summary>
/// Runs one session that acts <em>as</em> a registered agent — a validator, today.
/// <para>
/// The difference from <see cref="WorkerLauncher"/> is authority, and it is the whole reason this is a separate
/// class. A worker implements a frozen spec in a worktree and reports to its orchestrator; it cannot claim, land
/// or vote, so its hub credentials are scrubbed. An agent session holds a role and casts a verdict that decides
/// whether work ships, so it is given an identity and may call the hub. Neither launcher should ever grow the
/// other's behaviour: the tests assert the inversion in both directions.
/// </para>
/// </summary>
public sealed class AgentLauncher(IProcessRunner processes, Func<string, (string FileName, IReadOnlyList<string> Prefix)?>? resolve = null,
    Func<AgentIdentity, CancellationToken, Task>? heartbeat = null, TimeProvider? timeProvider = null,
    Func<AgentIdentity, CancellationToken, Task>? exited = null,
    IReadOnlyDictionary<string, string>? extraEnvironment = null)
{
    private readonly Func<string, (string FileName, IReadOnlyList<string> Prefix)?> _resolve = resolve ?? ExecutableResolver.Resolve;

    /// <summary>The identity a session acts under, as the hub's own CLI reads it.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentFor(AgentIdentity identity) =>
        new Dictionary<string, string>
        {
            ["MUTHUR_AGENT"] = identity.Name,
            ["MUTHUR_TOKEN"] = identity.Token,
        };

    /// <param name="identityFor">
    /// Called for the candidate about to run, not once up front: the launcher may fall through to another vendor
    /// when an account is out of quota, and the session's ledger entries must name whoever actually ran.
    /// </param>
    public async Task<IReadOnlyList<WorkerAttempt>> RunAsync(
        IReadOnlyList<HarnessCandidate> candidates,
        Func<HarnessCandidate, WorkerRequest> requestFor,
        Func<HarnessCandidate, Task<AgentIdentity>> identityFor,
        TimeSpan timeout,
        Func<HarnessCandidate, Task> onRateLimited,
        CancellationToken ct = default)
    {
        var attempts = new List<WorkerAttempt>();
        foreach (var candidate in candidates)
        {
            var clock = Stopwatch.StartNew();
            if (Harnesses.Find(candidate.Harness) is not { } adapter)
            {
                attempts.Add(new(candidate, new WorkerOutcome(false, $"Unknown harness '{candidate.Harness}'.", false), clock.Elapsed, Started: false));
                continue;
            }

            var runId = Guid.NewGuid().ToString("n");
            var request = SessionWorkspace.ForAttempt(requestFor(candidate), runId);
            var invocation = adapter.Build(request);
            if (_resolve(invocation.FileName) is not { } executable)
            {
                attempts.Add(new(candidate, new WorkerOutcome(false, $"'{invocation.FileName}' is not installed or not on PATH.", false), clock.Elapsed, Started: false));
                continue;
            }

            var identity = await identityFor(candidate);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var environment = new Dictionary<string, string>(extraEnvironment ?? new Dictionary<string, string>());
            foreach (var pair in request.GitEnvironment ?? new Dictionary<string, string>()) environment[pair.Key] = pair.Value;
            foreach (var pair in EnvironmentFor(identity)) environment[pair.Key] = pair.Value;
            var running = processes.RunAsync(executable.FileName, [.. executable.Prefix, .. invocation.Arguments],
                request.WorkingDirectory, invocation.Stdin, timeout, lifetime.Token,
                scrubEnvironment: null, environment: environment);
            ProcessResult result;
            try
            {
                while (heartbeat is not null && !running.IsCompleted)
                {
                    var tick = Task.Delay(TimeSpan.FromSeconds(30), timeProvider ?? TimeProvider.System, lifetime.Token);
                    if (await Task.WhenAny(running, tick) == running) break;
                    await tick;
                    if (!running.IsCompleted) await heartbeat(identity, ct);
                }
                result = await running;
            }
            catch
            {
                await lifetime.CancelAsync();
                try { await running; } catch { /* Observe the child shutdown before releasing the session slot. */ }
                throw;
            }
            finally
            {
                await lifetime.CancelAsync();
                if (exited is not null) await exited(identity, CancellationToken.None);
            }
            var outcome = adapter.Interpret(request, result);
            attempts.Add(new(candidate, outcome, clock.Elapsed, RunId: runId,
                FailureKind: SessionWorkspace.FailureKind(result, outcome), ExitCode: result.ExitCode));

            // A session that ran and gave a verdict is finished, right or wrong. Only an account that could not
            // answer at all is worth trying elsewhere.
            if (!outcome.RateLimited) break;
            await onRateLimited(candidate);
        }
        return attempts;
    }
}
