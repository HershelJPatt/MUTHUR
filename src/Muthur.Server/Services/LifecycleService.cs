using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>The second half of a task's life: implemented → validating → validated → landed.</summary>
public sealed class LifecycleService(Ledger ledger, LeasePolicy leases, ITaskLander lander, MuthurOptions options, IntegrationService integration)
{
    private readonly SemaphoreSlim _landing = new(1, 1);

    private async Task<bool> ConductorStaffsValidatorsAsync(Mutation m, CancellationToken ct) =>
        await m.Db.Meta.Where(e => e.Key == MetaEntry.ConductorEnabled).Select(e => e.Value).SingleOrDefaultAsync(ct)
            is { } stored ? stored == "true" : options.ConductorEnabled;

    /// <summary>The owner declares the work built, reviewed and tested. Validators take it from here.</summary>
    public async Task<TaskDto> ImplementedAsync(Caller caller, string id, ImplementedRequest request, CancellationToken ct = default)
    {
        var branch = (request.Branch ?? "").Trim();
        if (branch.Length == 0)
            throw Fail.Rule("branch_required", "Pass the task branch: --branch task/T-n-<slug>.");

        return await ledger.MutateAsync(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            TaskService.RequireOwnerOrFounder(task, caller);
            if (task.State != TaskState.InProgress)
                throw Fail.Rule("not_in_progress", $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}'; only an in-progress task can be marked implemented.");
            var project = task.Project!;
            if (branch == project.DefaultBranch)
                throw Fail.Rule("branch_is_default", $"'{branch}' is the default branch. Work happens on a task branch; MUTHUR lands it.");
            var head = await lander.BranchHeadAsync(project, branch, ct)
                ?? throw Fail.Rule("branch_missing", $"Branch '{branch}' does not exist in {project.RepoPath}.");
            if (string.IsNullOrWhiteSpace(task.SpecPath))
                throw Fail.Rule("spec_required", $"{Wire.TaskId(task.Id)} has no spec. Attach the frozen spec first: muthur task spec {Wire.TaskId(task.Id)} specs/{Wire.TaskId(task.Id)}.md");
            var subject = await Validations.CreateAsync(m, task, head, lander, ct);
            m.Db.ValidationSubjects.Add(subject);
            task.CurrentSubject = subject;
            task.CurrentSubjectId = subject.Id;
            task.ValidationInvalidationReason = null;
            var required = subject.ToDto().RequiredValidators;
            var existing = await m.Db.TaskValidations.Where(v => v.TaskId == task.Id).ToListAsync(ct);
            m.Db.TaskValidations.RemoveRange(existing); // a new round starts from scratch; earlier verdicts stay in the ledger
            foreach (var validator in required)
                m.Db.TaskValidations.Add(new TaskValidation { TaskId = task.Id, SubjectId = subject.Id, ValidatorKey = validator, Verdict = Verdict.Pending, WaitingSince = m.Now });

            task.Branch = branch;
            task.State = required.Count == 0 ? TaskState.Validated : TaskState.Validating;
            task.ClaimExpires = null;
            task.UpdatedAt = m.Now;
            m.Record("task.implemented", task.Id, new { branch, head, spec = task.SpecPath, validators = required, subject = subject.ToDto() });
            // A human-staffed validator waits on its role's inbox for this; the conductor reads the ledger and staffs the
            // role itself, and a post nobody will ever read is exactly the kind of message the inbox is judged on.
            if (!await ConductorStaffsValidatorsAsync(m, ct))
                foreach (var validator in required)
                    MessageService.PostFromHub(m, Recipient.Role, validator,
                        $"{Wire.TaskId(task.Id)} \"{task.Title}\" is ready for validation on branch {branch}.", task.Id);
            if (required.Count == 0)
                m.Record("task.validated", task.Id, new { note = "project requires no validators" });

            await m.Db.SaveChangesAsync(ct);
            var validations = await Validations.ForTasksAsync(m.Db, [task.Id], ct);
            return await Validations.MapAsync(m.Db, task, validations.GetValueOrDefault(task.Id), ct);
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
                .Where(t => t.State == TaskState.Validating && t.CurrentSubjectId != null && pendingIds.Contains(t.Id))
                .OrderByDescending(t => t.Priority).ThenBy(t => t.Id).ToListAsync(ct);
            var validations = await Validations.ForTasksAsync(db, tasks.Select(t => t.Id).ToList(), ct);
            var result = new List<TaskDto>();
            foreach (var task in tasks)
                if (await Validations.CompatibleAsync(db, task, ct))
                    result.Add(await Validations.MapAsync(db, task, validations.GetValueOrDefault(task.Id), ct));
            return result;
        }, ct);

