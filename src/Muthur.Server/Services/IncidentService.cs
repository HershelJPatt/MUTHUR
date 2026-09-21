using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed class IncidentService(Ledger ledger)
{
    private static string Key(int id) => $"I-{id}";

    private static string Text(string? value, string field, int max = 16000, bool optional = false, string code = "incident_input")
    {
        var text = value?.Trim() ?? "";
        if ((!optional && text.Length == 0) || text.Length > max)
            throw Fail.Rule(code, $"{field} must contain {(optional ? "0" : "1")}..{max} characters.");
        return text;
    }

    private static int Id(string? value, string prefix)
    {
        var text = value?.Trim() ?? "";
        if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw Fail.Rule("incident_input", $"Expected a positive {prefix} identifier.");
        return id;
    }

    private static async Task<Incident> LoadAsync(MuthurDb db, string id, CancellationToken ct)
    {
        var number = Id(id, "I-");
        return await db.Incidents.Include(i => i.Project).SingleOrDefaultAsync(i => i.Id == number, ct)
            ?? throw Fail.NotFound("Incident", id);
    }

    private static async Task<WorkTask> TaskAsync(Mutation m, Incident incident, string id, CancellationToken ct)
    {
        var number = Id(id, "T-");
        var task = await m.Db.Tasks.Include(t => t.Owner).SingleOrDefaultAsync(t => t.Id == number, ct)
            ?? throw Fail.NotFound("Task", id);
        if (task.ProjectId != incident.ProjectId) throw Fail.Rule("incident_scope", "Task and incident must belong to the same project.");
        if (task.OwnerAgentId is not null) TaskService.RequireOwnerOrFounder(task, m.Caller);
        if (task.State is TaskState.Done or TaskState.Cancelled) throw Fail.Rule("incident_task_state", "Incident links and suppressions cannot be changed on a terminal task.");
        return task;
    }

    private static IncidentDto Map(Incident i) => new(Key(i.Id), i.Project!.Key, i.Title, i.State, i.Signature,
        i.ExecutionPath, i.Configuration, i.ConditionVersion, i.Diagnosis, i.Workaround, i.AuthorizationReference,
        i.RecoveryCondition, i.CreatedAt, i.UpdatedAt);

    private static IncidentObservationDto Map(IncidentObservation o) => new(o.Id, Key(o.IncidentId), Wire.TaskId(o.TaskId),
        o.RunId, o.Evidence, o.Signature, o.ExecutionPath, o.Configuration, o.ConditionVersion, o.CreatedAt, o.Actor, o.Active, o.UnlinkReason);

    private static string EffectiveReason(IncidentSuppression s)
    {
        if (!s.Active) return s.ReleaseReason ?? "released";
        if (!s.EvidenceObservation!.Active) return "evidence unlinked";
        if (s.ConditionVersion != s.Incident!.ConditionVersion || s.EvidenceObservation.ConditionVersion != s.Incident.ConditionVersion)
            return "condition changed; fresh observation required";
        if (s.Incident.State is not ("confirmed" or "mitigated")) return "incident is not confirmed or mitigated";
        if (s.Task!.State is TaskState.Done or TaskState.Cancelled) return "task is terminal";
        return "effective";
    }

    private static IncidentSuppressionDto Map(IncidentSuppression s)
    {
        var reason = EffectiveReason(s);
        return new(Key(s.IncidentId), Wire.TaskId(s.TaskId), s.Assignment, s.ConditionVersion, s.EvidenceObservationId,
            s.Reason, s.CreatedAt, s.Active, s.ReleasedAt, s.ReleaseReason, s.Active && reason == "effective", reason, s.Incident!.RecoveryCondition);
    }

    private static IQueryable<IncidentSuppression> Suppressions(MuthurDb db) =>
        db.IncidentSuppressions.Include(s => s.Incident).Include(s => s.EvidenceObservation).Include(s => s.Task);

    /// <summary>One persisted gate shared by claims, both conductor planners, and operator explanations.</summary>
    internal static async Task<IReadOnlyList<IncidentSuppressionDto>> EffectiveAsync(MuthurDb db, CancellationToken ct)
    {
        var rows = await Suppressions(db).Where(s => s.Active).OrderBy(s => s.Id).ToListAsync(ct);
        return rows.Select(Map).Where(s => s.Effective).ToList();
    }

    internal static async Task<IReadOnlyList<TaskIncidentDto>> ForTaskAsync(MuthurDb db, int taskId, CancellationToken ct)
    {
        var observations = await db.IncidentObservations.Where(o => o.TaskId == taskId).OrderBy(o => o.Id).ToListAsync(ct);
        var ids = observations.Select(o => o.IncidentId).Distinct().ToList();
        var incidents = await db.Incidents.Include(i => i.Project).Where(i => ids.Contains(i.Id)).OrderBy(i => i.Id).ToListAsync(ct);
        var suppressions = await Suppressions(db).Where(s => s.TaskId == taskId).OrderBy(s => s.Id).ToListAsync(ct);
        return incidents.Select(i => new TaskIncidentDto(Map(i), observations.Where(o => o.IncidentId == i.Id).Select(Map).ToList(),
            suppressions.Where(s => s.IncidentId == i.Id).Select(Map).ToList())).ToList();
    }

    public Task<IncidentDto> AddAsync(Caller caller, AddIncidentRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        return ledger.MutateAsync(caller, async m =>
        {
            var project = await ProjectService.FindAsync(m.Db, request.Project is null ? null : Text(request.Project, "project", 1000), ct);
            var incident = new Incident
            {
                ProjectId = project.Id, Project = project, Title = Text(request.Title, "title", 300),
                Signature = Text(request.Signature, "signature", 1000), ExecutionPath = Text(request.Path, "path", 1000),
                Configuration = Text(request.Configuration, "configuration", 1000), RecoveryCondition = Text(request.Recovery, "recovery"),
                CreatedAt = m.Now, UpdatedAt = m.Now,
            };
            m.Db.Incidents.Add(incident);
            await m.Db.SaveChangesAsync(ct);
            m.Record("incident.created", payload: new { incidentId = Key(incident.Id), incident.Title, project = project.Key });
            return Map(incident);
        }, ct);
    }

    public Task<IReadOnlyList<IncidentDto>> ListAsync(string? project, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<IncidentDto>>(async (db, _) =>
        {
            var query = db.Incidents.Include(i => i.Project).AsQueryable();
            if (project is not null)
            {
                var found = await ProjectService.FindAsync(db, Text(project, "project", 1000), ct);
                query = query.Where(i => i.ProjectId == found.Id);
            }
            return (await query.OrderBy(i => i.Id).ToListAsync(ct)).Select(Map).ToList();
        }, ct);

    public Task<IncidentMatchesDto> MatchAsync(string? project, string? signature, string? path, string? configuration, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) =>
        {
            var found = await ProjectService.FindAsync(db, project is null ? null : Text(project, "project", 1000), ct);
            var tuple = (Text(signature, "signature", 1000), Text(path, "path", 1000), Text(configuration, "configuration", 1000));
            var rows = await db.Incidents.Include(i => i.Project).Where(i => i.ProjectId == found.Id &&
                (i.State == "suspected" || i.State == "confirmed" || i.State == "mitigated")).OrderBy(i => i.Id).ToListAsync(ct);
            return new IncidentMatchesDto(true, rows.Where(i => (i.Signature, i.ExecutionPath, i.Configuration) == tuple).Select(Map).ToList());
        }, ct);

    private static async Task<List<LedgerEvent>> EventsAsync(MuthurDb db, int id, CancellationToken ct)
    {
        var rows = await db.Events.Where(e => e.Type.StartsWith("incident.")).OrderBy(e => e.Seq).ToListAsync(ct);
        return rows.Where(e =>
        {
            using var json = JsonDocument.Parse(e.PayloadJson);
            return json.RootElement.TryGetProperty("incidentId", out var value) && value.GetString() == Key(id);
        }).ToList();
    }

    public Task<IncidentDetailDto> GetAsync(string id, CancellationToken ct = default) => ledger.ReadAsync(async (db, _) =>
    {
        var incident = await LoadAsync(db, id, ct);
        var observations = await db.IncidentObservations.Where(o => o.IncidentId == incident.Id).OrderBy(o => o.Id).ToListAsync(ct);
        var suppressions = await Suppressions(db).Where(s => s.IncidentId == incident.Id).OrderBy(s => s.Id).ToListAsync(ct);
        return new IncidentDetailDto(Map(incident), observations.Select(Map).ToList(), suppressions.Select(Map).ToList(),
            (await EventsAsync(db, incident.Id, ct)).Select(e => e.ToDto()).ToList());
    }, ct);

    private Task<IncidentDto> ChangeAsync(Caller caller, string id, Func<Mutation, Incident, Task> change, CancellationToken ct)
    {
        caller.RequireIdentified();
        return ledger.MutateAsync(caller, async m =>
        {
            var incident = await LoadAsync(m.Db, id, ct);
            await change(m, incident);
            return Map(incident);
        }, ct);
    }

    public Task<IncidentDto> UpdateAsync(Caller caller, string id, UpdateIncidentRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, (m, i) =>
        {
            var diagnosis = Text(request.Diagnosis, "diagnosis");
            var workaround = request.Workaround is null ? i.Workaround : Text(request.Workaround, "workaround", optional: true);
            var authorization = request.Authorization is null ? i.AuthorizationReference : Text(request.Authorization, "authorization", 2000, true);
            if (workaround.Length > 0 && authorization.Length == 0) throw Fail.Rule("incident_input", "A workaround requires an authorization reference.");
            i.Diagnosis = diagnosis; i.Workaround = workaround; i.AuthorizationReference = authorization; i.UpdatedAt = m.Now;
            m.Record("incident.updated", payload: new { incidentId = Key(i.Id), i.Diagnosis, i.Workaround, i.AuthorizationReference });
            return Task.CompletedTask;
        }, ct);

    public Task<IncidentDto> TransitionAsync(Caller caller, string id, TransitionIncidentRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, async (m, i) =>
        {
            var state = Text(request.State, "state");
            var evidence = Text(request.Evidence, "evidence", code: "incident_evidence");
            var allowed = i.State switch
            {
                "suspected" => state is "confirmed" or "disproven",
                "confirmed" => state is "mitigated" or "resolved" or "disproven",
                "mitigated" => state is "confirmed" or "resolved" or "disproven",
                "resolved" or "disproven" => state == "suspected",
                _ => false,
            };
            if (!allowed) throw Fail.Rule("incident_transition", $"Cannot transition from {i.State} to {state}.");
            var previousState = i.State;
            var previousVersion = i.ConditionVersion;
            if (previousState is "resolved" or "disproven") i.ConditionVersion++;
            i.State = state; i.UpdatedAt = m.Now;
            m.Record("incident.transitioned", payload: new { incidentId = Key(i.Id), previousState, state, evidence, previousVersion, newVersion = i.ConditionVersion });
            if (state is "resolved" or "disproven") await ReleaseAsync(m, i, null, "transition: " + evidence, "transition", ct);
        }, ct);

    public Task<IncidentObservationDto> ObserveAsync(Caller caller, string id, ObserveIncidentRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        return ledger.MutateAsync(caller, async m =>
        {
            var i = await LoadAsync(m.Db, id, ct);
            var task = await TaskAsync(m, i, request.Task, ct);
            var observation = new IncidentObservation
            {
                IncidentId = i.Id, TaskId = task.Id, Evidence = Text(request.Evidence, "evidence", code: "incident_evidence"),
                Signature = Text(request.Signature, "signature", 1000), ExecutionPath = Text(request.Path, "path", 1000),
                Configuration = Text(request.Configuration, "configuration", 1000), ConditionVersion = i.ConditionVersion,
                RunId = request.Run is null ? null : Text(request.Run, "run", 2000), CreatedAt = m.Now, Actor = caller.Name,
            };
            m.Db.IncidentObservations.Add(observation);
            await m.Db.SaveChangesAsync(ct);
            m.Record("incident.observed", task.Id, new { incidentId = Key(i.Id), taskId = Wire.TaskId(task.Id), observationId = observation.Id, observation.Evidence, observation.ConditionVersion });
            return Map(observation);
        }, ct);
    }

    public Task<IncidentDto> UnlinkAsync(Caller caller, string id, UnlinkIncidentRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, async (m, i) =>
        {
            var reason = Text(request.Reason, "reason");
            var observation = await ObservationAsync(m.Db, i, request.Observation, ct);
            await TaskAsync(m, i, Wire.TaskId(observation.TaskId), ct);
            if (!observation.Active) return;
            observation.Active = false; observation.UnlinkReason = reason;
            m.Record("incident.unlinked", observation.TaskId, new { incidentId = Key(i.Id), taskId = Wire.TaskId(observation.TaskId), observationId = observation.Id, reason });
            await ReleaseAsync(m, i, observation.Id, reason, "unlink", ct);
        }, ct);

    private static async Task<IncidentObservation> ObservationAsync(MuthurDb db, Incident i, int id, CancellationToken ct)
    {
        if (id <= 0) throw Fail.Rule("incident_input", "Observation must be a positive identifier.");
        var observation = await db.IncidentObservations.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw Fail.NotFound("Observation", id.ToString(CultureInfo.InvariantCulture));
        if (observation.IncidentId != i.Id) throw Fail.Rule("incident_evidence", "Observation belongs to another incident.");
        return observation;
    }

    private static void RequireConfirmed(Incident i)
    {
        if (i.State is not ("confirmed" or "mitigated")) throw Fail.Rule("incident_transition", "Incident must be confirmed or mitigated.");
    }

    public Task<IncidentDto> SuppressAsync(Caller caller, string id, SuppressIncidentRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, async (m, i) =>
        {
            RequireConfirmed(i);
            var task = await TaskAsync(m, i, request.Task, ct);
            var assignment = Text(request.Assignment, "assignment", 1000);
            var reason = Text(request.Reason, "reason");
            if (assignment != "#orchestrator")
            {
                var role = await m.Db.Roles.SingleOrDefaultAsync(r => r.Key == assignment, ct) ?? throw Fail.NotFound("Role", assignment);
                if (!role.IsValidator) throw Fail.Rule("incident_input", "Assignment must be a validator role or #orchestrator.");
            }
            var observation = await ObservationAsync(m.Db, i, request.Observation, ct);
            if (observation.TaskId != task.Id || !observation.Active || observation.ConditionVersion != i.ConditionVersion ||
                (observation.Signature, observation.ExecutionPath, observation.Configuration) != (i.Signature, i.ExecutionPath, i.Configuration))
                throw Fail.Rule("incident_evidence", "Suppression requires this task's active observation of the exact current condition.");
            if (await m.Db.IncidentSuppressions.AnyAsync(s => s.IncidentId == i.Id && s.TaskId == task.Id &&
                s.Assignment == assignment && s.ConditionVersion == i.ConditionVersion && s.Active, ct)) return;
            m.Db.IncidentSuppressions.Add(new IncidentSuppression
            {
                IncidentId = i.Id, TaskId = task.Id, Assignment = assignment, ConditionVersion = i.ConditionVersion,
                EvidenceObservationId = observation.Id, Reason = reason, CreatedAt = m.Now,
            });
            m.Record("incident.suppressed", task.Id, new { incidentId = Key(i.Id), taskId = Wire.TaskId(task.Id), assignment, i.ConditionVersion, observationId = observation.Id, reason });
        }, ct);

    private static async Task ReleaseAsync(Mutation m, Incident i, int? observationId, string reason, string kind, CancellationToken ct)
    {
        var rows = await m.Db.IncidentSuppressions.Where(s => s.IncidentId == i.Id && s.Active &&
            (observationId == null || s.EvidenceObservationId == observationId)).ToListAsync(ct);
        foreach (var s in rows)
        {
            s.Active = false; s.ReleasedAt = m.Now; s.ReleaseReason = reason;
            m.Record("incident.released", s.TaskId, new { incidentId = Key(i.Id), taskId = Wire.TaskId(s.TaskId), s.Assignment,
                s.ConditionVersion, s.EvidenceObservationId, reason, kind });
        }
    }

    public Task<IncidentDto> RecoverAsync(Caller caller, string id, RecoverIncidentRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, async (m, i) =>
        {
            RequireConfirmed(i);
            var kind = Text(request.Kind, "kind");
            var evidence = Text(request.Evidence, "evidence", code: "incident_evidence");
            var previousConfiguration = i.Configuration;
            if (kind == "configuration")
            {
                var configuration = Text(request.Configuration, "configuration", 1000);
                if (configuration == i.Configuration) throw Fail.Rule("incident_input", "Recovery requires a different measured configuration.");
                i.Configuration = configuration;
            }
            else if (kind == "probe")
            {
                if (request.Configuration is not null) throw Fail.Rule("incident_input", "Probe recovery does not accept configuration.");
                i.State = "mitigated";
            }
            else throw Fail.Rule("incident_input", "Recovery kind must be probe or configuration.");
            var previousVersion = i.ConditionVersion++;
            i.UpdatedAt = m.Now;
            m.Record("incident.recovered", payload: new { incidentId = Key(i.Id), kind, evidence, previousVersion,
                newVersion = i.ConditionVersion, previousConfiguration, i.Configuration });
            await ReleaseAsync(m, i, null, evidence, "recovery", ct);
        }, ct);

    public Task<IncidentMetricsDto> MetricsAsync(string id, int hours = 24, CancellationToken ct = default)
    {
        if (hours is < 1 or > 720) throw Fail.Rule("incident_input", "Hours must be between 1 and 720.");
        return ledger.ReadAsync(async (db, now) =>
        {
            var incident = await LoadAsync(db, id, ct);
            var start = now.AddHours(-hours);
            var observations = await db.IncidentObservations.Where(o => o.IncidentId == incident.Id).ToListAsync(ct);
            var cohort = observations.Select(o => o.TaskId).Distinct().Order().ToList();
            var history = await EventsAsync(db, incident.Id, ct);
            var events = history.Where(e => e.At >= start && e.At <= now).ToList();
            var taskEvents = await db.Events.Where(e => e.TaskId != null && cohort.Contains(e.TaskId.Value) && e.At >= start && e.At <= now &&
                (e.Type == "conductor.staffing" || (e.Type == "worker.finished" || e.Type == "worker.failed") || (e.Type == "task.landed" || e.Type == "task.pr_opened"))).OrderBy(e => e.Seq).ToListAsync(ct);
            var elapsed = new List<IncidentStaffingElapsedDto>();
            foreach (var recovery in events.Where(e => e.Type == "incident.recovered"))
                foreach (var task in cohort.Where(t => history.Any(e => e.Type == "incident.observed" && e.TaskId == t && e.Seq < recovery.Seq)))
                {
                    var staffing = taskEvents.FirstOrDefault(e => e.TaskId == task && e.Type == "conductor.staffing" && e.Seq > recovery.Seq);
                    elapsed.Add(new(Wire.TaskId(task), recovery.Seq, staffing?.Seq, staffing is null ? null : (staffing.At - recovery.At).TotalSeconds));
                }
            var windowObservations = observations.Where(o => o.CreatedAt >= start && o.CreatedAt <= now).ToList();
            var corrections = events.Count(e =>
            {
                if (e.Type != "incident.released") return false;
                using var payload = JsonDocument.Parse(e.PayloadJson);
                return payload.RootElement.GetProperty("kind").GetString() == "unlink";
            });
            var revision = typeof(IncidentService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return new IncidentMetricsDto(Key(incident.Id), start, now, cohort.Select(Wire.TaskId).ToList(), windowObservations.Count,
                windowObservations.Select(o => o.TaskId).Distinct().Count(), events.Count(e => e.Type == "incident.updated"),
                events.Count(e => e.Type == "incident.unlinked"), events.Count(e => e.Type == "incident.suppressed"),
                events.Count(e => e.Type == "incident.released"), corrections, taskEvents.Count(e => e.Type == "conductor.staffing"),
                null, taskEvents.Count(e => (e.Type == "worker.finished" || e.Type == "worker.failed")), taskEvents.Count(e => (e.Type == "task.landed" || e.Type == "task.pr_opened")), null, elapsed,
                events.Concat(taskEvents).Select(e => e.Seq).Distinct().Order().ToList(), revision ?? "unknown",
                ["Actual process starts and diagnosis sessions are not attributed to incidents.",
                 "Worker runs and successful task outcomes are cohort events, not evidence of incident causality or speedup.",
                 "Null elapsed time means no subsequent conductor staffing was observed within this window."]);
        }, ct);
    }
}
