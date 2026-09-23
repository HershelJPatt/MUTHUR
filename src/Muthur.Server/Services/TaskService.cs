using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed record TaskQuery(IReadOnlyList<TaskState>? States = null, string? Project = null, string? Owner = null, bool OpenOnly = false, int Limit = 500);

public sealed partial class TaskService(Ledger ledger, LeasePolicy leases, ITaskLander lander, MuthurOptions options)
{
    [GeneratedRegex(@"^#\s*(T-\d+)\b")]
    private static partial Regex SpecHeadingPattern();

    /// <summary>A declared need, with any leading list marker, quote marker or emphasis: <c>- **needs:** browser</c>.</summary>
    [GeneratedRegex(@"^[\s>*_`-]*needs:[\s*_`]*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecNeedsPattern();

    public Task<TaskDto> AddAsync(Caller caller, AddTaskRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (string.IsNullOrWhiteSpace(request.Title))
            throw Fail.Rule("title_required", "A task needs a title.");

        return ledger.MutateAsync(caller, async m =>
        {
            var project = await ProjectService.FindAsync(m.Db, request.Project, ct);
            int? parentId = null;
            if (request.Parent is { Length: > 0 } parent)
                parentId = (await LoadAsync(m.Db, parent, ct)).Id;

            var task = new WorkTask
            {
                ProjectId = project.Id,
                Project = project,
                Title = request.Title.Trim(),
                Body = request.Body ?? "",
                State = TaskState.Backlog,
                Priority = request.Priority,
                ParentId = parentId,
                CreatedAt = m.Now,
                UpdatedAt = m.Now,
            };
            m.Db.Tasks.Add(task);
            await m.Db.SaveChangesAsync(ct); // assigns the sequential id the event needs
            m.Record("task.added", task.Id, new { task.Title, project = project.Key, task.Priority, parent = request.Parent });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);
    }

    public Task<IReadOnlyList<TaskDto>> ListAsync(TaskQuery query, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<TaskDto>>(async (db, _) =>
        {
            var tasks = db.Tasks.Include(t => t.Project).Include(t => t.Owner).AsQueryable();
            if (query.States is { Count: > 0 } states) tasks = tasks.Where(t => states.Contains(t.State));
            if (query.OpenOnly) tasks = tasks.Where(t => t.State != TaskState.Done && t.State != TaskState.Cancelled);
            if (!string.IsNullOrWhiteSpace(query.Project))
            {
                var key = query.Project.Trim().ToLowerInvariant();
                tasks = tasks.Where(t => t.Project!.Key == key);
            }
            if (!string.IsNullOrWhiteSpace(query.Owner))
            {
                var owner = query.Owner.Trim().ToLowerInvariant();
                tasks = tasks.Where(t => t.Owner!.Name == owner);
            }
            var rows = await tasks
                .OrderByDescending(t => t.Priority).ThenBy(t => t.Id)
                .Take(Math.Clamp(query.Limit, 1, 2000))
                .ToListAsync(ct);
            var validations = await Validations.ForTasksAsync(db, rows.Select(t => t.Id).ToList(), ct);
            var result = new List<TaskDto>();
            foreach (var task in rows) result.Add(await Validations.MapAsync(db, task, validations.GetValueOrDefault(task.Id), ct));
            return result;
        }, ct);

    public Task<TaskDetailDto> GetAsync(string id, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) =>
        {
            var task = await LoadAsync(db, id, ct);
            var events = await db.Events.Where(e => e.TaskId == task.Id).OrderBy(e => e.Seq).ToListAsync(ct);
            var validations = await Validations.ForTasksAsync(db, [task.Id], ct);
            return new TaskDetailDto(await Validations.MapAsync(db, task, validations.GetValueOrDefault(task.Id), ct), events.Select(e => e.ToDto()).ToList(),
                await IncidentService.ForTaskAsync(db, task.Id, ct), await NotesAsync(db, task.Id, ct), await WorkKindSuggestedAsync(db, task.Id, ct));
        }, ct);

    /// <summary>The utility tier's guess at the task's work-kind, or null when none was recorded.</summary>
    public static Task<string?> WorkKindSuggestedAsync(MuthurDb db, int taskId, CancellationToken ct) =>
        db.Meta.Where(e => e.Key == MetaEntry.TaskWorkKindSuggested(taskId)).Select(e => e.Value).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Records what the utility tier thinks the work is. Advisory: it is shown to the orchestrator and never read
    /// by the spec parser, so the worst a wrong guess does is get overruled by the line the owner writes. Only
    /// the founder or, once the task is owned, its owner may set it, and only to one of the four kinds a spec
    /// could declare.
    /// </summary>
    public Task<TaskDto> SetWorkKindSuggestedAsync(Caller caller, string id, WorkKindRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            if (task.OwnerAgentId is not null) RequireOwnerOrFounder(task, caller);
            else caller.RequireIdentified();
            var kind = request.WorkKind?.Trim() ?? "";
            if (!WorkKindRequest.Kinds.Contains(kind, StringComparer.Ordinal))
                throw Fail.Rule("invalid_work_kind", $"'{kind}' is not a work-kind. Valid: {string.Join(", ", WorkKindRequest.Kinds)}.");
            var key = MetaEntry.TaskWorkKindSuggested(task.Id);
            var entry = await m.Db.Meta.SingleOrDefaultAsync(e => e.Key == key, ct);
            if (entry is null) m.Db.Meta.Add(new MetaEntry { Key = key, Value = kind });
            else entry.Value = kind;
            task.UpdatedAt = m.Now;
            m.Record("task.work_kind_suggested", task.Id, new { workKind = kind, by = caller.Name });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);

    /// <summary>The owner's working notes on a task, or null when none were written.</summary>
    public static Task<string?> NotesAsync(MuthurDb db, int taskId, CancellationToken ct) =>
        db.Meta.Where(e => e.Key == MetaEntry.TaskNotes(taskId)).Select(e => e.Value).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Replaces the owner's working notes. Only the owner: the notes are one session's understanding handed to
    /// the next session on the same task, and nobody else's account of the work belongs in that slot. Blank
    /// clears them. The size cap keeps the resume prompt a prompt and not a transcript.
    /// </summary>
    public Task<TaskDto> SetNotesAsync(Caller caller, string id, TaskNotesRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            RequireOwnerOrFounder(task, caller);
            var notes = request.Notes?.Trim() ?? "";
            if (notes.Length > TaskNotesRequest.MaxChars)
                throw Fail.Rule("notes_too_long", $"Notes are {notes.Length} characters; the limit is {TaskNotesRequest.MaxChars}. Keep what the next session needs, not the transcript.");
            var key = MetaEntry.TaskNotes(task.Id);
            var entry = await m.Db.Meta.SingleOrDefaultAsync(e => e.Key == key, ct);
            if (notes.Length == 0)
            {
                if (entry is not null) { m.Db.Meta.Remove(entry); m.Record("task.notes_cleared", task.Id); }
            }
            else if (entry is null) { m.Db.Meta.Add(new MetaEntry { Key = key, Value = notes }); m.Record("task.notes_set", task.Id, new { chars = notes.Length }); }
            else if (entry.Value != notes) { entry.Value = notes; m.Record("task.notes_set", task.Id, new { chars = notes.Length }); }
            task.UpdatedAt = m.Now;
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);

    /// <summary>Atomic: mutations are serialized, so of two racing claims exactly one sees a claimable task.</summary>
    public Task<TaskDto> ClaimAsync(Caller caller, string id, ClaimTaskRequest request, CancellationToken ct = default)
    {
        var agentId = caller.RequireAgent();
        var lease = leases.ResolveClaimLease(request.LeaseMinutes);
        return ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            if (task.State == TaskState.InProgress && task.OwnerAgentId == agentId)
            {
                task.ClaimExpires = m.Now + lease; // re-claiming your own task just renews it
                return await Validations.MapAsync(m.Db, task, null, ct);
            }
            var completed = await TaskDependencies.CompletedAsync(m.Db, ct);
            var incident = (await IncidentService.EffectiveAsync(m.Db, ct)).FirstOrDefault(s => s.TaskId == Wire.TaskId(task.Id) && s.Assignment == "#orchestrator");
            if (incident is not null)
                throw Fail.Conflict("incident_wait", $"{incident.IncidentId}: {incident.Reason}. Recovery: {incident.RecoveryCondition}");
            var pending = task.DependsOn.Where(d => !completed.Contains(d)).ToList();
            if (pending.Count > 0)
                throw Fail.Conflict("dependency_wait", "Waiting for " + string.Join(", ", pending) + " to land.");
            if (!LeasePolicy.IsClaimable(task, m.Now))
                throw Fail.Conflict("not_claimable", task.State == TaskState.InProgress
                    ? $"{Wire.TaskId(task.Id)} is held by '{task.Owner?.Name}' until {task.ClaimExpires:O}."
                    : $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}' and cannot be claimed.");

            var previousOwner = task.State == TaskState.InProgress ? task.Owner?.Name : null;
            task.State = TaskState.InProgress;
            task.OwnerAgentId = agentId;
            task.Owner = await m.Db.Agents.SingleAsync(a => a.Id == agentId, ct);
            task.ClaimExpires = m.Now + lease;
            task.UpdatedAt = m.Now;
            m.Record("task.claimed", task.Id, new { agent = caller.Name, expires = task.ClaimExpires, tookOverFrom = previousOwner });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);
    }

    public Task<TaskDto> ReleaseAsync(Caller caller, string id, ReleaseTaskRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            RequireOwnerOrFounder(task, caller);
            TaskStateMachine.EnsureCanTransition(Wire.TaskId(task.Id), task.State, TaskState.Backlog);
            ReturnToBacklog(task, m.Now);
            await RequestService.WithdrawForTaskAsync(m, task.Id, "task released", ct);
            m.Record("task.released", task.Id, new { agent = caller.Name, request.Reason });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);

    public Task<TaskDto> SetDependenciesAsync(Caller caller, string id, DependenciesRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            if (task.OwnerAgentId is not null) RequireOwnerOrFounder(task, caller);
            else caller.RequireIdentified();
            if (task.State is not (TaskState.Backlog or TaskState.InProgress or TaskState.Blocked))
                throw Fail.Rule("dependency_state", "Set prerequisites before submitting work for validation.");
            if (request.Tasks is null || request.Tasks.Count > 50)
                throw Fail.Rule("invalid_dependencies", "Provide at most 50 prerequisite task IDs.");
            var targets = new List<string>();
            foreach (var key in request.Tasks)
            {
                var dependency = await LoadAsync(m.Db, key, ct);
                if (dependency.ProjectId != task.ProjectId)
                    throw Fail.Rule("dependency_project", "Prerequisites must belong to the same project.");
                targets.Add(Wire.TaskId(dependency.Id));
            }
            var graph = await m.Db.Tasks.Where(t => t.ProjectId == task.ProjectId).ToListAsync(ct);
            var edges = graph.ToDictionary(t => Wire.TaskId(t.Id), t => t.DependsOn);
            var own = Wire.TaskId(task.Id);
            var visited = new HashSet<string>();
            bool ReachesSelf(string key) => key == own || (visited.Add(key) && edges.GetValueOrDefault(key, []).Any(ReachesSelf));
            if (targets.Any(ReachesSelf)) throw Fail.Rule("dependency_cycle", "Prerequisites cannot form a cycle.");
            task.DependsOn = targets.Distinct(StringComparer.Ordinal).ToList();
            task.DependencyReason = task.DependsOn.Count == 0 ? null : request.Reason?.Trim();
            if (task.State == TaskState.InProgress && task.DependsOn.Count > 0)
            {
                ReturnToBacklog(task, m.Now);
                m.Record("task.released", task.Id, new { reason = "Waiting for prerequisites." });
            }
            task.UpdatedAt = m.Now;
            m.Record("task.dependencies_set", task.Id, new { tasks = task.DependsOn, reason = task.DependencyReason });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);

    public Task<TaskDto> SetSpecAsync(Caller caller, string id, SetSpecRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            RequireOwnerOrFounder(task, caller);
            if (string.IsNullOrWhiteSpace(request.Path) || Path.IsPathRooted(request.Path))
                throw Fail.Rule("relative_path_required", "The spec path must be relative to the project repository.");
            var relative = request.Path.Replace('\\', '/');
            var spec = await RequireSpecBelongsToTaskAsync(task, relative, request.Branch, ct);
            if (task.State is not (TaskState.InProgress or TaskState.Blocked))
                throw Fail.Rule("not_in_progress", $"A spec can only be attached while the task is in progress; {Wire.TaskId(task.Id)} is '{task.State.ToWire()}'.");
            task.SpecPath = relative;
            task.SpecSha256 = Validations.Hash(spec);
            task.UpdatedAt = m.Now;
            m.Record("task.spec_set", task.Id, new { path = task.SpecPath, sha256 = task.SpecSha256 });

            // A spec that says out loud what it needs is flagged the moment it is frozen, rather than
            // discovered by a validator session that spends a role lease, an account's quota and 45 minutes
            // of the founder's budget to find out. Installed headless interaction can be preflighted by
            // unattended sessions; other declared needs still require attendance.
            if (SpecNeeds(spec) is { } need && string.IsNullOrWhiteSpace(task.AttendedReason))
            {
                task.AttendedReason = $"The spec declares 'needs: {need}'. This need requires attendance, so this waits for a human validator.";
                m.Record("task.attended", task.Id, new { reason = task.AttendedReason, source = "spec_needs", need });
            }
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);

    public Task<TaskDto> SetPriorityAsync(Caller caller, string id, SetPriorityRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        return ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            var from = task.Priority;
            task.Priority = request.Priority;
            task.UpdatedAt = m.Now;
            m.Record("task.priority_changed", task.Id, new { from, to = task.Priority });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);
    }

    /// <summary>
    /// Says that a human validator is needed, and why, or lifts that when the reason stops being true. Nothing sets
    /// this automatically: a person reads the evidence and decides. Repeating an assertion is not an error.
    /// </summary>
    public Task<TaskDto> SetAttendedAsync(Caller caller, string id, AttendedRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            // An owned task is its owner's to flag; an unowned one is anyone's, because the case worth recording
            // early - a backlog task whose author already knows it needs a browser - has no owner to be.
            if (task.OwnerAgentId is not null) RequireOwnerOrFounder(task, caller);
            else caller.RequireIdentified();
            var was = task.AttendedReason;
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            if (reason == was) return await Validations.MapAsync(m.Db, task, null, ct);
            task.AttendedReason = reason;
            task.UpdatedAt = m.Now;
            if (reason is null) m.Record("task.attended_cleared", task.Id, new { was });
            else m.Record("task.attended", task.Id, new { reason });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);


    /// <summary>
    /// "Do not land this yet, and here is why." Information the next lander sees, never a lock — <c>land</c>
    /// warns and proceeds, which is the founder's T-16 answer applied to intentions rather than to files.
    /// <para>
    /// Any identified agent may hold any task, owned or not, and that is the difference from
    /// <see cref="SetAttendedAsync"/>: the whole point is that somebody other than the owner has an intention
    /// about it. The one on this task was an orchestrator holding another orchestrator's work.
    /// </para>
    /// </summary>
    public Task<TaskDto> SetHoldAsync(Caller caller, string id, HoldRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            caller.RequireIdentified();
            var task = await LoadAsync(m.Db, id, ct);
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            if (reason is null)
            {
                // Clearing what is not held is not an error, and neither is clearing one that has lapsed:
                // both leave the task without a live hold, which is what the caller asked for.
                var was = task.HoldReason;
                if (was is null) return await Validations.MapAsync(m.Db, task, null, ct);
                task.HoldReason = null;
                task.HoldBy = null;
                task.HoldExpires = null;
                task.UpdatedAt = m.Now;
                m.Record("task.hold_cleared", task.Id, new { was, by = caller.Name });
                return await Validations.MapAsync(m.Db, task, null, ct);
            }

            task.HoldReason = reason;
            task.HoldBy = caller.Name;
            task.HoldExpires = m.Now + TimeSpan.FromMinutes(options.HoldMinutes);
            task.UpdatedAt = m.Now;
            m.Record("task.held", task.Id, new { reason, by = caller.Name, expires = task.HoldExpires });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);

    /// <summary>The hold if it still counts, or null. An expired one is left on the row but is nobody's business.</summary>
    public static (string Reason, string By, DateTimeOffset PlacedAt)? LiveHold(WorkTask task, DateTimeOffset now, int holdMinutes) =>
        task is { HoldReason: { } reason, HoldBy: { } by, HoldExpires: { } expires } && expires > now
            ? (reason, by, expires - TimeSpan.FromMinutes(holdMinutes))
            : null;

    public Task<TaskDto> CancelAsync(Caller caller, string id, CancelTaskRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            if (task.OwnerAgentId is not null) RequireOwnerOrFounder(task, caller);
            else caller.RequireIdentified();
            TaskStateMachine.EnsureCanTransition(Wire.TaskId(task.Id), task.State, TaskState.Cancelled);
            task.State = TaskState.Cancelled;
            await RequestService.WithdrawForTaskAsync(m, task.Id, "task cancelled", ct);
            task.ClaimExpires = null;
            task.UpdatedAt = m.Now;
            m.Record("task.cancelled", task.Id, new { request.Reason });
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);

    public Task<TaskDto> ReopenAsync(Caller caller, string id, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        return ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            TaskStateMachine.EnsureCanTransition(Wire.TaskId(task.Id), task.State, TaskState.Backlog);
            ReturnToBacklog(task, m.Now);
            m.Record("task.reopened", task.Id);
            return await Validations.MapAsync(m.Db, task, null, ct);
        }, ct);
    }

    /// <summary>Returns lapsed in-progress tasks to the backlog. Run at startup and periodically.</summary>
    public Task<int> SweepExpiredClaimsAsync(CancellationToken ct = default) =>
        ledger.MutateAsync(Caller.System, async m =>
        {
            var lapsed = await m.Db.Tasks.Include(t => t.Owner)
                .Where(t => t.State == TaskState.InProgress && (t.ClaimExpires == null || t.ClaimExpires <= m.Now))
                .ToListAsync(ct);
            foreach (var task in lapsed)
            {
                var owner = task.Owner?.Name;
                ReturnToBacklog(task, m.Now);
                m.Record("task.claim_expired", task.Id, new { agent = owner });
            }
            return lapsed.Count;
        }, ct);

    public static async Task<WorkTask> LoadAsync(MuthurDb db, string id, CancellationToken ct)
    {
        if (!Wire.TryParseTaskId(id, out var number))
            throw Fail.Rule("invalid_task_id", $"'{id}' is not a task id (expected T-<number>).");
        return await db.Tasks.Include(t => t.Project).Include(t => t.Owner).SingleOrDefaultAsync(t => t.Id == number, ct)
            ?? throw Fail.NotFound("Task", Wire.TaskId(number));
    }

    public static void RequireOwnerOrFounder(WorkTask task, Caller caller)
    {
        if (caller.IsFounder) return;
        if (caller.AgentId is null || task.OwnerAgentId != caller.AgentId)
            throw Fail.Rule("not_owner", $"{Wire.TaskId(task.Id)} is owned by '{task.Owner?.Name ?? "nobody"}'; only its owner or the founder may do this.");
    }

    /// <summary>
    /// A spec must be a file inside the project's checkout, and if its first heading names a task it must name
    /// this one: ledger ids get reused, so attaching another task's spec is how implementers build the wrong thing.
    /// </summary>
    private async Task<string> RequireSpecBelongsToTaskAsync(WorkTask task, string relative, string? branch, CancellationToken ct)
    {
        var root = Path.GetFullPath(task.Project!.RepoPath);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        // First, and on the path rather than on any content: reading out of a branch must not become a way to
        // name something outside the repository.
        if (!RepoFile.IsInside(root, full))
            throw Fail.Rule("spec_outside_repository", "The spec path must stay inside the project repository.");

        // A branch is a hint, never an assertion: one that does not have the file is passed over rather than
        // fatal, which is what makes it safe for the CLI to fill in from wherever the caller is standing.
        var tried = new List<string>();
        var content = await FromBranchAsync(branch) ?? await FromBranchAsync(task.Branch) ?? FromWorkingTree();
        if (content is null)
            throw Fail.Rule("spec_missing", tried.Count > 0
                ? $"No file at '{relative}' in {task.Project.RepoPath}: not on {string.Join(" or ", tried.Select(b => $"branch '{b}'"))}, " +
                  "and not in the working tree. If the spec is committed on a different branch, pass --branch <that branch>."
                : $"No file at '{relative}' in {task.Project.RepoPath}. If the spec is committed on a task branch, pass --branch task/T-n-<slug>.");

        // The heading check keeps its own small window: its question is about the first non-blank line, and a
        // mistaken path to something enormous stays cheap to reject.
        RequireSpecHeading(task, relative, content);
        return content;

        async Task<string?> FromBranchAsync(string? candidate)
        {
            if (candidate is not { Length: > 0 } || tried.Contains(candidate)) return null;
            tried.Add(candidate);
            // Refused rather than truncated, the same way the working tree is read: a spec too big to read in
            // full is one whose declared need could be past the cut, and silently scanning a prefix is the
            // failure this whole rule exists to prevent.
            if (await lander.ReadFileAsync(task.Project!, candidate, relative, ct) is not { } text) return null;
            if (text.Length > MaxSpec) throw TooLarge(relative);
            return text;
        }

        string? FromWorkingTree()
        {
            var resolvedRoot = SpecPath.Resolve(root);
            var resolved = SpecPath.Resolve(full);
            if (resolvedRoot is null || resolved is null)
                throw Fail.Rule("spec_unreadable", "The spec location could not be determined.");
            if (!RepoFile.IsInside(resolvedRoot, resolved))
                throw Fail.Rule("spec_outside_repository", "The spec path must stay inside the project repository.");
            if (!File.Exists(resolved)) return null;
            try
            {
                using var reader = new StreamReader(resolved);
                // One more than the cap, so "it filled the buffer" and "there was more" are different answers.
                var buffer = new char[MaxSpec + 1];
                var read = reader.ReadBlock(buffer, 0, buffer.Length);
                if (read > MaxSpec) throw TooLarge(relative);
                return new string(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Fail.Rule("spec_unreadable", $"'{relative}' could not be read: {ex.Message}");
            }
        }
    }

    /// <summary>How much of a spec the heading check reads: a mistaken path to something enormous stays cheap.</summary>
    private const int Head = 8 * 1024;

    internal static void RequireSpecHeading(WorkTask task, string relative, string content)
    {
        if (FirstHeadingTaskId(content[..Math.Min(content.Length, Head)]) is { } found && found != Wire.TaskId(task.Id))
            throw Fail.Rule("spec_id_mismatch", $"'{relative}' is the spec for {found}, not {Wire.TaskId(task.Id)}.");
    }

    /// <summary>
    /// The largest spec that is read at all. A declared need can be anywhere in the document, so the read
    /// cannot stop early — and a cap that truncates silently does not remove that problem, it only moves it
    /// to a bigger number. Anything past this is **refused**, which keeps a mistaken path to something
    /// enormous cheap to reject while leaving no size at which a declaration is quietly unseen.
    /// </summary>
    private const int MaxSpec = 1024 * 1024;

    private static MuthurException TooLarge(string relative) =>
        Fail.Rule("spec_too_large", $"'{relative}' is larger than 1 MB. A spec that size cannot be read in full, " +
            "and a 'needs:' line past the cut would be silently missed. Shorten it, or split what belongs elsewhere out of it.");

    /// <summary>
    /// The first declared need requiring attendance, or null when none does.
    /// <para>
    /// One line, anywhere in the document: <c>needs: browser</c>, with any leading list marker, quote marker
    /// or emphasis. Only headless-browser is unattended-capable, subject to the caller's prerequisite probe.
    /// Continue past that marker so a later attended need is not hidden. This is not capability scheduling.
    /// </para>
    /// </summary>
    private static string? SpecNeeds(string content)
    {
        foreach (var line in content.Split('\n'))
            if (SpecNeedsPattern().Match(line) is { Success: true } match
                && match.Groups[1].Value.Trim().TrimEnd('.', '*', '`') is { Length: > 0 } need
                && !need.Equals("headless-browser", StringComparison.OrdinalIgnoreCase))
                return need;
        return null;
    }

    /// <summary>
    /// The task id in the spec's first non-blank line, or null if the heading names none. Takes the content
    /// rather than a path, because a spec read out of a branch must be checked exactly as one on disk is.
    /// </summary>
    private static string? FirstHeadingTaskId(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = SpecHeadingPattern().Match(line.Trim());
            return match.Success ? match.Groups[1].Value : null;
        }
        return null;
    }

    private static void ReturnToBacklog(WorkTask task, DateTimeOffset now)
    {
        task.State = TaskState.Backlog;
        task.OwnerAgentId = null;
        task.Owner = null;
        task.ClaimExpires = null;
        task.UpdatedAt = now;
    }
}
