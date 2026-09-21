using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>
/// No validator session ever reached a process: no candidate to run, an unknown harness, a CLI that is not on
/// PATH, or no repository to run in. Nothing was spent and nothing was tried.
/// </summary>
public sealed class ValidatorLaunchException(string message) : Exception(message);

/// <summary>A validator session started and produced nothing — reaped at its timeout, or it crashed.</summary>
public sealed class ValidatorSessionException(string message) : Exception(message);

/// <summary>
/// What any session the conductor starts may and may not run.
/// <para>
/// One home for both launchers, because this is a boundary rather than a convenience: the deny list is the whole
/// of what stops an unattended session pushing, merging, or checking out the default branch, and two copies of a
/// boundary is how one of them quietly grows a hole. A validator and an orchestrator differ in what they are told
/// to do, never in what they are allowed to do — an orchestrator does more, but nothing it does needs more reach
/// than the hub's own CLI and the tools to build with.
/// </para>
/// </summary>
internal static class SessionCommands
{
    internal static readonly string[] Allowed = ["muthur*", "git*", "dotnet*", "pwsh*", "powershell*"];

    internal static readonly string[] Denied = ["git push*", "git merge*", "git rebase*", "git checkout main*", "git switch main*", "gh*"];
}

/// <summary>
/// Starts a real validator session through <see cref="AgentLauncher"/>.
/// <para>
/// The session is an ordinary registered agent with an ordinary token: nothing the conductor starts has authority
/// a founder-started terminal would not. It is told which role to take and which task is waiting, and then it
/// follows the same validate procedure a human-started session follows — including the part where it may decide
/// the product cannot be driven and report blocked instead of passing.
/// </para>
/// </summary>
public sealed class ValidatorSessionLauncher(
    Ledger ledger,
    MuthurOptions options,
    AgentService agents,
    HarnessService harnesses,
    IProcessRunner processes,
    ILogger<ValidatorSessionLauncher> logger, TimeProvider? clock = null) : IValidatorSessionLauncher
{
    private const string Tier = "mastermind";

    public async Task StartAsync(ConductorAssignment assignment, CancellationToken ct = default)
    {
        var repo = await RepositoryPathAsync(assignment.Project, ct);
        if (repo is null || !Directory.Exists(repo))
            throw new ValidatorLaunchException($"Project '{assignment.Project}' has no repository on disk.");

        var candidates = await CandidatesAsync(assignment.AvoidHarness, ct);
        if (candidates.Count == 0)
            throw new ValidatorLaunchException($"No available {Tier} candidate to validate {assignment.TaskKey}.");

        var scratch = Path.Combine(options.DataDir, "conductor", $"{assignment.TaskKey}-{assignment.RoleKey}");
        Directory.CreateDirectory(scratch);

        var launcher = new AgentLauncher(processes, heartbeat: agents.RenewChildAsync, timeProvider: clock, exited: agents.ReleaseChildAsync);
        var attempts = await launcher.RunAsync(
            candidates,
            candidate => new WorkerRequest(
                WorkingDirectory: repo,
                Prompt: Prompt(assignment, candidate),
                Model: candidate.Model,
                GitCommonDirectory: null,
                AllowedCommands: SessionCommands.Allowed,
                DeniedCommands: SessionCommands.Denied,
                ScratchDirectory: scratch,
                ReasoningEffort: candidate.ReasoningEffort),
            candidate => IdentityFor(assignment, candidate, ct),
            TimeSpan.FromMinutes(options.ConductorSessionMinutes),
            candidate => MarkLimitedAsync(candidate.Account, ct),
            ct);

        EnsureSomethingRan(assignment.TaskKey, attempts);

        var last = attempts[^1];
        logger.LogInformation("Validator session for {Task}/{Role} finished on {Harness}.",
            assignment.TaskKey, assignment.RoleKey, last.Candidate.Harness);
    }

    /// <summary>
    /// Turns a set of attempts into the one thing the conductor needs to know: did a validator do its job.
    /// <para>
    /// Two different faults hide behind "not successful". A harness this build cannot run never reached a process —
    /// nothing was spent and nothing was tried. A session that started and was reaped at its timeout held the role,
    /// consumed the founder's quota, and hung. Telling a founder to look for a missing CLI when the real problem is
    /// hanging sessions sends them to the wrong place, so the two leave by different doors.
    /// </para>
    /// </summary>
    internal static void EnsureSomethingRan(string taskKey, IReadOnlyList<WorkerAttempt> attempts)
    {
        // A session that ran and voted - either way - is the system working.
        if (attempts.Any(a => a.Outcome.Success)) return;

        var last = attempts.Count > 0 ? attempts[^1] : null;
        if (last is null || !attempts.Any(a => a.Started))
            throw new ValidatorLaunchException(last?.Outcome.Report ?? $"No candidate reported why no validator ran for {taskKey}.");

        throw new ValidatorSessionException(attempts.Last(a => a.Started).Outcome.Report);
    }

    /// <summary>
    /// One agent per (task, role) pair — the same pair the conductor staffs — so a retry of that pair reuses its
    /// name and the ledger keeps a stable, readable actor. It must not be one agent per role: a validator role may
    /// have several holders, registering re-issues that agent's token, and two sessions sharing a name would leave
    /// the older one holding a revoked token, failing every call while the conductor still counts it as running.
    /// It is registered for the candidate that is about to run — a fall-through to another vendor must not leave
    /// the ledger saying the first one did the work.
    /// </summary>
    internal async Task<AgentIdentity> IdentityFor(ConductorAssignment assignment, HarnessCandidate candidate, CancellationToken ct)
    {
        var name = IdentityName(assignment.TaskKey, assignment.RoleKey);
        // A tier entry may leave the model blank to mean "the harness's own default"; the ledger still needs a word.
        var model = candidate.Model is { Length: > 0 } ? candidate.Model : "default";
        var registration = await agents.RegisterConductorSessionAsync(
            new RegisterAgentRequest(name, candidate.Harness, model, Tier, candidate.Account), ct);
        return new AgentIdentity(name, registration.Token);
    }

    /// <summary>
    /// The agent name for one staffed pair: stable across retries of that pair, distinct between pairs.
    /// <para>
    /// Plain concatenation, and the whole of both parts. Two earlier versions squeezed this into a 48-character
    /// name and both failed validation: truncating the head discarded exactly what told two long role keys apart,
    /// and a six-hex digest of the role left 24 bits a validator brute-forced. The fix was not a cleverer encoding
    /// but removing the limit that forced one — <see cref="AgentService"/> allows 80 characters, and the worst case
    /// the hub can produce is 71: "conductor-" (10) + a role key of at most 48 + "-t-" (3) + a 10-digit task id.
    /// </para>
    /// <para>
    /// Injective over everything the hub accepts, by counting rather than by luck. Task keys are "T-" and a
    /// positive int, so the tail is always a non-empty run of digits: R1 + "-t-" + d1 == R2 + "-t-" + d2 forces
    /// the two role keys to be the same length — a tail shorter by one, two or three would land '-', 't' or '-'
    /// where the other string has a digit — and equal lengths make R1 == R2 and d1 == d2. A role key that itself
    /// contains "-t-" does not break it. The bound on the length, and the character set that keeps the result a
    /// legal agent name, are <c>RoleService.KeyPattern</c>'s to hold; ConductorTests pins that.
    /// </para>
    /// </summary>
    internal static string IdentityName(string task, string role) =>
        $"conductor-{role}-{task.ToLowerInvariant()}";       // "T-3" -> "conductor-win-validator-t-3"

    /// <summary>An account that could not answer leaves the rotation, exactly as `muthur agent limited` does.</summary>
    internal Task MarkLimitedAsync(string? account, CancellationToken ct) =>
        account is { Length: > 0 }
            ? ledger.MutateAsync(Caller.Founder, m => HarnessService.ExhaustedAsync(m, account, ct), ct)
            : Task.CompletedTask;

    /// <summary>
    /// Candidates for the tier, with the harness that built the task moved to the back rather than removed:
    /// a different vendor checking the work is a preference, and validating beats not validating.
    /// </summary>
    private async Task<IReadOnlyList<HarnessCandidate>> CandidatesAsync(string? avoid, CancellationToken ct)
    {
        var tiers = await harnesses.TiersAsync(Tier, ct);
        return Prefer(tiers.SelectMany(t => t.Candidates).Where(c => !c.Limited)
            .Select(c => new HarnessCandidate(c.Harness, c.Model, c.Account, c.ReasoningEffort)).ToList(), avoid);
    }

    /// <summary>
    /// The harness that built the task goes to the back of the queue, never off it. One vendor checking another's
    /// work is the preference; when the other vendor is out of quota, the builder's own harness validating is
    /// still better than a task nobody looks at.
    /// </summary>
    internal static IReadOnlyList<HarnessCandidate> Prefer(IReadOnlyList<HarnessCandidate> available, string? avoid) =>
        avoid is { Length: > 0 }
            ? [.. available.Where(c => c.Harness != avoid), .. available.Where(c => c.Harness == avoid)]
            : available;

    private Task<string?> RepositoryPathAsync(string projectKey, CancellationToken ct) =>
        ledger.ReadAsync((db, _) => db.Projects.Where(p => p.Key == projectKey).Select(p => p.RepoPath).SingleOrDefaultAsync(ct), ct);

    private static string Prompt(ConductorAssignment assignment, HarnessCandidate candidate) =>
        $"""
        You are a validator on call in this MUTHUR organization, acting as the agent in $MUTHUR_AGENT.

        Take the role `{assignment.RoleKey}` and read its brief — it is your job description:

            muthur role take {assignment.RoleKey}
            muthur role brief {assignment.RoleKey}
            muthur validate claim {assignment.TaskKey} --as {assignment.RoleKey}

        Then validate {assignment.TaskKey} ("{assignment.TaskTitle}"): follow the validate procedure in this
        repository, exercise the change end to end on the real product, and give a verdict with evidence.

        Rules that are not yours to bend:
        - Claim the task before you start. If the claim is refused, another validator has it: release the role and stop.
        - Retain currentSubject.id from that claim. Inspect currentSubject.implementationSha in your own worktree,
          and the recorded spec digest, checks and policy. Never replace the retained ID by fetching at verdict time.
        - Every verdict needs --subject <retained-guid> and --evidence <report> or --evidence-file <UTF-8 file>.
          Describe the check, observation and artifact/reference or reproduction command in at least 20 characters.
        - You did not write this task and must not fix what you find. Report it.
        - A pass says you ran the product and it worked; it never says the diff looked right.
        - If the product cannot be driven unattended, the verdict is not pass: message the task's owner, say what
          stopped you, and release the role.
        - Never push, merge, or check out the default branch. Work in a worktree of your own and remove it after.

        When you are done, release the role. You are running unattended on {candidate.Harness}, so leave nothing
        behind that a person would have to clean up.
        """;
}
