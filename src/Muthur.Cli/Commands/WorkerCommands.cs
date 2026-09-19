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
    private static readonly string[] DefaultAllowed =
    [
        "dotnet *", "npm *", "node *", "git status*", "git add *", "git commit *", "git diff*", "git log*", "git show*",
        "git branch --show-current", "ls*", "cat *",
    ];

    private static readonly string[] Denied =
        ["muthur *", "muthur.exe *", "git push*", "git merge*", "git rebase*", "git checkout*", "git switch*", "git worktree*", "gh *"];

    public static void AddTo(RootCommand root)
    {
        AddHarness(root);

        var worker = new Command("worker", "Headless workers on any harness (claude, codex, local models).");
        root.Subcommands.Add(worker);

        var tier = new Option<string>("--tier") { Description = "mastermind | implementer | utility", DefaultValueFactory = _ => "implementer" };
        var spec = new Option<string>("--spec") { Description = "Frozen spec, relative to the repository root. Must be committed on the base branch.", Required = true };
        var unit = new Option<string?>("--unit") { Description = "The unit of the spec this worker owns (default: the whole spec)." };
        var task = new Option<string?>("--task") { Description = "Task id, recorded in the ledger with the run." };
        var harness = new Option<string?>("--harness") { Description = "Only use this harness from the tier's candidates." };
        var baseRef = new Option<string?>("--base") { Description = "Branch the worker starts from (default: the current branch)." };
        var branch = new Option<string?>("--branch") { Description = "Branch to create for the worker (default: worker/<task>-<unit>-<id>)." };
        var note = new Option<string?>("--note") { Description = "One extra instruction for the worker." };
        var timeout = new Option<int>("--timeout-minutes") { DefaultValueFactory = _ => 60 };
        var run = new Command("run", "Run one worker in its own worktree and print its report. The worker gets no hub identity.")
            { tier, spec, unit, task, harness, baseRef, branch, note, timeout };
        run.SetAction((parse, ct) => RunAsync(parse, new RunOptions(
            parse.GetValue(tier)!, parse.GetValue(spec)!, parse.GetValue(unit), parse.GetValue(task), parse.GetValue(harness),
            parse.GetValue(baseRef), parse.GetValue(branch), parse.GetValue(note), parse.GetValue(timeout)), ct));
        worker.Subcommands.Add(run);
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

    private sealed record RunOptions(string Tier, string Spec, string? Unit, string? Task, string? Harness, string? Base, string? Branch, string? Note, int TimeoutMinutes);

    private static async Task<int> RunAsync(ParseResult parse, RunOptions o, CancellationToken ct)
    {
        var processes = new ProcessRunner();
        async Task<string?> Git(string directory, params string[] arguments)
        {
            var result = await processes.RunAsync("git", arguments, directory, timeout: TimeSpan.FromMinutes(1), ct: ct);
            return result.Ok ? result.StdOut.Trim() : null;
        }

        if (await Git(Environment.CurrentDirectory, "rev-parse", "--show-toplevel") is not { } repo)
            return Output.Error("not_a_repository", "Run this inside the project's git repository.", ExitCodes.RuleViolation);
        repo = Path.GetFullPath(repo);
        var gitCommon = Path.GetFullPath(Path.Combine(repo, await Git(repo, "rev-parse", "--git-common-dir") ?? ".git"));
        var baseRef = o.Base ?? await Git(repo, "branch", "--show-current") ?? "HEAD";

        // 1. Who can staff this tier right now?
        var hub = HubClient.For(parse);
        var tiers = await hub.GetAsync($"{Routes.Tiers}?tier={Uri.EscapeDataString(o.Tier)}", ct);
        if (!tiers.IsSuccess) return Output.Emit(parse, tiers);
        var catalog = JsonSerializer.Deserialize(tiers.Body, MuthurJsonContext.Default.IReadOnlyListTierDto) ?? [];
        var candidates = catalog.SelectMany(t => t.Candidates)
            .Where(c => !c.Limited && (o.Harness is null || string.Equals(c.Harness, o.Harness, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new HarnessCandidate(c.Harness, c.Model, c.Account))
            .ToList();
        if (candidates.Count == 0)
            return Output.Error("no_candidates", $"No available candidate for tier '{o.Tier}'" + (o.Harness is null ? "" : $" on harness '{o.Harness}'") +
                ". Every account may be limited: muthur harness tiers", ExitCodes.RuleViolation);

        // 2. The contract and the project's verification commands.
        if (KitCommands.LocateKit() is not { } kit) return KitCommands.KitMissing();
        var contract = await File.ReadAllTextAsync(Path.Combine(kit, "core", "implementer.md"), ct);
        var (verify, extraAllowed) = ReadProject(repo);

        // 3. A worktree and branch of the worker's own.
        var id = Guid.NewGuid().ToString("n")[..6];
        var branchName = o.Branch ?? $"worker/{Slug(o.Task ?? Path.GetFileNameWithoutExtension(o.Spec))}-{Slug(o.Unit ?? "all")}-{id}";
        var worktree = Path.Combine(repo, ".worktrees", branchName.Replace('/', '-'));
        // Check the spec on the base ref before creating anything, so a refused run leaves no worktree or branch behind.
        if (await Git(repo, "cat-file", "-e", $"{baseRef}:{o.Spec.Replace('\\', '/')}") is null)
            return Output.Error("spec_not_committed", $"'{o.Spec}' does not exist on '{baseRef}'. Commit the frozen spec before delegating.", ExitCodes.RuleViolation);
        var added = await processes.RunAsync("git", ["worktree", "add", "-b", branchName, worktree, baseRef], repo, timeout: TimeSpan.FromMinutes(2), ct: ct);
        if (!added.Ok) return Output.Error("worktree_failed", added.Message);
        if (!File.Exists(Path.Combine(worktree, o.Spec)))
            return Output.Error("spec_not_committed", $"'{o.Spec}' does not exist on '{baseRef}'. Commit the frozen spec before delegating.", ExitCodes.RuleViolation);

        var scratch = Path.Combine(MuthurEnvironment.Home, "workers", id);
        Directory.CreateDirectory(scratch);
        string PromptFor(HarnessCandidate c)
        {
            var notes = string.Join(" ", new[] { o.Note, Harnesses.Find(c.Harness)?.WorkerNote }.Where(n => n is { Length: > 0 }));
            return WorkerPrompt.Compose(contract, o.Spec.Replace('\\', '/'), o.Unit, branchName, verify, notes);
        }

        // 4. Run, falling through candidates whose account turns out to be exhausted.
        var attempts = await new WorkerLauncher(processes).RunAsync(
            candidates,
            c => new WorkerRequest(worktree, PromptFor(c), c.Model, gitCommon, [.. DefaultAllowed, .. extraAllowed], Denied, scratch),
            TimeSpan.FromMinutes(o.TimeoutMinutes),
            async c =>
            {
                if (c.Account is { Length: > 0 } account)
                    await hub.PostAsync(Routes.AccountLimits, new AccountLimitRequest(account, DateTimeOffset.UtcNow.AddHours(1)), MuthurJsonContext.Default.AccountLimitRequest, ct);
            },
            ct);

        var final = attempts[^1];

        // Some sandboxes keep .git read-only, and any worker can forget: whatever is left uncommitted is committed here,
        // so the orchestrator always reviews a branch, never a dirty directory.
        var committedByLauncher = false;
        if ((await Git(worktree, "status", "--porcelain"))?.Length > 0)
        {
            await Git(worktree, "add", "-A");
            var subject = $"{o.Task ?? Path.GetFileNameWithoutExtension(o.Spec)}{(o.Unit is null ? "" : " " + o.Unit)}";
            committedByLauncher = await Git(worktree, "commit", "-q", "-m", $"{subject}: worker output ({final.Candidate.Harness}/{(final.Candidate.Model.Length > 0 ? final.Candidate.Model : "default")})") is not null;
        }

        // A harness that exits cleanly has not necessarily done the work: the report's own STATUS line decides.
        var status = WorkerReport.Status(final.Outcome.Report);
        var success = final.Outcome.Success && status is null or "done";
        var commits = (await Git(worktree, "log", "--oneline", $"{baseRef}..HEAD") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await hub.PostAsync(Routes.WorkerRuns, new WorkerRunReport(o.Task, o.Tier, final.Candidate.Harness, final.Candidate.Model, final.Candidate.Account,
            branchName, o.Unit, success, (int)final.Duration.TotalSeconds, final.Outcome.CostUsd), MuthurJsonContext.Default.WorkerRunReport, ct);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteBoolean("success", success);
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

    /// <summary>Build/test commands and extra allowed shell commands from muthur.project.json.</summary>
    private static (List<string> Verify, List<string> Allowed) ReadProject(string repo)
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

    private static string Slug(string text)
    {
        var slug = new string(text.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 24 ? slug[..24].Trim('-') : slug.Length == 0 ? "x" : slug;
    }
}