    /// <summary>How deep the queue is per validator role. Idle roles appear with zeros: an empty queue is a fact too.</summary>
    public Task<IReadOnlyList<ValidationQueueDto>> QueueAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<ValidationQueueDto>>(async (db, now) =>
        {
            var roles = await db.Roles.Where(r => r.IsValidator).ToListAsync(ct);
            if (roles.Count == 0) return [];

            var candidates = await db.Tasks.Include(t => t.Project)
                .Where(t => t.State == TaskState.Validating && t.CurrentSubjectId != null).ToListAsync(ct);
            var validating = new List<int>();
            foreach (var task in candidates)
                if (await Validations.CompatibleAsync(db, task, ct)) validating.Add(task.Id);
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
    /// Tasks in 'validating' that a human has said need them, longest-waiting first within a priority. Nothing
    /// advances one of these: the conductor skips an attended task on purpose, so unless somebody is told, it
    /// waits until somebody happens to read the board.
    /// </summary>
    /// <remarks>
    /// Uncapped, deliberately: this set is bounded by the work in flight and nothing an agent does can inflate
    /// it, so the list is also the count and the badge and the page cannot disagree.
    /// </remarks>
    public Task<IReadOnlyList<TaskDto>> AttendedAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<TaskDto>>(async (db, _) =>
        {
            var tasks = await db.Tasks.Include(t => t.Project).Include(t => t.Owner)
                .Where(t => t.State == TaskState.Validating && t.AttendedReason != null)
                .OrderBy(t => t.Id).ToListAsync(ct);
            var validations = await Validations.ForTasksAsync(db, tasks.Select(t => t.Id).ToList(), ct);
            // Priority first, as every other queue someone picks work off does; the numeric id breaks the last
            // tie by being the order the rows arrived in, which a stable sort keeps without parsing "T-n" back.
            var result = new List<TaskDto>();
            foreach (var task in tasks) result.Add(await Validations.MapAsync(db, task, validations.GetValueOrDefault(task.Id), ct));
            return result
                .OrderByDescending(t => t.Priority)
                .ThenBy(WaitingSince)
                .ToList();
        }, ct);

    /// <summary>
    /// When this task started waiting on a person: when its validation round opened. A task that re-enters
    /// 'validating' starts a new round and a new clock, which is right — the wait being reported is the wait
    /// for the verdict that is outstanding now, not the age of the task.
    /// </summary>
    /// <remarks>
    /// Falls back to <see cref="TaskDto.UpdatedAt"/> when no verdict is pending. The state machine says a task
    /// in 'validating' always has one, so the fallback is unreachable — but it is here rather than a
    /// <c>Min()</c> that would throw, because this runs inside a dashboard panel and an empty sequence would
    /// take the page down rather than show a row with a wrong age on it.
    /// </remarks>
    public static DateTimeOffset WaitingSince(TaskDto task) =>
        task.Validations.Where(IsPending).Select(v => v.WaitingSince).DefaultIfEmpty(task.UpdatedAt).Min();

    /// <summary>
    /// A verdict nobody has given yet. Compared against the wire word the read itself writes, so renaming it
    /// cannot leave this looking for a word nothing produces.
    /// </summary>
    internal static bool IsPending(ValidationDto validation) =>
        string.Equals(validation.Verdict, Verdict.Pending.ToWire(), StringComparison.Ordinal);

    /// <summary>
    /// Takes one (task, role) pair so no second validator spends a session on it. The claim is a lease: it lapses
    /// if the validator stops showing signs of life, and the sweep hands the pair back.
    /// </summary>
    public async Task<TaskDto> ClaimValidationAsync(Caller caller, string id, ClaimValidationRequest request, CancellationToken ct = default)
    {
        // A claim is an agent taking work. The founder holds no roles and so has nothing to claim with.
        var agentId = caller.RequireAgent();
        var validator = (request.Validator ?? "").Trim().ToLowerInvariant();
        MuthurException? refusal = null;
        var result = await ledger.MutateAsync<TaskDto?>(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            if (request.SubjectId is { } requested && requested != task.CurrentSubjectId)
                throw Fail.Conflict("stale_validation_subject", "This validation round is closed or superseded. Claim the current round and inspect its exact implementation SHA.");
            if (task.State != TaskState.Validating)
                throw Fail.Rule("not_validating", $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}', not awaiting validation.");
            var row = await LoadRowAsync(m, task, validator, ct);
            refusal = await CheckSubjectAsync(m, task, ct);
            if (refusal is not null) return null;
            if (row.SubjectId != task.CurrentSubjectId)
                throw Fail.Conflict("stale_validation_subject", "This claim belongs to a superseded validation round.");
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
                m.Record("validation.claimed", task.Id, new { validator, by = caller.Name, subject = task.CurrentSubject!.ToDto() });

            await m.Db.SaveChangesAsync(ct);
            var validations = await Validations.ForTasksAsync(m.Db, [task.Id], ct);
            return await Validations.MapAsync(m.Db, task, validations.GetValueOrDefault(task.Id), ct);
        }, ct);
        if (refusal is not null) throw refusal;
        return result!;
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
            return await Validations.MapAsync(m.Db, task, validations.GetValueOrDefault(task.Id), ct);
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

    /// <summary><paramref name="outcome"/> is the verdict being recorded: yes, no, or blocked — the validator could not run at all.</summary>
    public async Task<TaskDto> VerdictAsync(Caller caller, string id, VerdictRequest request, Verdict outcome, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        var recorded = outcome switch
        {
            Verdict.Yes => "validation.passed",
            Verdict.No => "validation.failed",
            Verdict.Blocked => "validation.blocked",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A recorded verdict is yes, no or blocked."),
        };
        var validator = (request.Validator ?? "").Trim().ToLowerInvariant();
        MuthurException? refusal = null;
        var result = await ledger.MutateAsync<TaskDto?>(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            refusal = request.SubjectId is null
                ? Fail.Rule("validation_subject_required", "Retain the subject ID returned by validate claim and pass --subject <guid> with every verdict. Upgrade older clients; never fetch a replacement subject at verdict time.")
                : request.SubjectId != task.CurrentSubjectId || task.State != TaskState.Validating
                    ? Fail.Conflict("stale_validation_subject", "This validation round is closed or superseded. Claim and review the current round before submitting a new verdict.")
                    : !Validations.UsefulEvidence(request.Evidence) ? Fail.Rule("evidence_required", Validations.EvidenceHelp) : null;
            if (refusal is not null)
            {
                m.Record("validation.verdict_refused", task.Id, new { subjectId = request.SubjectId, validator, code = refusal.Code });
                return null;
            }
            refusal = await CheckSubjectAsync(m, task, ct);
            if (refusal is not null) return null;
            var row = await LoadRowAsync(m, task, validator, ct);
            if (row.SubjectId != request.SubjectId)
            {
                refusal = Fail.Conflict("stale_validation_subject", "The claim belongs to a superseded round.");
                m.Record("validation.verdict_refused", task.Id, new { subjectId = request.SubjectId, validator, code = refusal.Code });
                return null;
            }

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

            if (outcome == Verdict.Yes) await DesignService.RequireComparisonAsync(m.Db, task, caller.Name, ct);
            row.Verdict = outcome;
            row.Evidence = request.Evidence!.Trim();
            row.AgentId = caller.AgentId;
            row.At = m.Now;
            ClearClaim(row);   // decided: nobody is working this pair any more
            task.UpdatedAt = m.Now;
            m.Record(recorded, task.Id, new { validator, by = caller.Name, evidence = row.Evidence, subject = task.CurrentSubject!.ToDto() });

            await m.Db.SaveChangesAsync(ct);
            var rows = await m.Db.TaskValidations.Where(v => v.TaskId == task.Id).ToListAsync(ct);
            if (outcome == Verdict.No)
            {
                task.ValidationInvalidationReason = "Validation failed.";
                m.Record("validation.subject_invalidated", task.Id, new { subjectId = task.CurrentSubjectId, reason = task.ValidationInvalidationReason });
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
            else if (outcome == Verdict.Blocked)
            {
                foreach (var other in rows) ClearClaim(other);
                task.ValidationInvalidationReason = "Validation blocked.";
                m.Record("validation.subject_invalidated", task.Id, new { subjectId = task.CurrentSubjectId, reason = task.ValidationInvalidationReason });
                // The same return to the owner a failure does, and the reason a blocked task is heard once rather
                // than restaffed forever: the conductor staffs only Validating, so nothing picks this up again.
                task.State = TaskState.InProgress;
                task.ClaimExpires = m.Now + leases.ClaimLease;
                m.Record("task.validation_blocked", task.Id, new { validator, owner = task.Owner?.Name });
                if (task.Owner is { } owner)
                    MessageService.PostFromHub(m, Recipient.Agent, owner.Name,
                        $"{Wire.TaskId(task.Id)} could not be validated by {validator}: it is back in progress with you. " +
                        $"This is not a verdict on the work. Why: muthur task show {Wire.TaskId(task.Id)}", task.Id);

                // Including this one. A second session that could not reach a verdict had nothing available to
                // it that the first did not, so the task stops being staffed rather than spending the budget
                // rediscovering the same prerequisite every round the owner re-implements.
                var blocks = 1 + await m.Db.Events.CountAsync(e => e.TaskId == task.Id && e.Type == "task.validation_blocked", ct);
                var first = Evidence.FirstLine(request.Evidence);

                // The conductor already leaves an attended task alone (T-31), so setting the flag is the whole
                // mechanism; there is deliberately no second way to skip. An orchestrator's own reason is left
                // as it stands — a human said why a human is needed, and that sentence is better than this one.
                if (blocks >= 2 && string.IsNullOrWhiteSpace(task.AttendedReason))
                {
                    task.AttendedReason = $"Blocked twice without reaching a verdict. Latest: {first}";
                    m.Record("task.attended", task.Id, new { reason = task.AttendedReason, source = "validation_blocked", blocks });
                }

                // And the founder: that a task needs something the organization cannot supply unattended is a fact
                // only they can act on, and the failure this closes was exactly that it reached one inbox and stopped.
                // Twice is where it becomes a class of thing rather than an incident; past that the flag is set and
                // the founder has been told, so a task still being blocked is the flag working, not news.
                if (blocks == 1)
                    MessageService.PostFromHub(m, Recipient.Founder, null,
                        $"{Wire.TaskId(task.Id)} \"{task.Title}\" needs something a validator could not supply: " +
                        $"{first}. It is back with {task.Owner?.Name ?? "its owner"}. " +
                        $"Full evidence: muthur task show {Wire.TaskId(task.Id)}", task.Id);
                else if (blocks == 2)
                    MessageService.PostFromHub(m, Recipient.Founder, null,
                        $"{Wire.TaskId(task.Id)} \"{task.Title}\" has now been blocked twice without a verdict: {first}. " +
                        $"It is flagged for a human validator and will not be staffed again until that is lifted " +
                        $"(muthur task attended {Wire.TaskId(task.Id)} --clear). " +
                        $"Full evidence: muthur task show {Wire.TaskId(task.Id)}", task.Id);
            }
            else if (task.CurrentSubject!.ToDto().RequiredValidators.All(key => rows.Any(v => v.ValidatorKey == key
                && v.SubjectId == task.CurrentSubjectId && v.Verdict == Verdict.Yes && Validations.UsefulEvidence(v.Evidence))))
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
            return await Validations.MapAsync(m.Db, task, validations.GetValueOrDefault(task.Id), ct);
        }, ct);
        if (refusal is not null) throw refusal;
        return result!;
    }

    /// <summary>
    /// Approval, bounded git integration and recording hold the ledger writer together. Other mutations wait
    /// so an owner or policy update cannot change the approved subject during integration.
    /// </summary>
    public async Task<TaskDto> LandAsync(Caller caller, string id, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        await _landing.WaitAsync(ct);
        try
        {
            LandResult result = null!;
            MuthurException? refusal = null;
            // The intent must commit before the Git side effect so a crash can be reconciled exactly once.
            refusal = await ledger.MutateAsync<MuthurException?>(caller, async m =>
            {
                var task = await TaskService.LoadAsync(m.Db, id, ct);
                TaskService.RequireOwnerOrFounder(task, caller);
                if (task.Project!.LandMode != LandMode.Merge || task.State != TaskState.Validated) return null;
                if (await CheckSubjectAsync(m, task, ct) is { } subjectError) return subjectError;
                if (await integration.RequirePassedAsync(m, task, ct) is { } candidateError) return candidateError;
                var candidate = task.CurrentIntegrationCandidate!;
                if (candidate.PromotionIntentAt is null)
                {
                    candidate.PromotionIntentAt = m.Now;
                    m.Record("integration.promotion_intent", task.Id, new { candidate.Id, candidate.SubjectId, candidate.TargetSha, candidate.CandidateSha, candidate.EvidenceSha256 });
                }
                return null;
            }, ct);
            if (refusal is not null) throw refusal;
            var dto = await ledger.MutateAsync<TaskDto?>(caller, async m =>
            {
                var current = await TaskService.LoadAsync(m.Db, id, ct);
                TaskService.RequireOwnerOrFounder(current, caller);
                if (current.State == TaskState.Done && current.CurrentIntegrationCandidate is { State: "promoted" } promoted)
                {
                    result = new LandResult(LandOutcome.Landed, Commit: promoted.CandidateSha);
                    return await Validations.MapAsync(m.Db, current, null, ct);
                }
                if (current.CurrentSubject is null) throw Fail.Rule("validation_provenance_unknown", Validations.Recovery);
                if (current.State != TaskState.Validated)
                    throw Fail.Rule("not_validated", $"{Wire.TaskId(current.Id)} is '{current.State.ToWire()}'. Only a validated task can land.");
                refusal = await CheckSubjectAsync(m, current, ct);
                if (refusal is not null) return null;
                if (string.IsNullOrWhiteSpace(current.Branch))
                    throw Fail.Rule("branch_required", $"{Wire.TaskId(current.Id)} has no branch recorded.");
                var rows = await m.Db.TaskValidations.Where(v => v.TaskId == current.Id).ToListAsync(ct);
                if (!current.CurrentSubject!.ToDto().RequiredValidators.All(key => rows.Any(v => v.ValidatorKey == key
                    && v.SubjectId == current.CurrentSubjectId && v.Verdict == Verdict.Yes && Validations.UsefulEvidence(v.Evidence))))
                    throw Fail.Rule("not_validated", "Every required validator must pass this exact subject with useful evidence. " + Validations.Recovery);
                if (current.Project!.LandMode == LandMode.Merge)
                {
                    refusal = await integration.RequirePassedAsync(m, current, ct);
                    if (refusal is not null) return null;
                }
                result = await lander.LandAsync(current.Project!, current, current.Owner?.Name ?? caller.Name, ct);
                switch (result.Outcome)
                {
                    case LandOutcome.Landed:
                        // Read before the state change, and never a refusal: a hold is information the lander
                        // weighs, not a gate. The founder's answer on T-16 and again on request #16 - do not
                        // prevent the collision, make it visible and make the bounce cheap.
                        var held = TaskService.LiveHold(current, m.Now, options.HoldMinutes);
                        current.State = TaskState.Done;
                        current.DoneAt = m.Now;
                        current.PrUrl = result.PrUrl ?? current.PrUrl;
                        if (result.PrUrl is null && current.CurrentIntegrationCandidate is { } candidate)
                        {
                            candidate.State = "promoted";
                            candidate.PromotedAt = m.Now;
                            m.Record("integration.promoted", current.Id, new { candidate.Id, candidate.SubjectId, candidate.TargetSha, candidate.CandidateSha, candidate.EvidenceSha256 });
                        }
                        m.Record(result.PrUrl is null ? "task.landed" : "task.pr_opened", current.Id,
                            new
                            {
                                mode = current.Project!.LandMode.ToWire(), current.Branch, commit = result.Commit, prUrl = result.PrUrl,
                                subject = current.CurrentSubject!.ToDto(),
                                integrationStatus = result.PrUrl is null ? "passed" : "delegated_external_checks_unknown",
                                overrodeHold = held is { } h ? new { by = h.By, reason = h.Reason, placedAt = h.PlacedAt } : null,
                            });
                        if (held is { } hold)
                        {
                            // A row of its own, because "a hold a land later overrode" is the thing request #16
                            // asked to become countable, and a field inside another event is harder to count.
                            m.Record("task.hold_overridden", current.Id,
                                new { by = hold.By, reason = hold.Reason, placedAt = hold.PlacedAt, landedBy = caller.Name });
                            // Who, when, and the reason verbatim - "this task has a hold" tells a reader nothing
                            // they can weigh.
                            var said = $"{caller.Name} landed {Wire.TaskId(current.Id)}, which {hold.By} held " +
                                $"{Components.Shared.Format.Age(hold.PlacedAt, m.Now)} ago: \"{hold.Reason}\".";
                            MessageService.PostFromHub(m, Recipient.Founder, null, said, current.Id);
                            if (!string.Equals(hold.By, caller.Name, StringComparison.Ordinal))
                                MessageService.PostFromHub(m, Recipient.Agent, hold.By, said, current.Id);
                        }
                        break;
                    case LandOutcome.Conflict:
                        current.ValidationInvalidationReason = result.Message;
                        m.Record("validation.subject_invalidated", current.Id, new { subjectId = current.CurrentSubjectId, reason = result.Message });
                        current.State = TaskState.InProgress;
                        current.ClaimExpires = m.Now + leases.ClaimLease;
                        m.Record("task.land_failed", current.Id, new
                        {
                            code = result.Code,
                            result.Message,
                            branch = current.Branch,
                            target = current.Project!.DefaultBranch,
                            files = result.Files ?? [],
                            landedSince = result.LandedSince ?? [],
                        });
                        break;
                    default:
                        if (result.Code == "implementation_changed")
                        {
                            current.ValidationInvalidationReason = result.Message;
                            m.Record("validation.subject_invalidated", current.Id, new { subjectId = current.CurrentSubjectId, reason = result.Message });
                        }
                        m.Record("task.land_refused", current.Id, new { code = result.Code, result.Message });
                        break;
                }
                current.UpdatedAt = m.Now;
                var validations = await Validations.ForTasksAsync(m.Db, [current.Id], ct);
                return await Validations.MapAsync(m.Db, current, validations.GetValueOrDefault(current.Id), ct);
            }, ct);

            if (refusal is not null) throw refusal;
            return result.Outcome switch
            {
                LandOutcome.Landed => dto!,
                LandOutcome.Conflict => throw Fail.Conflict(result.Code!, result.Message!),
                _ => throw Fail.Rule(result.Code!, result.Message!),
            };
        }
        finally
        {
            _landing.Release();
        }
    }

    public Task<TaskDto> RevalidateAsync(Caller caller, string id, RevalidateRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            TaskService.RequireOwnerOrFounder(task, caller);
            if (task.State is not (TaskState.Validating or TaskState.Validated or TaskState.InProgress))
                throw Fail.Rule("cannot_revalidate", "Only validating, validated or in-progress tasks can be revalidated.");
            if (string.IsNullOrWhiteSpace(request.Reason)) throw Fail.Rule("reason_required", "Pass --reason <text> explaining the fresh validation round.");
            var subjectId = task.CurrentSubjectId;
            foreach (var row in await m.Db.TaskValidations.Where(v => v.TaskId == task.Id).ToListAsync(ct)) ClearClaim(row);
            task.CurrentSubject = null;
            task.CurrentSubjectId = null;
            task.ValidationInvalidationReason = request.Reason.Trim();
            task.State = TaskState.InProgress;
            task.ClaimExpires = m.Now + leases.ClaimLease;
            task.UpdatedAt = m.Now;
            m.Record("validation.subject_invalidated", task.Id, new { subjectId, reason = task.ValidationInvalidationReason });
            m.Record("task.revalidation_requested", task.Id, new { subjectId, reason = task.ValidationInvalidationReason });
            await m.Db.SaveChangesAsync(ct);
            var validations = await Validations.ForTasksAsync(m.Db, [task.Id], ct);
            return await Validations.MapAsync(m.Db, task, validations.GetValueOrDefault(task.Id), ct);
        }, ct);

    private static async Task<TaskValidation> LoadRowAsync(Mutation m, WorkTask task, string validator, CancellationToken ct) =>
        await m.Db.TaskValidations.Include(v => v.ClaimedBy)
            .SingleOrDefaultAsync(v => v.TaskId == task.Id && v.ValidatorKey == validator, ct)
        ?? throw Fail.Rule("validator_not_required", $"'{validator}' is not a required validator of {Wire.TaskId(task.Id)}.");

    private async Task<MuthurException?> CheckSubjectAsync(Mutation m, WorkTask task, CancellationToken ct)
    {
        try
        {
            await Validations.RequireCurrentAsync(m.Db, task, lander, ct);
            return null;
        }
        catch (MuthurException ex) when (ex.Code == "validation_subject_changed")
        {
            if (task.ValidationInvalidationReason is null)
            {
                task.ValidationInvalidationReason = ex.Message;
                m.Record("validation.subject_invalidated", task.Id, new { subjectId = task.CurrentSubjectId, reason = ex.Message });
            }
            return ex;
        }
    }

    private static void ClearClaim(TaskValidation row)
    {
        row.ClaimedByAgentId = null;
        row.ClaimedBy = null;
        row.ClaimExpires = null;
    }
}
