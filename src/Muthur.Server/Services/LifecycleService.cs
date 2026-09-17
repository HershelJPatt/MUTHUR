using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>The second half of a task's life: implemented → validating → validated → landed.</summary>
public sealed class LifecycleService(Ledger ledger, LeasePolicy leases, ITaskLander lander)
{
    private readonly SemaphoreSlim _landing = new(1, 1);

    /// <summary>The owner declares the work built, reviewed and tested. Validators take it from here.</summary>
    public async Task<TaskDto> ImplementedAsync(Caller caller, string id, ImplementedRequest request, CancellationToken ct = default)
    {
        var branch = (request.Branch ?? "").Trim();
        if (branch.Length == 0)
            throw Fail.Rule("branch_required", "Pass the task branch: --branch task/T-n-<slug>.");

        var (project, specPath) = await ledger.ReadAsync(async (db, _) =>
        {
            var task = await TaskService.LoadAsync(db, id, ct);
            return (task.Project!, task.SpecPath);
        }, ct);
        if (branch == project.DefaultBranch)
            throw Fail.Rule("branch_is_default", $"'{branch}' is the default branch. Work happens on a task branch; MUTHUR lands it.");
        if (!await lander.BranchExistsAsync(project, branch, ct))
            throw Fail.Rule("branch_missing", $"Branch '{branch}' does not exist in {project.RepoPath}.");

        return await ledger.MutateAsync(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            TaskService.RequireOwnerOrFounder(task, caller);
            if (task.State != TaskState.InProgress)
                throw Fail.Rule("not_in_progress", $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}'; only an in-progress task can be marked implemented.");
            if (string.IsNullOrWhiteSpace(task.SpecPath))
                throw Fail.Rule("spec_required", $"{Wire.TaskId(task.Id)} has no spec. Attach the frozen spec first: muthur task spec {Wire.TaskId(task.Id)} specs/{Wire.TaskId(task.Id)}.md");

            var required = task.Project!.RequiredValidators;
            var existing = await m.Db.TaskValidations.Where(v => v.TaskId == task.Id).ToListAsync(ct);
            m.Db.TaskValidations.RemoveRange(existing); // a new round starts from scratch; earlier verdicts stay in the ledger
            foreach (var validator in required)
                m.Db.TaskValidations.Add(new TaskValidation { TaskId = task.Id, ValidatorKey = validator, Verdict = Verdict.Pending });

            task.Branch = branch;
            task.State = required.Count == 0 ? TaskState.Validated : TaskState.Validating;
            task.ClaimExpires = null;
            task.UpdatedAt = m.Now;
            m.Record("task.implemented", task.Id, new { branch, spec = specPath, validators = required });
            foreach (var validator in required)
                MessageService.PostFromHub(m, Recipient.Role, validator,
                    $"{Wire.TaskId(task.Id)} \"{task.Title}\" is ready for validation on branch {branch}.", task.Id);
            if (required.Count == 0)
                m.Record("task.validated", task.Id, new { note = "project requires no validators" });

            await m.Db.SaveChangesAsync(ct);
            var validations = await Validations.ForTasksAsync(m.Db, [task.Id], ct);
            return task.ToDto(validations.GetValueOrDefault(task.Id));
        }, ct);
    }

    /// <summary>Tasks waiting for a verdict, optionally only those waiting on one validator role.</summary>
    public Task<IReadOnlyList<TaskDto>> PendingAsync(string? validatorKey, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<TaskDto>>(async (db, _) =>
        {
            var key = validatorKey?.Trim().ToLowerInvariant();
            var pendingIds = await db.TaskValidations
                .Where(v => v.Verdict == Verdict.Pending && (key == null || v.ValidatorKey == key))
                .Select(v => v.TaskId).Distinct().ToListAsync(ct);
            var tasks = await db.Tasks.Include(t => t.Project).Include(t => t.Owner)
                .Where(t => t.State == TaskState.Validating && pendingIds.Contains(t.Id))
                .OrderByDescending(t => t.Priority).ThenBy(t => t.Id).ToListAsync(ct);
            var validations = await Validations.ForTasksAsync(db, tasks.Select(t => t.Id).ToList(), ct);
            return tasks.Select(t => t.ToDto(validations.GetValueOrDefault(t.Id))).ToList();
        }, ct);

    public Task<TaskDto> VerdictAsync(Caller caller, string id, VerdictRequest request, bool pass, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        var validator = (request.Validator ?? "").Trim().ToLowerInvariant();
        if (!pass && string.IsNullOrWhiteSpace(request.Evidence))
            throw Fail.Rule("evidence_required", "A failed validation needs evidence: what you ran, what you saw, how to reproduce.");

        return ledger.MutateAsync(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            if (task.State != TaskState.Validating)
                throw Fail.Rule("not_validating", $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}', not awaiting validation.");
            var row = await m.Db.TaskValidations.SingleOrDefaultAsync(v => v.TaskId == task.Id && v.ValidatorKey == validator, ct)
                ?? throw Fail.Rule("validator_not_required", $"'{validator}' is not a required validator of {Wire.TaskId(task.Id)}.");

            if (caller.AgentId is { } agentId)
            {
                if (!await RoleLeases.HoldsAsync(m.Db, agentId, validator, m.Now, ct))
                    throw Fail.Rule("role_not_held", $"You do not hold the '{validator}' role. Take it first: muthur role take {validator}");
                if (task.OwnerAgentId == agentId)
                    throw Fail.Rule("self_validation", "You own this task. Validation must come from someone who did not build it.");
            }

            row.Verdict = pass ? Verdict.Yes : Verdict.No;
            row.Evidence = request.Evidence;
            row.AgentId = caller.AgentId;
            row.At = m.Now;
            task.UpdatedAt = m.Now;
            m.Record(pass ? "validation.passed" : "validation.failed", task.Id, new { validator, by = caller.Name, evidence = request.Evidence });

            await m.Db.SaveChangesAsync(ct);
            var rows = await m.Db.TaskValidations.Where(v => v.TaskId == task.Id).ToListAsync(ct);
            if (!pass)
            {
                // Back to the owner, with a fresh lease so the task is not instantly up for grabs.
                task.State = TaskState.InProgress;
                task.ClaimExpires = m.Now + leases.ClaimLease;
                m.Record("task.validation_failed", task.Id, new { validator, owner = task.Owner?.Name });
                if (task.Owner is { } owner)
                    MessageService.PostFromHub(m, Recipient.Agent, owner.Name,
                        $"{Wire.TaskId(task.Id)} failed validation by {validator}; it is back in progress with you. Evidence: muthur task show {Wire.TaskId(task.Id)}", task.Id);
            }
            else if (rows.All(v => v.Verdict == Verdict.Yes))
            {
                task.State = TaskState.Validated;
                m.Record("task.validated", task.Id, new { validators = rows.Select(v => v.ValidatorKey).Order().ToList() });
                if (task.Owner is { } owner)
                    MessageService.PostFromHub(m, Recipient.Agent, owner.Name,
                        $"{Wire.TaskId(task.Id)} passed every validator. Land it: muthur task land {Wire.TaskId(task.Id)}", task.Id);
            }

            var validations = await Validations.ForTasksAsync(m.Db, [task.Id], ct);
            return task.ToDto(validations.GetValueOrDefault(task.Id));
        }, ct);
    }

    /// <summary>
    /// Merge authority lives here. The git work happens outside the ledger's write lock (it can take seconds),
    /// serialized by its own lock; the outcome is then recorded in one mutation.
    /// </summary>
    public async Task<TaskDto> LandAsync(Caller caller, string id, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        await _landing.WaitAsync(ct);
        try
        {
            var task = await ledger.ReadAsync((db, _) => TaskService.LoadAsync(db, id, ct), ct);
            TaskService.RequireOwnerOrFounder(task, caller);
            if (task.State != TaskState.Validated)
                throw Fail.Rule("not_validated", task.State == TaskState.Validating
                    ? $"{Wire.TaskId(task.Id)} is still validating. It lands only after every required validator says yes."
                    : $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}'. Only a validated task can land.");
            if (string.IsNullOrWhiteSpace(task.Branch))
                throw Fail.Rule("branch_required", $"{Wire.TaskId(task.Id)} has no branch recorded.");

            var result = await lander.LandAsync(task.Project!, task, task.Owner?.Name ?? caller.Name, ct);

            var dto = await ledger.MutateAsync(caller, async m =>
            {
                var current = await TaskService.LoadAsync(m.Db, id, ct);
                switch (result.Outcome)
                {
                    case LandOutcome.Landed:
                        current.State = TaskState.Done;
                        current.DoneAt = m.Now;
                        current.PrUrl = result.PrUrl ?? current.PrUrl;
                        m.Record(result.PrUrl is null ? "task.landed" : "task.pr_opened", current.Id,
                            new { mode = current.Project!.LandMode.ToWire(), current.Branch, commit = result.Commit, prUrl = result.PrUrl });
                        break;
                    case LandOutcome.Conflict:
                        current.State = TaskState.InProgress;
                        current.ClaimExpires = m.Now + leases.ClaimLease;
                        m.Record("task.land_failed", current.Id, new { code = result.Code, result.Message });
                        break;
                    default:
                        m.Record("task.land_refused", current.Id, new { code = result.Code, result.Message });
                        break;
                }
                current.UpdatedAt = m.Now;
                var validations = await Validations.ForTasksAsync(m.Db, [current.Id], ct);
                return current.ToDto(validations.GetValueOrDefault(current.Id));
            }, ct);

            return result.Outcome switch
            {
                LandOutcome.Landed => dto,
                LandOutcome.Conflict => throw Fail.Conflict(result.Code!, result.Message!),
                _ => throw Fail.Rule(result.Code!, result.Message!),
            };
        }
        finally
        {
            _landing.Release();
        }
    }
}
