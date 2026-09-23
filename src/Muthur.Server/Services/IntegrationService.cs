using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed class IntegrationService(Ledger ledger, ITaskLander lander, MuthurOptions options, LeasePolicy leases)
{
    private static readonly string[] ActiveStates = ["assigned", "checking", "passed"];
    private static MuthurException Stale() => Fail.Conflict("stale_integration_candidate", "The integration assignment or its approved inputs are no longer current.");
    private static MuthurException Forbidden() => Fail.Rule("integration_runner_forbidden", "Only the assigned caller may act on this integration assignment.");
    private static MuthurException Invalid() => Fail.Rule("integration_candidate_invalid", "Candidate identity, tree and ordered parents must match the immutable assignment.");
    private static MuthurException Incomplete() => Fail.Rule("integration_evidence_incomplete", "Provide the exact build/test commands, outcomes, timestamps, checkout and artifact digests for this assignment.");
    private TimeSpan Lease => TimeSpan.FromMinutes(options.ConductorOrchestratorMinutes + 1);

    // A refusal may follow a durable invalidation or owner recovery. Throw only after the mutation commits.
    private async Task<T> MutateAsync<T>(Caller caller, Func<Mutation, Task<T>> action, CancellationToken ct)
    {
        MuthurException? refusal = null;
        var result = await ledger.MutateAsync(caller, async m =>
        {
            try { return await action(m); }
            catch (MuthurException ex) { refusal = ex; return default; }
        }, ct);
        if (refusal is not null) throw refusal;
        return result!;
    }

    public Task AuthorizeRequestAsync(Caller caller, string method, string path, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) =>
        {
            if (!caller.IntegrationRunner) return true;
            if (method == "POST" && path == Routes.Agents + "/heartbeat") return true;
            var candidates = await db.IntegrationCandidates.Where(c => c.AssignedAgentId == caller.AgentId).ToListAsync(ct);
            var taskIds = candidates.Select(c => c.TaskId).Distinct().ToList();
            var current = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(ct);
            foreach (var task in current.Where(t => candidates.Any(c => c.Id == t.CurrentIntegrationCandidateId)))
            {
                var root = Routes.TaskIntegration(Wire.TaskId(task.Id));
                if (method == "GET" && path == root) return true;
                if (method == "POST" && new[] { "started", "candidate", "verdict", "failure", "renew" }
                    .Any(action => path == root + "/" + action)) return true;
            }
            throw Fail.Unauthorized("Integration runners may access only their assigned integration and heartbeat.");
        }, ct);

    public Task<IntegrationHistoryDto> ShowAsync(Caller caller, string id, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) =>
        {
            var task = await TaskService.LoadAsync(db, id, ct);
            if (caller.IntegrationRunner && (caller.AgentId is null || task.CurrentIntegrationCandidate?.AssignedAgentId != caller.AgentId))
                throw Forbidden();
            var history = await db.IntegrationCandidates.Where(c => c.TaskId == task.Id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Attempt).ToListAsync(ct);
            return new IntegrationHistoryDto(task.CurrentIntegrationCandidate?.ToDto(), history.Select(c => c.ToDto()).ToList());
        }, ct);

    public Task<IntegrationAssignmentDto> ClaimAsync(Caller caller, string id, CancellationToken ct = default) =>
        ClaimCoreAsync(caller, id, false, ct);

    internal Task<IntegrationAssignmentDto> AssignRunnerAsync(Caller runner, string id, CancellationToken ct = default) =>
        ClaimCoreAsync(runner, id, true, ct);

    private Task<IntegrationAssignmentDto> ClaimCoreAsync(Caller caller, string id, bool internalAssignment, CancellationToken ct) =>
        MutateAsync(caller, async m =>
        {
            caller.RequireIdentified();
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            var agent = caller.AgentId is { } agentId ? await m.Db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, ct) : null;
            if (internalAssignment)
            {
                if (agent?.IntegrationRunner != true || !caller.IntegrationRunner || task.OwnerAgentId == agent.Id) throw Forbidden();
            }
            else
            {
                if (caller.IntegrationRunner || agent?.IntegrationRunner == true) throw Forbidden();
                if (!caller.IsFounder)
                {
                    if (agent?.Tier != "mastermind" || task.OwnerAgentId == agent.Id || task.CurrentSubject is null) throw Forbidden();
                    var roles = task.CurrentSubject.ToDto().RequiredValidators;
                    if (!await m.Db.RoleHolds.AnyAsync(h => h.AgentId == agent.Id && roles.Contains(h.RoleKey) && h.LeaseExpires > m.Now, ct)) throw Forbidden();
                }
            }
            await SweepAsync(m, ct);
            var active = await m.Db.IntegrationCandidates.Where(c => ActiveStates.Contains(c.State)).ToListAsync(ct);
            active = active.Where(c => ActiveStates.Contains(c.State)).ToList();
            if (active.Count > 0)
            {
                var existing = active.Single();
                if (existing.TaskId == task.Id && Owns(caller, existing)) return Assignment(existing);
                throw Fail.Conflict("integration_busy", "Another integration candidate occupies the global slot.");
            }
            await RequireRetryAsync(m, task, ct);
            await RequireEligibleAsync(m, task, ct);
            var waiting = await m.Db.Tasks.Include(t => t.Project).Where(t => t.State == TaskState.Validated).ToListAsync(ct);
            var eligible = new List<WorkTask>();
            foreach (var other in waiting)
            {
                try { await RequireEligibleAsync(m, other, ct); await RequireRetryAsync(m, other, ct); eligible.Add(other); }
                catch (MuthurException) { /* Ineligible tasks do not hold the queue. */ }
            }
            var events = await m.Db.Events.Where(e => e.Type == "task.validated" || e.Type == "integration.invalidated" || e.Type == "integration.interrupted").ToListAsync(ct);
            long Sequence(WorkTask t) => events.Where(e => e.TaskId == t.Id).Select(e => e.Seq).DefaultIfEmpty(long.MaxValue).Max();
            // Transitions from this sweep have not received database sequence numbers yet; they go last.
            var next = eligible.OrderBy(t => m.Recorded.Any(e => e.TaskId == t.Id && e.Type is "integration.invalidated" or "integration.interrupted") ? long.MaxValue : Sequence(t)).ThenBy(t => t.Id).FirstOrDefault();
            if (next?.Id != task.Id) throw Fail.Conflict("integration_not_next", "Another eligible task precedes this task in the integration queue.");
            var subject = task.CurrentSubject!;
            var target = await lander.BranchHeadAsync(task.Project!, task.Project!.DefaultBranch, ct) ?? throw Stale();
            var attempts = await m.Db.IntegrationCandidates.Where(c => c.SubjectId == subject.Id).Select(c => c.Attempt).ToListAsync(ct);
            var candidate = new IntegrationCandidate
            {
                Id = Guid.NewGuid(), AssignmentId = Guid.NewGuid(), ProjectId = task.ProjectId, TaskId = task.Id,
                SubjectId = subject.Id, RepositoryPath = subject.RepositoryPath, DefaultBranch = task.Project.DefaultBranch,
                TargetSha = target, ImplementationSha = subject.ImplementationSha, RequiredChecksJson = subject.RequiredChecksJson,
                AssignedAgentId = caller.AgentId, LeaseExpires = m.Now + Lease, Attempt = attempts.DefaultIfEmpty(0).Max() + 1, CreatedAt = m.Now,
            };
            m.Db.IntegrationCandidates.Add(candidate);
            task.CurrentIntegrationCandidate = candidate;
            task.CurrentIntegrationCandidateId = candidate.Id;
            Record(m, candidate, "assigned");
            return Assignment(candidate);
        }, ct);

    private IntegrationAssignmentDto Assignment(IntegrationCandidate c) => new(c.ToDto(), checked(options.ConductorOrchestratorMinutes * 60));
    private static bool Owns(Caller caller, IntegrationCandidate c) => c.AssignedAgentId is { } id
        ? caller.IsAgent && caller.AgentId == id : caller.IsFounder && !caller.IntegrationRunner;

    private async Task RequireEligibleAsync(Mutation m, WorkTask task, CancellationToken ct)
    {
        if (task.State != TaskState.Validated || task.Project!.LandMode != LandMode.Merge) throw Stale();
        await RequireInputsAsync(m, task, ct);
        var checks = task.CurrentSubject!.ToDto().RequiredChecks;
        if (string.IsNullOrWhiteSpace(checks.Build) || string.IsNullOrWhiteSpace(checks.Test))
            throw Fail.Rule("integration_checks_missing", "The approved subject must specify both build and test commands.");
    }

    private async Task RequireInputsAsync(Mutation m, WorkTask task, CancellationToken ct)
    {
        try { await Validations.RequireCurrentAsync(m.Db, task, lander, ct); }
        catch (MuthurException) { throw Stale(); }
        var subject = task.CurrentSubject!;
        if (task.Branch is null || await lander.BranchHeadAsync(task.Project!, task.Branch, ct) != subject.ImplementationSha) throw Stale();
        var rows = await m.Db.TaskValidations.Where(v => v.TaskId == task.Id).ToListAsync(ct);
        if (!subject.ToDto().RequiredValidators.All(role => rows.Any(v => v.ValidatorKey == role && v.SubjectId == subject.Id
            && v.Verdict == Verdict.Yes && Validations.UsefulEvidence(v.Evidence)
            && (v.AgentId is null || v.AgentId != task.OwnerAgentId)))
            || rows.Any(v => v.SubjectId == subject.Id && (v.Verdict != Verdict.Yes || !Validations.UsefulEvidence(v.Evidence)))) throw Stale();
    }

    private async Task RequireRetryAsync(Mutation m, WorkTask task, CancellationToken ct)
    {
        var previous = await m.Db.IntegrationCandidates.Where(c => c.SubjectId == task.CurrentSubjectId).OrderByDescending(c => c.Attempt).FirstOrDefaultAsync(ct);
        if (previous is null) return;
        if (previous.Attempt >= options.ConductorMaxAttempts)
        {
            ReturnToOwner(m, task, "Integration retry limit reached. " + Validations.Recovery);
            throw Fail.Rule("integration_retry_exhausted", "Integration retry limit reached. " + Validations.Recovery);
        }
        if (previous.State is not ("invalidated" or "interrupted")) throw Stale();
        if (previous.CompletedAt + TimeSpan.FromMinutes(options.ConductorStallProbeMinutes) > m.Now)
            throw Fail.Conflict("integration_retry_cooldown", "Integration retry is waiting for the configured cooldown.");
    }

    internal Task SweepAsync(CancellationToken ct = default) => ledger.MutateAsync(Caller.System, m => SweepAsync(m, ct), ct);

    internal Task<WorkTask?> NextAsync(IReadOnlySet<string> budgetBlocked, CancellationToken ct = default) =>
        ledger.MutateAsync<WorkTask?>(Caller.System, async m =>
        {
            await SweepAsync(m, ct);
            var active = await m.Db.IntegrationCandidates.Where(c => ActiveStates.Contains(c.State)).ToListAsync(ct);
            if (active.Any(c => ActiveStates.Contains(c.State))) return null;
            var tasks = await m.Db.Tasks.Include(t => t.Project).Where(t => t.State == TaskState.Validated && t.Project!.LandMode == LandMode.Merge).ToListAsync(ct);
            var eligible = new List<WorkTask>();
            foreach (var task in tasks)
            {
                if (budgetBlocked.Contains(Wire.TaskId(task.Id) + "/#integration")) continue;
                try { await RequireEligibleAsync(m, task, ct); await RequireRetryAsync(m, task, ct); eligible.Add(task); }
                catch (MuthurException) { }
            }
            var events = await m.Db.Events.Where(e => e.Type == "task.validated" || e.Type == "integration.invalidated" || e.Type == "integration.interrupted").ToListAsync(ct);
            return eligible.OrderBy(t => events.Where(e => e.TaskId == t.Id).Select(e => e.Seq).DefaultIfEmpty(long.MaxValue).Max()).ThenBy(t => t.Id).FirstOrDefault();
        }, ct);

    internal async Task SweepAsync(Mutation m, CancellationToken ct)
    {
        var active = await m.Db.IntegrationCandidates.Where(c => ActiveStates.Contains(c.State)).ToListAsync(ct);
        foreach (var c in active)
        {
            var task = await TaskService.LoadAsync(m.Db, Wire.TaskId(c.TaskId), ct);
            await ValidateCurrentAsync(m, task, c, ct);
        }
    }

    /// <summary>Uses the caller's mutation; callers must commit a returned refusal before throwing it.</summary>
    public async Task<MuthurException?> ValidateCurrentAsync(Mutation m, WorkTask task, IntegrationCandidate c, CancellationToken ct = default)
    {
        MuthurException? refusal = null;
        if (task.CurrentIntegrationCandidateId != c.Id || task.CurrentSubjectId != c.SubjectId || task.ProjectId != c.ProjectId
            || task.Project!.LandMode != LandMode.Merge || task.Project.DefaultBranch != c.DefaultBranch
            || task.CurrentSubject?.RepositoryPath != c.RepositoryPath || task.CurrentSubject?.ImplementationSha != c.ImplementationSha
            || task.CurrentSubject?.RequiredChecksJson != c.RequiredChecksJson
            || (task.State != TaskState.Validated && !(task.State == TaskState.InProgress && c.State == "failed"))) refusal = Stale();
        else
        {
            try { await RequireInputsAsync(m, task, ct); }
            catch (MuthurException ex) { refusal = ex; }
        }
        var target = refusal is null ? await lander.BranchHeadAsync(task.Project!, c.DefaultBranch, ct) : null;
        if (refusal is null && target != c.TargetSha)
        {
            var recoveringPromotion = target is not null && c.State == "passed" && c.PromotionIntentAt is not null && c.CandidateSha is not null
                && await lander.IsAncestorAsync(task.Project!, c.CandidateSha, target, ct);
            if (!recoveringPromotion) refusal = Fail.Conflict("integration_target_changed", "The default branch moved. Check a new candidate against its current revision.");
        }
        if (refusal is not null)
        {
            Invalidate(m, c, refusal.Code, refusal.Message);
            if (c.Attempt >= options.ConductorMaxAttempts && task.State == TaskState.Validated
                && task.CurrentSubjectId == c.SubjectId && task.CurrentIntegrationCandidateId == c.Id)
                ReturnToOwner(m, task, "Integration retry limit reached. " + Validations.Recovery);
            return refusal;
        }
        if (c.State is "assigned" or "checking" && c.LeaseExpires <= m.Now)
        {
            Complete(m, c, "interrupted", "runner_timeout", "Integration lease expired; preserve artifacts and retry after cooldown.");
            if (c.Attempt >= options.ConductorMaxAttempts) ReturnToOwner(m, task, "Integration retry limit reached. " + Validations.Recovery);
            return Stale();
        }
        return null;
    }

    public static void Invalidate(Mutation m, IntegrationCandidate c, string code, string message)
    {
        if (ActiveStates.Contains(c.State)) Complete(m, c, "invalidated", code, message);
    }

    public async Task<MuthurException?> RequirePassedAsync(Mutation m, WorkTask task, CancellationToken ct = default)
    {
        if (task.CurrentIntegrationCandidate is not { } c) return Fail.Rule("integration_required", "A current passing integration candidate is required.");
        if (await ValidateCurrentAsync(m, task, c, ct) is { } refusal) return refusal;
        if (c.State != "passed") return Fail.Rule("integration_required", "A current passing integration candidate is required.");
        if (c.CandidateSha is null || c.TreeSha is null || c.EvidenceJson is null || Validations.Hash(c.EvidenceJson) != c.EvidenceSha256) return Incomplete();
        try
        {
            var report = JsonSerializer.Deserialize(c.EvidenceJson, MuthurJsonContext.Default.IntegrationEvidenceDto);
            if (report is null || !ValidateEvidence(c, report, m.Now)) return Incomplete();
            await RequireStructureAsync(task.Project!, c, new(c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, c.CandidateSha, c.TreeSha, c.AlreadyIncluded), ct);
        }
        catch (MuthurException ex) { return ex; }
        catch (JsonException) { return Incomplete(); }
        return null;
    }

    private Task<IntegrationCandidateDto> UpdateAsync(Caller caller, string id, Guid assignment, Guid subject,
        Func<Mutation, WorkTask, IntegrationCandidate, Task> action, CancellationToken ct) => MutateAsync(caller, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, id, ct);
            var c = task.CurrentIntegrationCandidate;
            if (c is null || c.AssignmentId != assignment || c.SubjectId != subject) throw Stale();
            if (!Owns(caller, c) || task.OwnerAgentId == caller.AgentId && !caller.IsFounder) throw Forbidden();
            if (await ValidateCurrentAsync(m, task, c, ct) is { } refusal) throw refusal;
            await action(m, task, c);
            if (await ValidateCurrentAsync(m, task, c, ct) is { } changed) throw changed;
            return c.ToDto();
        }, ct);

    private static void RequireActive(IntegrationCandidate c)
    {
        if (c.State is not ("assigned" or "checking")) throw Stale();
    }

    public Task<IntegrationCandidateDto> RenewAsync(Caller caller, string id, IntegrationRenewRequest request, CancellationToken ct = default) =>
        UpdateAsync(caller, id, request.AssignmentId, request.SubjectId, (m, _, c) =>
        {
            RequireActive(c);
            c.LeaseExpires = m.Now + Lease;
            Record(m, c, "renewed");
            return Task.CompletedTask;
        }, ct);

    public Task<IntegrationCandidateDto> StartedAsync(Caller caller, string id, IntegrationStartRequest request, CancellationToken ct = default) =>
        UpdateAsync(caller, id, request.AssignmentId, request.SubjectId, (m, _, c) =>
        {
            RequireActive(c);
            if (request.ProcessId <= 0 || request.ProcessStartedAt == default || request.ProcessStartedAt > m.Now
                || !CanonicalPath(request.OwnedWorktreePath, Path.Combine(c.RepositoryPath, ".worktrees", $"integration-{c.Id:N}"))
                || !CanonicalPath(request.ArtifactsDirectory, Path.Combine(c.RepositoryPath, ".work", "integration", $"{c.Id:N}"))) throw Invalid();
            if (c.RunnerProcessId is not null)
            {
                if (c.RunnerProcessId != request.ProcessId || c.RunnerStartedAt != request.ProcessStartedAt
                    || c.OwnedWorktreePath != request.OwnedWorktreePath || c.ArtifactsDirectory != request.ArtifactsDirectory)
                    throw Fail.Conflict("integration_candidate_invalid", "Different process metadata is already recorded.");
                return Task.CompletedTask;
            }
            c.RunnerProcessId = request.ProcessId;
            c.RunnerStartedAt = request.ProcessStartedAt;
            c.OwnedWorktreePath = request.OwnedWorktreePath;
            c.ArtifactsDirectory = request.ArtifactsDirectory;
            c.StartedAt = m.Now;
            Record(m, c, "started");
            return Task.CompletedTask;
        }, ct);

    private static bool CanonicalPath(string? supplied, string expected)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        try { return Path.IsPathFullyQualified(supplied) && supplied == Path.GetFullPath(expected) && supplied == Path.GetFullPath(supplied); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public Task<IntegrationCandidateDto> CandidateAsync(Caller caller, string id, IntegrationCandidateRequest request, CancellationToken ct = default) =>
        UpdateAsync(caller, id, request.AssignmentId, request.SubjectId, async (m, task, c) =>
        {
            await RequireStructureAsync(task.Project!, c, request, ct);
            if (c.CandidateSha is not null)
            {
                if (c.CandidateSha != request.CandidateSha || c.TreeSha != request.TreeSha || c.AlreadyIncluded != request.AlreadyIncluded)
                    throw Invalid();
                return;
            }
            RequireActive(c);
            c.CandidateSha = request.CandidateSha;
            c.TreeSha = request.TreeSha;
            c.AlreadyIncluded = request.AlreadyIncluded;
            c.State = "checking";
            Record(m, c, "candidate_recorded");
        }, ct);

    private async Task RequireStructureAsync(Project project, IntegrationCandidate c, IntegrationCandidateRequest request, CancellationToken ct)
    {
        if (request.TargetSha != c.TargetSha || request.ImplementationSha != c.ImplementationSha) throw Invalid();
        var inspection = await lander.InspectCommitAsync(project, request.CandidateSha, ct);
        if (inspection is null || inspection.Sha != request.CandidateSha || inspection.TreeSha != request.TreeSha) throw Invalid();
        if (request.AlreadyIncluded)
        {
            if (request.CandidateSha != c.TargetSha || !await lander.IsAncestorAsync(project, c.ImplementationSha, c.TargetSha, ct)) throw Invalid();
        }
        else if (!inspection.Parents.SequenceEqual(new[] { c.TargetSha, c.ImplementationSha })) throw Invalid();
    }

    public Task<IntegrationCandidateDto> VerdictAsync(Caller caller, string id, IntegrationEvidenceDto request, CancellationToken ct = default) =>
        UpdateAsync(caller, id, request.AssignmentId, request.SubjectId, async (m, task, c) =>
        {

            var json = JsonSerializer.Serialize(request, MuthurJsonContext.Default.IntegrationEvidenceDto);
            if (c.State is "passed" or "failed")
            {
                if (c.EvidenceJson != json) throw Fail.Conflict("stale_integration_candidate", "A different terminal report is already recorded.");
                if (c.CandidateSha is null || c.TreeSha is null) throw Incomplete();
                await RequireStructureAsync(task.Project!, c, new(c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, c.CandidateSha, c.TreeSha, c.AlreadyIncluded), ct);
                return;
            }
            RequireActive(c);
            var passing = ValidateEvidence(c, request, m.Now);
            if (c.CandidateSha is null || c.TreeSha is null) throw Incomplete();
            await RequireStructureAsync(task.Project!, c, new(c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, c.CandidateSha, c.TreeSha, c.AlreadyIncluded), ct);
            c.EvidenceJson = json;
            c.EvidenceSha256 = Validations.Hash(json);
            var code = request.CleanupSucceeded ? "integration_check_failed" : "integration_cleanup_failed";
            var failed = request.Checks.FirstOrDefault(check => check.ExitCode != 0 || check.Skipped);
            var message = passing ? null : $"{failed?.Name ?? "cleanup"}: {failed?.Command}; exit {failed?.ExitCode}; artifact {failed?.ArtifactReference}. {request.CleanupMessage} {Validations.Recovery}";
            Complete(m, c, passing ? "passed" : "failed", passing ? null : code, message);
            if (!passing) ReturnToOwner(m, task, message!);
        }, ct);

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidateEvidence(IntegrationCandidate c, IntegrationEvidenceDto r, DateTimeOffset now)
    {
        if (r.AssignmentId != c.AssignmentId || r.CandidateId != c.Id || r.SubjectId != c.SubjectId
            || c.CandidateSha is null || r.CheckoutSha != c.CandidateSha || r.Checks is not { Count: 2 }
            || r.StartedAt < c.CreatedAt || r.EndedAt <= r.StartedAt || r.EndedAt > now) throw Incomplete();
        var required = c.ToDto().RequiredChecks;
        if (string.IsNullOrWhiteSpace(required.Build) || string.IsNullOrWhiteSpace(required.Test)) throw Incomplete();
        for (var i = 0; i < 2; i++)
        {
            var check = r.Checks[i];
            if (check is null || check.Name != (i == 0 ? "build" : "test") || check.Command != (i == 0 ? required.Build : required.Test)
                || check.DurationMilliseconds < 0) throw Incomplete();
            if (check.Skipped)
            {
                if (i != 1 || r.Checks[0].ExitCode is null or 0 || check.ExitCode is not null || check.ArtifactSha256 is not null) throw Incomplete();
            }
            else if (check.ExitCode is null || string.IsNullOrWhiteSpace(check.ArtifactReference) || !Digest(check.ArtifactSha256)) throw Incomplete();
        }
        if (r.Checks[0].ExitCode != 0 && !r.Checks[1].Skipped) throw Incomplete();
        return r.CleanupSucceeded && r.Checks.All(check => !check.Skipped && check.ExitCode == 0);
    }

    public Task<IntegrationCandidateDto> FailureAsync(Caller caller, string id, IntegrationFailureRequest request, CancellationToken ct = default) =>
        UpdateAsync(caller, id, request.AssignmentId, request.SubjectId, (m, task, c) =>
        {
            var json = JsonSerializer.Serialize(request, MuthurJsonContext.Default.IntegrationFailureRequest);
            if (c.State == "failed")
            {
                if (c.FailureJson != json) throw Fail.Conflict("stale_integration_candidate", "A different terminal failure is already recorded.");
                return Task.CompletedTask;
            }
            if (request.Phase is not ("construction" or "checkout" or "launch")
                || request.Code is not ("merge_conflict" or "checkout_failed" or "shell_unsupported" or "runner_cancelled" or "runner_timeout" or "runner_failed")
                || string.IsNullOrWhiteSpace(request.Message)
                || (request.ArtifactSha256 is not null && (!Digest(request.ArtifactSha256) || string.IsNullOrWhiteSpace(request.ArtifactReference)))) throw Incomplete();
            RequireActive(c);
            c.FailureJson = json;
            Complete(m, c, "failed", request.Code, request.Message);
            if (request.Code == "merge_conflict")
                m.Record("task.land_failed", task.Id, new { code = request.Code, request.Message, branch = task.Branch,
                    target = c.DefaultBranch, files = request.Files ?? [], landedSince = request.LandedSince ?? [], phase = "integration_construction" });
            ReturnToOwner(m, task, request.Message + " " + Validations.Recovery);
            return Task.CompletedTask;
        }, ct);

    private static void Complete(Mutation m, IntegrationCandidate c, string state, string? code, string? message)
    {
        c.State = state;
        c.CompletedAt = m.Now;
        c.FailureCode = code;
        c.FailureMessage = message;
        Record(m, c, state);
    }

    private void ReturnToOwner(Mutation m, WorkTask task, string reason)
    {
        task.State = TaskState.InProgress;
        task.ClaimExpires = m.Now + leases.ClaimLease;
        task.UpdatedAt = m.Now;
        m.Record("task.integration_recovery", task.Id, new { reason });
    }

    private static void Record(Mutation m, IntegrationCandidate c, string action) => m.Record("integration." + action, c.TaskId,
        new { candidateId = c.Id, c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, c.CandidateSha, c.TreeSha,
            c.Attempt, c.AssignedAgentId, c.LeaseExpires, c.StartedAt, c.CompletedAt, c.EvidenceSha256, c.FailureCode, c.FailureMessage,
            durationMilliseconds = c.StartedAt is { } start ? (m.Now - start).TotalMilliseconds : (double?)null,
            c.RunnerProcessId, c.RunnerStartedAt, c.OwnedWorktreePath, c.ArtifactsDirectory });
}
