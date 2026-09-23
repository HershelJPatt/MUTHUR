using System.CommandLine;
using System.Text;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

/// <summary>Harness-agnostic delegation: staff a tier, give the worker a worktree and a frozen spec, bring back its report.</summary>
public static class WorkerCommands
{
    internal static readonly string[] DefaultAllowed =
    [
        "dotnet *", "npm *", "node *", "git status*", "git add *", "git commit *", "git diff*", "git log*", "git show*",
        "git branch --show-current", "ls*", "cat *",
    ];

    private static readonly string[] Denied =
        ["muthur *", "muthur.exe *", "git push*", "git merge*", "git rebase*", "git checkout*", "git switch*", "git worktree*", "gh *"];

    public static void AddTo(RootCommand root) => AddTo(root, new ProcessRunner());

    internal static void AddTo(RootCommand root, IProcessRunner processes)
    {
        AddHarness(root);

        var worker = new Command("worker", "Headless workers on any harness (claude, codex, local models).");
        root.Subcommands.Add(worker);

        var tier = new Option<string>("--tier") { Description = "mastermind | implementer | local-implementer | utility", DefaultValueFactory = _ => "implementer" };
        var spec = new Option<string>("--spec") { Description = "Frozen spec, relative to the repository root. Must be committed on the base branch.", Required = true };
        var unit = new Option<string?>("--unit") { Description = "The unit of the spec this worker owns (default: the whole spec)." };
        var task = new Option<string?>("--task") { Description = "Task id, recorded in the ledger with the run." };
        var harness = new Option<string?>("--harness") { Description = "Only use this harness from the tier's candidates." };
        var baseRef = new Option<string?>("--base") { Description = "Branch the worker starts from (default: the current branch)." };
        var defaultBranch = new Option<string?>("--default-branch") { Description = "Named local default branch (otherwise resolved from the task/project registration)." };
        var branch = new Option<string?>("--branch") { Description = "Branch to create for the worker (default: worker/<task>-<unit>-<id>)." };
        var note = new Option<string?>("--note") { Description = "One extra instruction for the worker." };
        var parent = new Option<string?>("--parent")
            { Description = "The worker run whose plan this unit came from. Recorded so a two-level fan-out is readable in the ledger." };
        var timeout = new Option<int>("--timeout-minutes") { DefaultValueFactory = _ => 60 };
        var run = new Command("run", "Run one worker in its own worktree and print its report. The worker gets no hub identity.")
            { tier, spec, unit, task, harness, baseRef, defaultBranch, branch, note, parent, timeout };
        run.SetAction((parse, ct) => RunAsync(parse, new RunOptions(
            parse.GetValue(tier)!, parse.GetValue(spec)!, parse.GetValue(unit), parse.GetValue(task), parse.GetValue(harness),
            parse.GetValue(baseRef), parse.GetValue(branch), parse.GetValue(note), parse.GetValue(timeout), parse.GetValue(parent), parse.GetValue(defaultBranch)), processes, ct));
        worker.Subcommands.Add(run);
        var reservations = new Command("reservations", "List active full-worker reservations owned by this caller.");
        reservations.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.WorkerReservations, ct)));
        worker.Subcommands.Add(reservations);
        var reservationId = new Argument<string>("id");
        var confirmed = new Option<bool>("--cleanup-confirmed") { Description = "Attest that the entire reserved process tree is gone." };
        var release = new Command("release", "Recover a full-worker reservation only after verifying process cleanup.") { reservationId, confirmed };
        release.SetAction(async (parse, ct) => !parse.GetValue(confirmed)
            ? Output.Error("worker_cleanup_unconfirmed", "Explicit --cleanup-confirmed is required.", ExitCodes.RuleViolation)
            : Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.WorkerRelease,
                new WorkerReleaseRequest(parse.GetValue(reservationId)!, true), MuthurJsonContext.Default.WorkerReleaseRequest, ct)));
        worker.Subcommands.Add(release);
    }

    private static void AddHarness(RootCommand root)
    {
        var harness = new Command("harness", "Which harness, model and account staffs each tier; accounts out of quota.");
        root.Subcommands.Add(harness);

        var tier = new Option<string?>("--tier");
        var tiers = new Command("tiers", "Show the tier catalog with current account limits. Edit it in {MUTHUR_HOME}/harnesses.json.") { tier };
        tiers.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(
            Routes.Tiers + (parse.GetValue(tier) is { } t ? "?tier=" + Uri.EscapeDataString(t) : ""), ct)));
        harness.Subcommands.Add(tiers);

        var account = new Argument<string>("account");
        var minutes = new Option<int?>("--minutes");
        var clear = new Option<bool>("--clear");
        var limit = new Command("limit", "Mark an account as out of quota (or --clear it) so work is routed to the next candidate.") { account, minutes, clear };
        limit.SetAction(async (parse, ct) =>
        {
            DateTimeOffset? until = parse.GetValue(clear) ? null : DateTimeOffset.UtcNow.AddMinutes(parse.GetValue(minutes) ?? 60);
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.AccountLimits,
                new AccountLimitRequest(parse.GetValue(account)!, until), MuthurJsonContext.Default.AccountLimitRequest, ct));
        });
        harness.Subcommands.Add(limit);

        AddConductor(root);
    }

    /// <summary>The conductor staffs validation so a task that passes needs nobody awake.</summary>
    private static void AddConductor(RootCommand root)
    {
        var conductor = new Command("conductor", "Staffs validator sessions for tasks waiting on validation. Off until the founder turns it on.");
        root.Subcommands.Add(conductor);

        var status = new Command("status", "Whether the conductor is staffing, how many sessions it has running, and its limits.");
        status.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Conductor, ct)));
        conductor.Subcommands.Add(status);

        foreach (var (name, enabled, description) in new[]
                 {
                     ("on", true, "Let the conductor start validator sessions. Needs --founder; recorded in the ledger."),
                     ("off", false, "Stop staffing. Sessions already running are left to finish. Needs --founder."),
                 })
        {
            var command = new Command(name, description);
            command.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
                Routes.Conductor, new ConductorSwitch(enabled), MuthurJsonContext.Default.ConductorSwitch, ct)));
            conductor.Subcommands.Add(command);
        }

        // The half that begins new work rather than finishing it, and its own switch because turning it on starts spending.
        var orchestrators = new Command("orchestrators", "Whether the conductor also starts orchestrator sessions for backlog tasks. Off until the founder turns it on.");
        conductor.Subcommands.Add(orchestrators);
        foreach (var (name, enabled, description) in new[]
                 {
                     ("on", true, "Let the conductor start an orchestrator for a task nobody has claimed. Needs --founder; recorded in the ledger."),
                     ("off", false, "Stop starting orchestrators. Sessions already running are left to finish. Needs --founder."),
                 })
        {
            var command = new Command(name, description);
            command.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
                Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(enabled), MuthurJsonContext.Default.ConductorOrchestratorSwitch, ct)));
            orchestrators.Subcommands.Add(command);
        }

        var count = new Argument<int>("count") { Description = "Sessions the conductor may run at once." };
        var sessions = new Command("sessions", "Raise or lower how many sessions the conductor may run at once. Needs --founder.") { count };
        sessions.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.ConductorSessions, SessionsRequest(parse.GetValue(count)), MuthurJsonContext.Default.ConductorSessionsRequest, ct)));
        conductor.Subcommands.Add(sessions);

        var from = new Option<string?>("--from") { Description = "When the window opens, HH:mm in local time." };
        var to = new Option<string?>("--to") { Description = "When it closes, HH:mm in local time. Earlier than --from means it crosses midnight." };
        var inWindow = new Option<int?>("--sessions") { Description = "Sessions allowed while the window is open." };
        var clear = new Option<bool>("--clear") { Description = "Drop the window and the standing ceiling with it." };
        var unattended = new Command("unattended", "Cap sessions during the hours nobody is watching. Needs --founder.")
            { from, to, inWindow, clear };
        unattended.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.ConductorSessions,
            UnattendedRequest(parse.GetValue(from), parse.GetValue(to), parse.GetValue(inWindow), parse.GetValue(clear)),
            MuthurJsonContext.Default.ConductorSessionsRequest, ct)));
        conductor.Subcommands.Add(unattended);
    }

    /// <summary>
    /// What the two ceiling commands put on the wire. Pulled out of the actions because the mistake worth catching
    /// is a value reaching the wrong field: the window's <c>--sessions</c> is not the standing ceiling, and the hub
    /// would happily obey either. The hub decides what is legal; neither of these judges anything.
    /// </summary>
    public static ConductorSessionsRequest SessionsRequest(int sessions) => new(sessions, null, null, null, Clear: false);

    /// <inheritdoc cref="SessionsRequest"/>
    public static ConductorSessionsRequest UnattendedRequest(string? from, string? to, int? sessions, bool clear) =>
        new(null, from, to, sessions, clear);

    private sealed record RunOptions(string Tier, string Spec, string? Unit, string? Task, string? Harness, string? Base, string? Branch, string? Note, int TimeoutMinutes, string? Parent = null, string? DefaultBranch = null);

    /// <summary>
    /// The contract this tier is handed. A mastermind is given a problem area, not a frozen unit, so handing it
    /// the implementer's "do not redesign" contract alone is the opposite of why it was staffed: it gets the
    /// specialist procedure on top. Kit includes are expanded at install time and not on this read, so
    /// specialist.md carries no {{core:...}} token and the two procedures are composed here instead.
    /// </summary>
    internal static async Task<string> ReadContractAsync(string kit, string tier, CancellationToken ct)
    {
        var implementer = await File.ReadAllTextAsync(Path.Combine(kit, "core", "implementer.md"), ct);
        return tier.Equals("mastermind", StringComparison.OrdinalIgnoreCase)
            ? await File.ReadAllTextAsync(Path.Combine(kit, "core", "specialist.md"), ct) + "\n\n" + implementer
            : implementer;
    }

    internal static async Task<string> ResolveBaseAsync(IProcessRunner processes, string repository, string? explicitBase, CancellationToken ct)
    {
        if (explicitBase is not null) return explicitBase;
        var result = await processes.RunAsync("git", ["branch", "--show-current"], repository, timeout: TimeSpan.FromMinutes(1), ct: ct);
        return result.Ok ? result.StdOut.Trim() : "HEAD";
    }

    private static async Task<int> RunAsync(ParseResult parse, RunOptions o, IProcessRunner processes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Task))
            return Output.Error("worker_request_invalid", "Full-worker dispatch requires --task, including legacy specs.", ExitCodes.RuleViolation);
        var reporting = false;
        async Task<string?> Git(string directory, params string[] arguments)
        {
            try
            {
                var result = await processes.RunAsync("git", arguments, directory, timeout: TimeSpan.FromMinutes(1), ct: ct);
                return result.Ok ? result.StdOut.Trim() : null;
            }
            catch (OperationCanceledException) when (reporting) { return null; }
        }

        if (await Git(Environment.CurrentDirectory, "rev-parse", "--show-toplevel") is not { } repo)
            return Output.Error("not_a_repository", "Run this inside the project's git repository.", ExitCodes.RuleViolation);
        repo = Path.GetFullPath(repo);
        var gitCommon = Path.GetFullPath(Path.Combine(repo, await Git(repo, "rev-parse", "--git-common-dir") ?? ".git"));
        var baseRef = await ResolveBaseAsync(processes, repo, o.Base, ct);

        // 1. Who can staff this tier right now?
        var hub = HubClient.For(parse);
        var tiers = await hub.GetAsync($"{Routes.Tiers}?tier={Uri.EscapeDataString(o.Tier)}", ct);
        if (!tiers.IsSuccess) return Output.Emit(parse, tiers);
        var catalog = JsonSerializer.Deserialize(tiers.Body, MuthurJsonContext.Default.IReadOnlyListTierDto) ?? [];
        var candidates = Candidates(catalog, o.Harness);
        var local = o.Tier.Equals("local-implementer", StringComparison.OrdinalIgnoreCase) ||
            o.Tier.Equals("utility", StringComparison.OrdinalIgnoreCase);
        if (local) candidates = [.. candidates.Where(c => c.Account == "local").Take(1)];
        if (candidates.Count == 0)
            return Output.Error("no_candidates", $"No available candidate for tier '{o.Tier}'" + (o.Harness is null ? "" : $" on harness '{o.Harness}'") +
                ". Every account may be limited: muthur harness tiers", ExitCodes.RuleViolation);

        using var localLease = new DeferredLocalLease(local);

        // 2. The contract and the project's verification commands.
        if (KitCommands.LocateKit() is not { } kit) return KitCommands.KitMissing();
        var contract = await ReadContractAsync(kit, o.Tier, ct);
        var (verify, extraAllowed) = ReadProject(repo);

        // 3. A worktree and branch of the worker's own.
        var id = Guid.NewGuid().ToString("n")[..6];
        var branchName = o.Branch ?? $"worker/{Slug(o.Task ?? Path.GetFileNameWithoutExtension(o.Spec))}-{Slug(o.Unit ?? "all")}-{id}";
        var worktree = Path.Combine(repo, ".worktrees", branchName.Replace('/', '-'));
        var defaultName = o.DefaultBranch;
        if (defaultName is null)
        {
            var projectKey = ProjectContext.FindKey(repo);
            if (o.Task is { } taskKey)
            {
                var detail = await hub.GetAsync(Routes.Task(taskKey), ct);
                if (!detail.IsSuccess) return Output.Emit(parse, detail);
                projectKey = JsonSerializer.Deserialize(detail.Body, MuthurJsonContext.Default.TaskDetailDto)?.Task.Project;
            }
            if (projectKey is { Length: > 0 })
            {
                var project = await hub.GetAsync(Routes.Project(projectKey), ct);
                if (!project.IsSuccess) return Output.Emit(parse, project);
                defaultName = JsonSerializer.Deserialize(project.Body, MuthurJsonContext.Default.ProjectDto)?.DefaultBranch;
            }
        }
        if (string.IsNullOrWhiteSpace(defaultName))
            return Output.Error("default_branch_required", "Register this project or provide --default-branch with its named local default branch.", ExitCodes.RuleViolation);
        WorkerAssignment assignment;
        try { assignment = await WorkerAssignment.ResolveAsync(processes, repo, baseRef, defaultName, o.Spec, branchName, worktree, ct); }
        catch (WorkerDispatchException ex) { return Output.Error(ex.Code, ex.Message, ExitCodes.RuleViolation); }
        IReadOnlyList<string> requirements;
        string workKind;
        try
        {
            var frozen = await Git(repo, "cat-file", "blob", assignment.SpecBlob)
                ?? throw new WorkerDispatchException("spec_unreadable", "The pinned spec blob cannot be read.");
            requirements = CapabilityRequirements.Parse(frozen);
            workKind = RoutingPolicy.WorkKind(frozen);
            if (requirements.Count > 0) (verify, extraAllowed) = await ReadPinnedProjectAsync(processes, repo, assignment.BaseCommit, ct);
        }
        catch (WorkerDispatchException ex) { return Output.Error(ex.Code, ex.Message, ExitCodes.RuleViolation); }
        var scratch = Path.Combine(MuthurEnvironment.Home, "workers", id);
        var contained = new WorkerProcessRunner();
        var setup = new WorkerSetupRunner(contained);
        var prepared = false;
        async Task Prepare(CancellationToken token)
        {
            if (prepared) return;
            localLease.Acquire();
            var git = ExecutableResolver.Resolve("git") ?? throw new WorkerDispatchException("worktree_failed", "Git is unavailable.");
            var added = await setup.RunAsync(git.FileName, [.. git.Prefix, "worktree", "add", "-b", branchName, worktree, assignment.BaseCommit], repo,
                timeout: TimeSpan.FromMinutes(2), ct: token, environment: SessionWorkspace.GitEnvironment(repo));
            if (!added.Ok) throw new WorkerDispatchException("worktree_failed", added.Message);
            await assignment.VerifyCreatedAsync(setup, token);
            Directory.CreateDirectory(scratch);
            prepared = true;
        }
        string PromptFor(HarnessCandidate c)
        {
            var notes = string.Join(" ", new[] { o.Note, Harnesses.Find(c.Harness)?.WorkerNote }.Where(n => n is { Length: > 0 }));
            return WorkerPrompt.Compose(contract, assignment.SpecPath, o.Unit, branchName, verify, notes, assignment);
        }

        // 4. Run, falling through candidates whose account turns out to be exhausted. A candidate whose sandbox
        // identity is known to fail the build is refused here, before any seat is reserved, and the report says why.
        var (usable, refused) = WorkerEnvironment.Partition(candidates);
        IReadOnlyList<WorkerAttempt> attempts = usable.Count == 0 ? refused : [.. refused, .. await new WorkerLauncher(processes, workerProcesses: contained,
            admission: new(new WorkerAdmissionClient(hub), o.Task!, o.Tier, repo, assignment.BaseCommit, assignment.SpecBlob,
                assignment.SpecPath, o.Unit, o.Parent, branchName), prepare: Prepare, setupCleanupConfirmed: () => setup.CleanupConfirmed).RunAsync(
            usable,
            c => RequestFor(c, worktree, PromptFor(c), gitCommon, [.. DefaultAllowed, .. extraAllowed], scratch) with
            {
                Capabilities = new(requirements, "worker-run", Path.Combine(MuthurEnvironment.Home, "capabilities"), assignment.BaseCommit, repo),
            },
            TimeSpan.FromMinutes(local ? Math.Min(o.TimeoutMinutes, 10) : o.TimeoutMinutes),
            async c =>
            {
                if (c.Account is { Length: > 0 } account)
                {
                    var limited = await hub.PostAsync(Routes.AccountLimits, new AccountLimitRequest(account, DateTimeOffset.UtcNow.AddHours(1)), MuthurJsonContext.Default.AccountLimitRequest, ct);
                    if (!limited.IsSuccess) throw new InvalidOperationException(limited.Body);
                }
            },
            ct)];

        var executionCancelled = ct.IsCancellationRequested;
        using var reportBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ct = reportBudget.Token;
        reporting = true;
        var final = attempts[^1];

        // Some sandboxes keep .git read-only, and any worker can forget: whatever is left uncommitted is committed here,
        // so the orchestrator always reviews a branch, never a dirty directory.
        var committedByLauncher = false;
        var canInspect = prepared && !executionCancelled && final.Cleanup != WorkerCleanup.CleanupUncertain;
        if (canInspect && (await Git(worktree, "status", "--porcelain"))?.Length > 0)
        {
            await Git(worktree, "add", "-A");
            var subject = $"{o.Task ?? Path.GetFileNameWithoutExtension(o.Spec)}{(o.Unit is null ? "" : " " + o.Unit)}";
            committedByLauncher = await Git(worktree, "commit", "-q", "-m", $"{subject}: worker output ({final.Candidate.Harness}/{(final.Candidate.Model.Length > 0 ? final.Candidate.Model : "default")})") is not null;
        }

        // A harness that exits cleanly has not necessarily done the work: the report's own STATUS line decides.
        var status = WorkerReport.Status(final.Outcome.Report);
        var success = final.Outcome.Success && status is null or "done";
        var commits = ((canInspect ? await Git(worktree, "log", "--oneline", $"{assignment.BaseCommit}..HEAD") : null) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headCommit = canInspect ? await Git(worktree, "rev-parse", "HEAD") : null;
        var failureKind = final.FailureKind ?? (!final.Started ? "launch_unavailable" : status is "blocked" ? "worker_blocked" :
            status is "spec-problem" ? "spec_problem" : !success ? "worker_failed" : null);

        string? reportError = null;
        try
        {
            var reported = await hub.PostAsync(Routes.WorkerRuns, new WorkerRunReport(o.Task, o.Tier, final.Candidate.Harness, final.Candidate.Model, final.Candidate.Account,
            branchName, o.Unit, success, (int)final.Duration.TotalSeconds, final.Outcome.CostUsd, o.Parent,
            InputTokens: final.Outcome.InputTokens, OutputTokens: final.Outcome.OutputTokens,
            CacheReadTokens: final.Outcome.CacheReadTokens, TotalTokens: final.Outcome.TotalTokens,
            RunId: final.RunId, Status: status, FailureKind: failureKind, BaseCommit: assignment.BaseCommit, HeadCommit: headCommit,
            SpecBlob: assignment.SpecBlob, ExitCode: final.ExitCode, WorkKind: workKind, PolicyVersion: "catalog-order-v1", ReasoningEffort: final.Candidate.ReasoningEffort,
            Attempts: attempts.Select(a => new WorkerAttemptReport(a.Candidate.Harness, a.Candidate.Model, a.Candidate.Account,
                a.RunId, a.ReservationId, a.Started, a.FailureKind, a.Cleanup?.ToString())).ToArray()), MuthurJsonContext.Default.WorkerRunReport, ct);
            if (!reported.IsSuccess) reportError = reported.Body;
        }
        catch (OperationCanceledException) { reportError = "Run reporting exceeded its separate deadline."; }
        if (reportError is not null) { success = false; failureKind ??= "worker_report_failed"; }

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteBoolean("success", success);
            json.WriteString("runId", final.RunId);
            json.WriteString("failureKind", failureKind);
            json.WriteString("reportError", reportError);
            json.WriteNumber("probeStarts", attempts.Sum(a => a.ProbeStarts));
            json.WriteNumber("fullStarts", attempts.Sum(a => a.FullStarts));
            json.WriteNumber("fullSessionsAvoided", attempts.Sum(a => a.FullSessionsAvoided));
            json.WriteStartArray("attempts");
            foreach (var attempt in attempts)
            {
                json.WriteStartObject();
                json.WriteString("harness", attempt.Candidate.Harness);
                json.WriteBoolean("started", attempt.Started);
                json.WriteString("runId", attempt.RunId);
                json.WriteString("reservationId", attempt.ReservationId);
                json.WriteString("cleanup", attempt.Cleanup?.ToString());
                json.WriteString("failureKind", attempt.FailureKind);
                json.WritePropertyName("capabilityMatch");
                JsonSerializer.Serialize(json, attempt.CapabilityMatch, CapabilityJsonContext.Default.CapabilityMatch);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteString("baseBranch", assignment.BaseBranch);
            json.WriteString("baseCommit", assignment.BaseCommit);
            json.WriteString("defaultBranch", assignment.DefaultBranch);
            json.WriteString("specBlob", assignment.SpecBlob);
            json.WriteString("headCommit", headCommit);
            if (status is not null) json.WriteString("status", status);
            if (committedByLauncher) json.WriteBoolean("committedByLauncher", true);
            json.WriteString("harness", final.Candidate.Harness);
            json.WriteString("model", final.Candidate.Model);
            json.WriteString("branch", branchName);
            json.WriteString("worktree", worktree);
            json.WriteStartArray("commits");
            foreach (var commit in commits) json.WriteStringValue(commit);
            json.WriteEndArray();
            json.WriteNumber("seconds", (int)final.Duration.TotalSeconds);
            if (final.Outcome.CostUsd is { } cost) json.WriteNumber("costUsd", cost);
            json.WriteStartArray("skipped");
            foreach (var attempt in attempts.SkipLast(1))
                json.WriteStringValue($"{attempt.Candidate.Harness}: {(attempt.Outcome.RateLimited ? "account out of quota" : attempt.Outcome.Report)}");
            json.WriteEndArray();
            json.WriteString("report", final.Outcome.Report);
            json.WriteEndObject();
        }
        var body = Encoding.UTF8.GetString(stream.ToArray());
        return Output.Emit(parse, new ApiResult(success ? 200 : 500, body));
    }

    /// <summary>
    /// Who could staff this run, in catalog order, skipping accounts out of quota and anything --harness rules out.
    /// Everything the founder's catalog says about a candidate travels with it, reasoning effort included: a tier
    /// entry that is honoured for validator sessions and quietly dropped for workers is a knob that lies.
    /// </summary>
    internal static IReadOnlyList<HarnessCandidate> Candidates(IReadOnlyList<TierDto> catalog, string? harness) =>
        [.. catalog.SelectMany(t => t.Candidates)
            .Where(c => !c.Limited && (harness is null || string.Equals(c.Harness, harness, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new HarnessCandidate(c.Harness, c.Model, c.Account, c.ReasoningEffort, c.MaxTurns))];

    /// <summary>What one candidate is asked to do. The deny list is this command's own policy, never the project's.</summary>
    internal static WorkerRequest RequestFor(HarnessCandidate candidate, string worktree, string prompt,
        string? gitCommon, IReadOnlyList<string> allowed, string scratch) =>
        new(worktree, prompt, candidate.Model, gitCommon, allowed, Denied, scratch, candidate.ReasoningEffort, MaxTurns: candidate.MaxTurns);

    /// <summary>Build/test commands and extra allowed shell commands from muthur.project.json.</summary>
    internal static (List<string> Verify, List<string> Allowed) ReadProject(string repo)
    {
        var verify = new List<string>();
        var allowed = new List<string>();
        var file = Path.Combine(repo, ProjectContext.FileName);
        if (!File.Exists(file)) return (verify, allowed);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var name in (string[])["build", "test"])
                if (doc.RootElement.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } command)
                    verify.Add(command);
            if (doc.RootElement.TryGetProperty("workerAllowedCommands", out var list) && list.ValueKind == JsonValueKind.Array)
                allowed.AddRange(list.EnumerateArray().Select(x => x.GetString()).OfType<string>());
        }
        catch (JsonException) { }
        return (verify, allowed);
    }

    internal static async Task<(List<string> Verify, List<string> Allowed)> ReadPinnedProjectAsync(
        IProcessRunner processes, string repo, string commit, CancellationToken ct)
    {
        var entry = await processes.RunAsync("git", ["ls-tree", "--name-only", commit, "--", ProjectContext.FileName], repo,
            timeout: TimeSpan.FromSeconds(5), ct: ct);
        if (!entry.Ok) throw new WorkerDispatchException("capability_identity_unknown", "Cannot resolve pinned project settings.");
        if (string.IsNullOrWhiteSpace(entry.StdOut)) return ([], []);
        var blob = await processes.RunAsync("git", ["show", commit + ":" + ProjectContext.FileName], repo,
            timeout: TimeSpan.FromSeconds(5), ct: ct);
        if (!blob.Ok || blob.StdOut.Length > 1_048_576)
            throw new WorkerDispatchException("capability_identity_unknown", "Pinned project settings are unreadable or exceed the limit.");
        try
        {
            using var document = JsonDocument.Parse(blob.StdOut);
            var verify = new List<string>();
            var allowed = new List<string>();
            foreach (var name in new[] { "build", "test" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } command) verify.Add(command);
            if (document.RootElement.TryGetProperty("workerAllowedCommands", out var commands))
                foreach (var command in commands.EnumerateArray()) allowed.Add(command.GetString() ?? throw new JsonException());
            return (verify, allowed);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { throw new WorkerDispatchException("capability_identity_unknown", "Pinned project settings are malformed."); }
    }

    private sealed class DeferredLocalLease(bool local) : IDisposable
    {
        private FileStream? _lease;
        public void Acquire() { if (local && _lease is null) _lease = LocalInferenceLease.Acquire(MuthurEnvironment.Home); }
        public void Dispose() => _lease?.Dispose();
    }
    private static string Slug(string text)
    {
        var slug = new string(text.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 24 ? slug[..24].Trim('-') : slug.Length == 0 ? "x" : slug;
    }
}
