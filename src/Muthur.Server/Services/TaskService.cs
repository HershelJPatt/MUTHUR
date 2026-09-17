using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed record TaskQuery(IReadOnlyList<TaskState>? States = null, string? Project = null, string? Owner = null, bool OpenOnly = false, int Limit = 500);

public sealed class TaskService(Ledger ledger, LeasePolicy leases)
{
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
            return task.ToDto();
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
            return rows.Select(t => t.ToDto(validations.GetValueOrDefault(t.Id))).ToList();
        }, ct);

    public Task<TaskDetailDto> GetAsync(string id, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) =>
        {
            var task = await LoadAsync(db, id, ct);
            var events = await db.Events.Where(e => e.TaskId == task.Id).OrderBy(e => e.Seq).ToListAsync(ct);
            var validations = await Validations.ForTasksAsync(db, [task.Id], ct);
            return new TaskDetailDto(task.ToDto(validations.GetValueOrDefault(task.Id)), events.Select(e => e.ToDto()).ToList());
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
                return task.ToDto();
            }
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
            return task.ToDto();
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
            return task.ToDto();
        }, ct);

    public Task<TaskDto> SetSpecAsync(Caller caller, string id, SetSpecRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await LoadAsync(m.Db, id, ct);
            RequireOwnerOrFounder(task, caller);
            if (string.IsNullOrWhiteSpace(request.Path) || Path.IsPathRooted(request.Path))
                throw Fail.Rule("relative_path_required", "The spec path must be relative to the project repository.");
            if (task.State is not (TaskState.InProgress or TaskState.Blocked))
                throw Fail.Rule("not_in_progress", $"A spec can only be attached while the task is in progress; {Wire.TaskId(task.Id)} is '{task.State.ToWire()}'.");
            task.SpecPath = request.Path.Replace('\\', '/');
            task.UpdatedAt = m.Now;
            m.Record("task.spec_set", task.Id, new { path = task.SpecPath });
            return task.ToDto();
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
            return task.ToDto();
        }, ct);
    }

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
            return task.ToDto();
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
            return task.ToDto();
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

    private static void ReturnToBacklog(WorkTask task, DateTimeOffset now)
    {
        task.State = TaskState.Backlog;
        task.OwnerAgentId = null;
        task.Owner = null;
        task.ClaimExpires = null;
        task.UpdatedAt = now;
    }
}
