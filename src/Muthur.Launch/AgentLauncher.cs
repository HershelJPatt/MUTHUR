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
public sealed class AgentLauncher(IProcessRunner processes, Func<string, (string FileName, IReadOnlyList<string> Prefix)?>? resolve = null)
{
    private readonly Func<string, (string FileName, IReadOnlyList<string> Prefix)?> _resolve = resolve ?? ExecutableResolver.Resolve;

    /// <summary>The identity a session acts under, as the hub's own CLI reads it.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentFor(AgentIdentity identity) =>
        new Dictionary<string, string>
        {
            ["MUTHUR_AGENT"] = identity.Name,
            ["MUTHUR_TOKEN"] = identity.Token,
        };

    public async Task<IReadOnlyList<WorkerAttempt>> RunAsync(
        IReadOnlyList<HarnessCandidate> candidates,
        Func<HarnessCandidate, WorkerRequest> requestFor,
        AgentIdentity identity,
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
                attempts.Add(new(candidate, new WorkerOutcome(false, $"Unknown harness '{candidate.Harness}'.", false), clock.Elapsed));
                continue;
            }

            var request = requestFor(candidate);
            var invocation = adapter.Build(request);
            if (_resolve(invocation.FileName) is not { } executable)
            {
                attempts.Add(new(candidate, new WorkerOutcome(false, $"'{invocation.FileName}' is not installed or not on PATH.", false), clock.Elapsed));
                continue;
            }

            var result = await processes.RunAsync(executable.FileName, [.. executable.Prefix, .. invocation.Arguments],
                request.WorkingDirectory, invocation.Stdin, timeout, ct, scrubEnvironment: null, environment: EnvironmentFor(identity));
            var outcome = adapter.Interpret(request, result);
            attempts.Add(new(candidate, outcome, clock.Elapsed));

            // A session that ran and gave a verdict is finished, right or wrong. Only an account that could not
            // answer at all is worth trying elsewhere.
            if (!outcome.RateLimited) break;
            await onRateLimited(candidate);
        }
        return attempts;
    }
}
