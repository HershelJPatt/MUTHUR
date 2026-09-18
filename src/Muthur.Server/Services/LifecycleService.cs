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
                m.Db.TaskValidations.Add(new TaskValidation { TaskId = task.Id, ValidatorKey = validator, Verdict = Verdict.Pending, WaitingSince = m.Now });

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

    /// <summary>
    /// Tasks waiting for a verdict, optionally only those waiting on one validator role. Asked about one role,
    /// it answers with what the caller can actually take: a pair another validator has claimed is left out
    /// unless <paramref name="includeClaimed"/> says otherwise.
    /// </summary>
    public Task<IReadOnlyList<TaskDto>> PendingAsync(Caller caller, string? validatorKey, bool includeClaimed = false, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<TaskDto>>(async (db, now) =>
        {
            var key = validatorKey?.Trim().ToLowerInvariant();
            var pending = await db.TaskValidations
                .Where(v => v.Verdict == Verdict.Pending && (key == null || v.ValidatorKey == key))
                .ToListAsync(ct);
            // Without a role there is no single row per task to read a claim from, so there is nothing to filter on.
            if (key is not null && !includeClaimed)
                pending = pending.Where(v => v.ClaimedByAgentId == caller.AgentId || !LeasePolicy.IsClaimLive(v, now)).ToList();
            var pendingIds = pending.Select(v => v.TaskId).Distinct().ToList();
            var tasks = await db.Tasks.Include(t => t.Project).Include(t => t.Owner)
                .Where(t => t.State == TaskState.Validating && pendingIds.Contains(t.Id))
                .OrderByDescending(t => t.Priority).ThenBy(t => t.Id).ToListAsync(ct);
            var validations = await Validations.ForTasksAsync(db, tasks.Select(t => t.Id).ToList(), ct);
            return tasks.Select(t => t.ToDto(validations.GetValueOrDefault(t.Id))).ToList();
        }, ct);

    /// <summary>How deep the queue is per validator role. Idle roles appear with zeros: an empty queue is a fact too.</summary>
    public Task<IReadOnlyList<ValidationQueueDto>> QueueAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<ValidationQueueDto>>(async (db, now) =>
        {
            var roles = await db.Roles.Where(r => r.IsValidator).ToListAsync(ct);
            if (roles.Count == 0) return [];

            var validating = await db.Tasks.Where(t => t.State == TaskState.Validating).Select(t => t.Id).ToListAsync(ct);
            var waiting = await db.TaskValidations
                .Where(v => v.Verdict == Verdict.Pending && validating.Contains(v.TaskId))
                .ToListAsync(ct);
            var holders = await db.RoleHolds.Where(h => h.LeaseExpires > now)
                .GroupBy(h => h.RoleKey)
                .Select(g => new { Role = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Role, x => x.Count, ct);

            return roles
                .Select(r =>
                {
                    var rows = waiting.Where(v => v.ValidatorKey == r.Key).ToList();
                    return new ValidationQueueDto(
                        r.Key, r.Holders, holders.GetValueOrDefault(r.Key), rows.Count,
                        rows.Count(v => LeasePolicy.IsClaimLive(v, now)),
                        rows.Count == 0 ? null : rows.Min(v => v.WaitingSince));
                })
                .OrderByDescending(q => q.Waiting).ThenBy(q => q.Role, StringComparer.Ordinal)
                .ToList();
        }, ct);

    /// <summary>
    /// Takes one (task, role) pair so no second validator spends a session on it. The claim is a lease: it lapses
    /// if the validator stops showing signs of life, and the sweep hands the pair back.
    /// </summary>
    public Task<TaskDto> ClaimValidationAsync(Caller caller, string id, ClaimValidationRequest request, CancellationToken ct = default)
    {
        // A claim is an agent taking work. The founder holds no roles and so has nothing to claim with.
        var agentId = caller.RequireAgent();
        var validator = (request.Validator ?? "").Trim().ToLowerInvariant();

        return ledger.MutateAsync(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            if (task.State != TaskState.Validating)
                throw Fail.Rule("not_validating", $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}', not awaiting validation.");
            var row = await LoadRowAsync(m, task, validator, ct);
            if (row.Verdict != Verdict.Pending)
                throw Fail.Rule("already_decided", $"'{validator}' has already given a verdict on {Wire.TaskId(task.Id)}.");
            if (!await RoleLeases.HoldsAsync(m.Db, agentId, validator, m.Now, ct))
                throw Fail.Rule("role_not_held", $"You do not hold the '{validator}' role. Take it first: muthur role take {validator}");
            // The rule a verdict enforces anyway, moved earlier so a validator finds out before it spends a session.
            if (task.OwnerAgentId == agentId)
                throw Fail.Rule("self_validation", "You own this task. Validation must come from someone who did not build it.");
            if (row.ClaimedByAgentId != agentId && LeasePolicy.IsClaimLive(row, m.Now))
                throw Fail.Conflict("validation_claimed",
                    $"{Wire.TaskId(task.Id)} is being validated for '{validator}' by '{row.ClaimedBy?.Name}' until {row.ClaimExpires:O}.");

            // Claiming one you hold is a renewal and makes no ledger noise. Re-taking one of your own that
            // lapsed is not: it was open to anyone in between, and the ledger should say you took it again.
            var renewal = row.ClaimedByAgentId == agentId && LeasePolicy.IsClaimLive(row, m.Now);
            row.ClaimedByAgentId = agentId;
            row.ClaimedBy = await m.Db.Agents.SingleAsync(a => a.Id == agentId, ct);
            row.ClaimExpires = m.Now + leases.ClaimLease;
            if (!renewal)
                m.Record("validation.claimed", task.Id, new { validator, by = caller.Name });

            await m.Db.SaveChangesAsync(ct);
            var validations = await Validations.ForTasksAsync(m.Db, [task.Id], ct);
            return task.ToDto(validations.GetValueOrDefault(task.Id));
        }, ct);
    }

    /// <summary>Gives a claimed pair back without a verdict. The claimer or the founder.</summary>
    public Task<TaskDto> ReleaseValidationAsync(Caller caller, string id, ClaimValidationRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        var validator = (request.Validator ?? "").Trim().ToLowerInvariant();

        return ledger.MutateAsync(caller, async m =>
        {
            // No state rule here: giving a claim back is always allowed, whatever became of the task.
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            var row = await LoadRowAsync(m, task, validator, ct);
            if (row.ClaimedByAgentId is null)
                throw Fail.Rule("not_claimer", $"{Wire.TaskId(task.Id)} is not claimed for '{validator}'.");
            if (row.ClaimedByAgentId != caller.AgentId && !caller.IsFounder)
                throw Fail.Rule("not_claimer",
                    $"{Wire.TaskId(task.Id)} is claimed for '{validator}' by '{row.ClaimedBy?.Name}'; only the claimer or the founder may release it.");

            var released = row.ClaimedBy?.Name;
            ClearClaim(row);
            m.Record("validation.released", task.Id, new { validator, by = released ?? caller.Name });

            await m.Db.SaveChangesAsync(ct);
            var validations = await Validations.ForTasksAsync(m.Db, [task.Id], ct);
            return task.ToDto(validations.GetValueOrDefault(task.Id));
        }, ct);
    }

    /// <summary>Hands back the pairs whose validators went quiet, so a lapsed claim never wedges a task.</summary>
    public Task<int> SweepExpiredValidationClaimsAsync(CancellationToken ct = default) =>
        ledger.MutateAsync(Caller.System, async m =>
        {
            var lapsed = await m.Db.TaskValidations.Include(v => v.ClaimedBy)
                .Where(v => v.ClaimedByAgentId != null && (v.ClaimExpires == null || v.ClaimExpires <= m.Now))
                .ToListAsync(ct);
            foreach (var row in lapsed)
            {
                m.Record("validation.claim_expired", row.TaskId, new { validator = row.ValidatorKey, agent = row.ClaimedBy?.Name });
                ClearClaim(row);
            }
            return lapsed.Count;
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
            var row = await LoadRowAsync(m, task, validator, ct);

            if (caller.AgentId is { } agentId)
            {
                if (!await RoleLeases.HoldsAsync(m.Db, agentId, validator, m.Now, ct))
                    throw Fail.Rule("role_not_held", $"You do not hold the '{validator}' role. Take it first: muthur role take {validator}");
                if (task.OwnerAgentId == agentId)
                    throw Fail.Rule("self_validation", "You own this task. Validation must come from someone who did not build it.");
            }
            // Before the verdict is written, never after: a session someone else is spending must not be overwritten.
            // An unclaimed pair the caller may validate is claimed by the verdict itself, so one validator working
            // alone needs no extra command; the claim is cleared again below, because a decided pair is nobody's work.
            if (row.ClaimedByAgentId != caller.AgentId && LeasePolicy.IsClaimLive(row, m.Now))
                throw Fail.Conflict("validation_claimed",
                    $"{Wire.TaskId(task.Id)} is being validated for '{validator}' by '{row.ClaimedBy?.Name}'.");

            row.Verdict = pass ? Verdict.Yes : Verdict.No;
            row.Evidence = request.Evidence;
            row.AgentId = caller.AgentId;
            row.At = m.Now;
            ClearClaim(row);   // decided: nobody is working this pair any more
            task.UpdatedAt = m.Now;
            m.Record(pass ? "validation.passed" : "validation.failed", task.Id, new { validator, by = caller.Name, evidence = request.Evidence });

            await m.Db.SaveChangesAsync(ct);
            var rows = await m.Db.TaskValidations.Where(v => v.TaskId == task.Id).ToListAsync(ct);
            if (!pass)
            {
                // The round is over, so no claim on this task survives it.
                foreach (var other in rows) ClearClaim(other);
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

            // The cleared claims have to reach the table before the rows are read back: the read joins the
            // claiming agent in, and a claim still standing in the database would be fixed up onto them again.
            await m.Db.SaveChangesAsync(ct);
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

    private static async Task<TaskValidation> LoadRowAsync(Mutation m, WorkTask task, string validator, CancellationToken ct) =>
        await m.Db.TaskValidations.Include(v => v.ClaimedBy)
            .SingleOrDefaultAsync(v => v.TaskId == task.Id && v.ValidatorKey == validator, ct)
        ?? throw Fail.Rule("validator_not_required", $"'{validator}' is not a required validator of {Wire.TaskId(task.Id)}.");

    private static void ClearClaim(TaskValidation row)
    {
        row.ClaimedByAgentId = null;
        row.ClaimedBy = null;
        row.ClaimExpires = null;
    }
}
