using System.CommandLine;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class TaskCommands
{
    private static Argument<string> Id() => new("id") { Description = "Task id, e.g. T-12." };

    public static void AddTo(RootCommand root)
    {
        var task = new Command("task", "The task ledger: add, claim, and move work through its lifecycle.");
        root.Subcommands.Add(task);

        var project = new Option<string?>("--project") { Description = $"Project key (default: nearest {ProjectContext.FileName}, else the only project)." };

        var title = new Argument<string>("title");
        var body = new Option<string?>("--body") { Description = "Task description (markdown)." };
        var bodyFile = new Option<string?>("--body-file") { Description = "Read the description from a file." };
        var priority = new Option<int>("--priority") { Description = "Higher is more urgent; >0 shows as 'priority'." };
        var parent = new Option<string?>("--parent") { Description = "Parent task id." };
        var add = new Command("add", "Add a task to the backlog.") { title, project, body, bodyFile, priority, parent };
        add.SetAction(async (parse, ct) =>
        {
            var text = parse.GetValue(bodyFile) is { } file ? await File.ReadAllTextAsync(file, ct) : parse.GetValue(body);
            var request = new AddTaskRequest(parse.GetValue(title)!, parse.GetValue(project) ?? ProjectContext.FindKey(), text, parse.GetValue(priority), parse.GetValue(parent));
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Tasks, request, MuthurJsonContext.Default.AddTaskRequest, ct));
        });
        task.Subcommands.Add(add);

        var state = new Option<string?>("--state") { Description = "Comma-separated states: backlog,in_progress,blocked,validating,validated,done,cancelled." };
        var owner = new Option<string?>("--owner") { Description = "Only tasks owned by this agent." };
        var mine = new Option<bool>("--mine") { Description = "Only tasks I own." };
        var all = new Option<bool>("--all") { Description = "Include done and cancelled tasks." };
        var everyProject = new Option<bool>("--all-projects") { Description = "Do not default to the current directory's project." };
        var limit = new Option<int?>("--limit");
        var list = new Command("list", "List tasks (open ones by default), most urgent first.") { state, project, owner, mine, all, everyProject, limit };
        list.SetAction(async (parse, ct) =>
        {
            var query = new List<string>();
            if (parse.GetValue(state) is { } s) query.Add("state=" + Uri.EscapeDataString(s));
            else if (!parse.GetValue(all)) query.Add("open=true");
            var key = parse.GetValue(project) ?? (parse.GetValue(everyProject) ? null : ProjectContext.FindKey());
            if (key is not null) query.Add("project=" + Uri.EscapeDataString(key));
            if (parse.GetValue(owner) is { } o) query.Add("owner=" + Uri.EscapeDataString(o));
            if (parse.GetValue(mine)) query.Add("mine=true");
            if (parse.GetValue(limit) is { } l) query.Add("limit=" + l);
            return Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Tasks + "?" + string.Join('&', query), ct));
        });
        task.Subcommands.Add(list);

        var showId = Id();
        var show = new Command("show", "Show a task with its full event history.") { showId };
        show.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Task(parse.GetValue(showId)!), ct)));
        task.Subcommands.Add(show);

        var claimId = Id();
        var lease = new Option<int?>("--lease") { Description = "Lease in minutes (default 30). Any hub call you make renews it." };
        var claim = new Command("claim", "Claim a backlog task (or take over one whose claim lapsed). Exit 3 if someone else holds it.") { claimId, lease };
        claim.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.TaskAction(parse.GetValue(claimId)!, "claim"), new ClaimTaskRequest(parse.GetValue(lease)), MuthurJsonContext.Default.ClaimTaskRequest, ct)));
        task.Subcommands.Add(claim);

        var releaseId = Id();
        var reason = new Option<string?>("--reason");
        var release = new Command("release", "Give a task back to the backlog.") { releaseId, reason };
        release.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.TaskAction(parse.GetValue(releaseId)!, "release"), new ReleaseTaskRequest(parse.GetValue(reason)), MuthurJsonContext.Default.ReleaseTaskRequest, ct)));
        task.Subcommands.Add(release);

        var specId = Id();
        var specPath = new Argument<string>("path") { Description = "Spec file path relative to the repository root, e.g. specs/T-12.md." };
        var spec = new Command("spec", "Attach the frozen spec to a task you own.") { specId, specPath };
        spec.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.TaskAction(parse.GetValue(specId)!, "spec"), new SetSpecRequest(parse.GetValue(specPath)!), MuthurJsonContext.Default.SetSpecRequest, ct)));
        task.Subcommands.Add(spec);

        var priorityId = Id();
        var priorityValue = new Argument<int>("priority");
        var setPriority = new Command("priority", "Change a task's priority.") { priorityId, priorityValue };
        setPriority.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.TaskAction(parse.GetValue(priorityId)!, "priority"), new SetPriorityRequest(parse.GetValue(priorityValue)), MuthurJsonContext.Default.SetPriorityRequest, ct)));
        task.Subcommands.Add(setPriority);

        var attendedId = Id();
        var attendedReason = new Option<string?>("--reason") { Description = "Why a human is needed: a browser, a device, your hands. Required unless --clear." };
        var attendedClear = new Option<bool>("--clear") { Description = "Lift the flag. The conductor staffs the task again." };
        var attended = new Command("attended", "Say that this task needs a human validator, and why. --clear lifts it when the reason stops being true.")
            { attendedId, attendedReason, attendedClear };
        attended.SetAction(async (parse, ct) =>
        {
            var reason = parse.GetValue(attendedReason);
            var clear = parse.GetValue(attendedClear);
            if (AttendedRefusal(parse.GetResult(attendedReason) is not null, reason, clear) is { } refusal)
                return Output.Error(refusal.Code, refusal.Message, ExitCodes.RuleViolation);
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(
                Routes.TaskAction(parse.GetValue(attendedId)!, "attended"),
                new AttendedRequest(clear ? null : reason!.Trim()), MuthurJsonContext.Default.AttendedRequest, ct));
        });
        task.Subcommands.Add(attended);

        var cancelId = Id();
        var cancelReason = new Option<string?>("--reason");
        var cancel = new Command("cancel", "Cancel a task.") { cancelId, cancelReason };
        cancel.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.TaskAction(parse.GetValue(cancelId)!, "cancel"), new CancelTaskRequest(parse.GetValue(cancelReason)), MuthurJsonContext.Default.CancelTaskRequest, ct)));
        task.Subcommands.Add(cancel);

        var reopenId = Id();
        var reopen = new Command("reopen", "Return a cancelled task to the backlog.") { reopenId };
        reopen.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.TaskAction(parse.GetValue(reopenId)!, "reopen"), ct)));
        task.Subcommands.Add(reopen);

        var implementedId = Id();
        var branch = new Option<string>("--branch") { Description = "The task branch holding the finished work.", Required = true };
        var implemented = new Command("implemented", "Declare the work built, reviewed and tested; hands the task to its validators.") { implementedId, branch };
        implemented.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.TaskAction(parse.GetValue(implementedId)!, "implemented"), new ImplementedRequest(parse.GetValue(branch)!), MuthurJsonContext.Default.ImplementedRequest, ct)));
        task.Subcommands.Add(implemented);

        var landId = Id();
        var land = new Command("land", "Land a validated task: MUTHUR merges the branch (or opens the pull request). Exit 3 on merge conflict.") { landId };
        land.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse, TimeSpan.FromMinutes(5)).PostAsync(Routes.TaskAction(parse.GetValue(landId)!, "land"), ct)));
        task.Subcommands.Add(land);

        var logTask = new Option<string?>("--task") { Description = "Only events of this task." };
        var since = new Option<long?>("--since") { Description = "Only events after this sequence number." };
        var logLimit = new Option<int?>("--limit");
        var log = new Command("log", "Read the ledger: the latest events, or everything after --since.") { logTask, since, logLimit };
        log.SetAction(async (parse, ct) =>
        {
            var query = new List<string>();
            if (parse.GetValue(logTask) is { } t) query.Add("task=" + Uri.EscapeDataString(t));
            if (parse.GetValue(since) is { } s) query.Add("since=" + s);
            if (parse.GetValue(logLimit) is { } l) query.Add("limit=" + l);
            return Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Events + "?" + string.Join('&', query), ct));
        });
        root.Subcommands.Add(log);
    }

    /// <summary>
    /// Why `task attended` will not be sent, or null when the flags are usable. The hub cannot tell "clear" from
    /// "set, but the reason was left out" — both arrive as a null reason — so the refusal belongs here, before a
    /// round trip that would silently lift the flag instead. It branches on whether --reason was *supplied*, never
    /// on whether it has content: `--reason "   " --clear` is a contradiction to refuse, not a clear to obey.
    /// </summary>
    public static (string Code, string Message)? AttendedRefusal(bool reasonSupplied, string? reason, bool clear) =>
        (reasonSupplied, clear) switch
        {
            (true, true) => ("reason_required", "Pass --reason or --clear, not both."),
            (true, false) when !string.IsNullOrWhiteSpace(reason) => null,
            (false, true) => null,
            _ => ("reason_required", "Say why this task needs a human: --reason \"<why>\", or --clear to lift it."),
        };
}
