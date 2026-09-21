using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>Approved outcomes remain separate from delivery and require explicit measured acceptance.</summary>
public sealed class ObjectiveService(Ledger ledger)
{
    private static string Text(string? value, int limit = 16000)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit) throw Fail.Rule("objective_input", "A required value is empty or too long.");
        return value.Trim();
    }

    private static void Founder(Caller caller)
    {
        if (!caller.IsFounder || caller.Name != "founder") throw Fail.Unauthorized("Only the founder may amend or accept approved outcomes and record founder effort.");
    }

    private static void Owner(Caller caller, ObjectiveDto objective)
    {
        caller.RequireIdentified();
        if (!caller.IsFounder && caller.Name != objective.Owner) throw Fail.Unauthorized("Only the objective owner or founder may add evidence.");
    }

    private static void Writable(ObjectiveDto objective, bool observing = false)
    {
        if (objective.State is "superseded" or "abandoned" || observing && objective.State != "observing")
            throw Fail.Conflict("objective_state", "The current objective state does not allow this action.");
    }

    private static void Measurement(ObjectiveMeasurement? value)
    {
        if (value is null || value.Quality is not ("measured" or "estimated" or "unknown") || value.SampleCount < 0
            || (value.Quality == "unknown") != (value.Value is null) || value.From > value.To
            || value.From.Offset != TimeSpan.Zero || value.To.Offset != TimeSpan.Zero
            || value.Cohort is null || value.Revisions is null || value.MissingData is null)
            throw Fail.Rule("objective_input", "Measurement must preserve quality, sample count, UTC window and missing data.");
        Text(value.Metric, 120); Text(value.Unit, 120); Text(value.Evidence);
        if (value.Cohort.Count > 200 || value.Revisions.Count > 200) throw Fail.Rule("objective_input", "Measurement references exceed the limit.");
        foreach (var id in value.Cohort)
            if (!Wire.TryParseTaskId(id, out _)) throw Fail.Rule("objective_input", "Cohort contains an invalid task ID.");
        foreach (var revision in value.Revisions) Text(revision, 200);
    }

    private static async Task<string> DefinitionAsync(Mutation m, string project, ObjectiveDefinition? definition, CancellationToken ct)
    {
        if (definition is null || definition.Approval is null) throw Fail.Rule("objective_input", "An approved definition is required.");
        Text(definition.Outcome); Text(definition.Scope); Text(definition.SuccessCondition); Text(definition.Reason);
        Text(definition.Metric, 120); Measurement(definition.Baseline);
        if (definition.MinimumSample < 1 || definition.Comparison is not ("at_most" or "at_least")
            || definition.WindowStart >= definition.WindowEnd || definition.Baseline.To > definition.WindowStart
            || definition.WindowStart.Offset != TimeSpan.Zero || definition.WindowEnd.Offset != TimeSpan.Zero
            || definition.Metric != definition.Baseline.Metric)
            throw Fail.Rule("objective_input", "Define an ordered UTC observation window and an enforceable sample and metric condition.");
        var task = await TaskService.LoadAsync(m.Db, definition.Approval.Task, ct);
        if (task.Project!.Key != project) throw Fail.Rule("objective_scope", "Approval must belong to the objective project.");
        var source = await m.Db.Events.SingleOrDefaultAsync(e => e.Seq == definition.Approval.Event, ct);
        if (source is null || source.Actor != "founder" || source.ActorAgentId is not null || source.TaskId != task.Id)
            throw Fail.Rule("objective_approval", "Cite an existing founder-origin approval event for this task.");
        if (definition.Approval.Request is { } requestId)
        {
            var request = await m.Db.FounderRequests.SingleOrDefaultAsync(r => r.Id == requestId, ct);
            using var payload = JsonDocument.Parse(source.PayloadJson);
            if (source.Type != "request.answered" || request?.TaskId != task.Id || request.Answer is null
                || !payload.RootElement.TryGetProperty("request", out var id) || id.GetInt32() != requestId)
                throw Fail.Rule("objective_approval", "The cited founder answer does not match this request.");
            return $"{source.Actor} at {source.At:O}: {request.Question}\nAnswer: {request.Answer}";
        }
        if (source.Type != "task.added") throw Fail.Rule("objective_approval", "Cite founder task creation or an answered request.");
        return $"{source.Actor} at {source.At:O}: {task.Title}\n{task.Body}";
    }

    private static async Task<ObjectiveDto> LoadAsync(MuthurDb db, string id, CancellationToken ct)
    {
        if (!id.StartsWith("O-", StringComparison.Ordinal) || !int.TryParse(id.AsSpan(2), out var number) || number < 1)
            throw Fail.NotFound("Objective", id);
        var row = await db.Meta.SingleOrDefaultAsync(x => x.Key == "objective." + id, ct) ?? throw Fail.NotFound("Objective", id);
        return JsonSerializer.Deserialize(row.Value, MuthurJsonContext.Default.ObjectiveDto)!;
    }

    private static async Task SaveAsync(Mutation m, ObjectiveDto objective, string action, CancellationToken ct)
    {
        var key = "objective." + objective.Id;
        var row = await m.Db.Meta.SingleOrDefaultAsync(x => x.Key == key, ct);
        var value = JsonSerializer.Serialize(objective, MuthurJsonContext.Default.ObjectiveDto);
        if (value.Length > 2_000_000) throw Fail.Rule("objective_input", "Objective history exceeds the supported record size.");
        if (row is null) m.Db.Meta.Add(new MetaEntry { Key = key, Value = value }); else row.Value = value;
        m.Record("objective." + action, payload: new { objective.Id, objective.Revision, objective.Owner, objective.State });
    }

    public Task<ObjectiveDto> AddAsync(Caller caller, AddObjectiveRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            caller.RequireIdentified();
            var title = Text(request.Title, 240);
            var approval = await DefinitionAsync(m, request.Project, request.Definition, ct);
            if (request.Owner is not null && !caller.IsFounder && request.Owner != caller.Name) throw Fail.Unauthorized("An agent cannot assign another objective owner.");
            var next = await m.Db.Meta.SingleOrDefaultAsync(x => x.Key == "objective.next-id", ct);
            var number = next is null ? 1 : int.Parse(next.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (next is null) m.Db.Meta.Add(new MetaEntry { Key = "objective.next-id", Value = "2" });
            else next.Value = (number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var objective = new ObjectiveDto($"O-{number}", request.Project, Text(request.Owner ?? caller.Name, 120), title, 1, "observing",
                m.Now, m.Now, [new(1, null, caller.Name, m.Now, request.Definition, approval)], [], [], [], []);
            await SaveAsync(m, objective, "created", ct);
            return objective;
        }, ct);

    private Task<ObjectiveDto> ChangeAsync(Caller caller, string id, int revision, string action,
        Func<Mutation, ObjectiveDto, Task<ObjectiveDto>> change, CancellationToken ct) => ledger.MutateAsync(caller, async m =>
        {
            var objective = await LoadAsync(m.Db, id, ct);
            if (objective.Revision != revision) throw Fail.Conflict("objective_revision", "The objective revision changed; reread it before writing.");
            var updated = await change(m, objective);
            if (ReferenceEquals(updated, objective)) return objective;
            updated = updated with { UpdatedAt = m.Now };
            await SaveAsync(m, updated, action, ct);
            return updated;
        }, ct);

    public Task<ObjectiveDto> AmendAsync(Caller caller, string id, AmendObjectiveRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, request.ExpectedRevision, "amended", async (m, o) =>
        {
            Founder(caller);
            var approval = await DefinitionAsync(m, o.Project, request.Definition, ct);
            return o with { Revision = o.Revision + 1, State = "observing", Owner = Text(request.Owner ?? o.Owner, 120), SupersededBy = null,
                ClosureReason = null, Revisions = [.. o.Revisions, new(o.Revision + 1, o.Revision, caller.Name, m.Now, request.Definition, approval)] };
        }, ct);

    public Task<ObjectiveDto> LinkAsync(Caller caller, string id, LinkObjectiveRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, request.ExpectedRevision, "linked", async (m, o) =>
        {
            Owner(caller, o); Writable(o, true);
            if (request.Role is not ("enabling" or "capability" or "follow_up")) throw Fail.Rule("objective_input", "Unknown task relationship.");
            var task = await TaskService.LoadAsync(m.Db, request.Task, ct);
            if (task.Project!.Key != o.Project) throw Fail.Rule("objective_scope", "Task must belong to the same project.");
            var key = Wire.TaskId(task.Id);
            var links = o.Links.Where(l => l.Task != key).Append(new ObjectiveLink(key, request.Role, Text(request.Note))).ToList();
            if (links.Count > 200) throw Fail.Rule("objective_input", "At most 200 tasks may be linked.");
            return o with { Links = links };
        }, ct);

    private static bool Matches(ObjectiveDefinition d, ObjectiveMeasurement v) => v.Metric == d.Metric && v.Unit == d.Baseline.Unit
        && v.From == d.WindowStart && v.To == d.WindowEnd && v.Revisions.Count > 0
        && v.Cohort.ToHashSet(StringComparer.Ordinal).SetEquals(d.Baseline.Cohort);

    public Task<ObjectiveDto> ObserveAsync(Caller caller, string id, ObserveObjectiveRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, request.ExpectedRevision, "evidence_added", async (m, o) =>
        {
            Owner(caller, o); Writable(o, request.Kind is "observation" or "capability");
            Text(request.Key, 120); Text(request.Description); Measurement(request.Measurement);
            if (request.Kind is not ("observation" or "capability" or "regression" or "note")) throw Fail.Rule("objective_input", "Unknown evidence kind.");
            if (request.Kind == "observation" && !Matches(o.Revisions[^1].Definition, request.Measurement)) throw Fail.Rule("objective_scope", "Observation must match the approved metric, unit, cohort and window.");
            if (request.Task is not null && !o.Links.Any(l => l.Task == request.Task)) throw Fail.Rule("objective_scope", "Evidence task must be linked.");
            if (request.Event is { } seq)
            {
                var e = await m.Db.Events.SingleOrDefaultAsync(e => e.Seq == seq, ct);
                if (e?.TaskId is not { } taskId || !o.Links.Any(l => l.Task == Wire.TaskId(taskId))
                    || request.Task is not null && request.Task != Wire.TaskId(taskId)) throw Fail.Rule("objective_scope", "Evidence event must belong to a linked task.");
            }
            var evidence = new ObjectiveEvidence(request.Key, o.Revision, request.Kind, request.Measurement, request.Description, request.Task, request.Event, caller.Name, m.Now);
            if (o.Evidence.FirstOrDefault(e => e.Key == request.Key) is { } previous)
            {
                var replay = evidence with { At = previous.At };
                if (JsonSerializer.Serialize(previous, MuthurJsonContext.Default.ObjectiveEvidence) != JsonSerializer.Serialize(replay, MuthurJsonContext.Default.ObjectiveEvidence))
                    throw Fail.Conflict("objective_evidence_key", "This evidence key already names different content.");
                return o;
            }
            return o with { Evidence = [.. o.Evidence, evidence] };
        }, ct);

    public Task<ObjectiveDto> EffortAsync(Caller caller, string id, EffortObjectiveRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, request.ExpectedRevision, "effort_added", (m, o) =>
        {
            Founder(caller); Writable(o); Text(request.Key, 120); Text(request.Source); Text(request.Note);
            if (request.Minutes <= 0 || request.Quality is not ("measured" or "estimated") || request.From > request.To
                || request.From.Offset != TimeSpan.Zero || request.To.Offset != TimeSpan.Zero) throw Fail.Rule("objective_input", "Effort requires positive explicit minutes and a UTC period.");
            var effort = new ObjectiveEffort(request.Key, request.Minutes, request.Quality, request.From, request.To, request.Source, request.Note, caller.Name, m.Now);
            if (o.Effort.FirstOrDefault(e => e.Key == request.Key) is { } previous)
            {
                if (previous != effort with { At = previous.At }) throw Fail.Conflict("objective_effort_key", "This effort key already names a different allocation.");
                return Task.FromResult(o);
            }
            return Task.FromResult(o with { Effort = [.. o.Effort, effort] });
        }, ct);

    public Task<ObjectiveDto> AcceptAsync(Caller caller, string id, AcceptObjectiveRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, request.ExpectedRevision, "accepted", (m, o) =>
        {
            Founder(caller); Writable(o, true); Text(request.Reason);
            var d = o.Revisions[^1].Definition;
            var e = o.Evidence.FirstOrDefault(e => e.Key == request.ObservationKey && e.Revision == o.Revision && e.Kind == "observation");
            if (e is null || d.WindowEnd > m.Now || !Matches(d, e.Measurement) || e.Measurement.Quality != "measured"
                || e.Measurement.Value is not { } value || e.Measurement.SampleCount < d.MinimumSample || e.Measurement.MissingData.Length > 0
                || !(d.Comparison == "at_most" ? value <= d.Target : value >= d.Target))
                throw Fail.Rule("objective_success_not_demonstrated", "Current measured evidence must satisfy the full approved window and success condition.");
            return Task.FromResult(o with { State = "achieved", Acceptances = [.. o.Acceptances, new(o.Revision, e.Key, caller.Name, m.Now, request.Reason)] });
        }, ct);

    public Task<ObjectiveDto> CloseAsync(Caller caller, string id, CloseObjectiveRequest request, CancellationToken ct = default) =>
        ChangeAsync(caller, id, request.ExpectedRevision, "closed", async (m, o) =>
        {
            Founder(caller); Writable(o); Text(request.Reason);
            if (request.State is not ("superseded" or "abandoned")) throw Fail.Rule("objective_input", "Use superseded or abandoned.");
            if (request.State == "superseded")
            {
                var other = await LoadAsync(m.Db, request.SupersededBy ?? "", ct);
                if (other.Id == o.Id || other.Project != o.Project) throw Fail.Rule("objective_scope", "Replacement must be another objective in the same project.");
            }
            return o with { State = request.State, SupersededBy = request.SupersededBy, ClosureReason = request.Reason };
        }, ct);

    public Task<IReadOnlyList<ObjectiveDto>> ListAsync(string? project = null, CancellationToken ct = default) => ledger.ReadAsync<IReadOnlyList<ObjectiveDto>>(async (db, _) =>
        (await db.Meta.Where(m => m.Key.StartsWith("objective.O-")).ToListAsync(ct))
            .Select(m => JsonSerializer.Deserialize(m.Value, MuthurJsonContext.Default.ObjectiveDto)!)
            .Where(o => project is null || o.Project == project).OrderBy(o => int.Parse(o.Id.AsSpan(2))).ToList(), ct);

    public Task<ObjectiveView> GetAsync(string id, CancellationToken ct = default) => ledger.ReadAsync(async (db, now) =>
    {
        var o = await LoadAsync(db, id, ct);
        var tasks = new List<ObjectiveTaskView>(); var blockers = new List<ObjectiveBlocker>(); var decisions = new List<ObjectiveDecision>();
        foreach (var link in o.Links)
        {
            var task = await TaskService.LoadAsync(db, link.Task, ct);
            tasks.Add(new(link.Task, task.Title, task.State.ToString(), link.Role, link.Role == "capability" && task.State == TaskState.Done));
            foreach (var dependency in task.DependsOn)
            {
                var dep = await TaskService.LoadAsync(db, dependency, ct);
                if (dep.State != TaskState.Done) blockers.Add(new(link.Task, dependency, task.DependencyReason ?? "Dependency has not completed."));
            }
            var requests = await db.FounderRequests.Where(r => r.TaskId == task.Id && r.Answer == null).ToListAsync(ct);
            decisions.AddRange(requests.Select(r => new ObjectiveDecision(r.Id, link.Task, r.Question, r.Answer)));
        }
        var d = o.Revisions[^1].Definition;
        var measured = o.Effort.Where(e => e.Quality == "measured").Sum(e => e.Minutes);
        var estimated = o.Effort.Where(e => e.Quality == "estimated").Sum(e => e.Minutes);
        var acceptance = o.State == "achieved" ? o.Acceptances.LastOrDefault(a => a.Revision == o.Revision) : null;
        return new ObjectiveView(o, tasks, blockers, decisions, acceptance is not null ? "accepted" : now < d.WindowStart ? "not_started" : now < d.WindowEnd ? "pending" : "window_ended",
            measured, estimated, acceptance is not null && measured > 0 ? 60 / measured : null, acceptance is not null && estimated > 0 ? 60 / estimated : null,
            acceptance is not null && o.Evidence.Any(e => e.Kind == "regression" && e.At >= acceptance.At),
            o.Effort.Count == 0 ? ["Founder effort is unknown."] : [], await ResourcesAsync(db, o, now, ct));
    }, ct);

    private static async Task<ObjectiveResources> ResourcesAsync(MuthurDb db, ObjectiveDto o, DateTimeOffset now, CancellationToken ct)
    {
        var definition = o.Revisions[^1].Definition;
        var from = definition.WindowStart;
        var to = now < from ? from : now < definition.WindowEnd ? now : definition.WindowEnd;
        var ids = o.Links.Select(l => { Wire.TryParseTaskId(l.Task, out var id); return id; }).Distinct().ToList();
        var history = await db.Events.Where(e => e.TaskId != null && ids.Contains(e.TaskId.Value)).OrderBy(e => e.Seq).ToListAsync(ct);
        var window = history.Where(e => e.At >= from && e.At <= to && now >= from).ToList();
        var reports = window.Where(e => e.Type is "worker.finished" or "worker.failed").ToList();
        var runs = reports.Select(e => new ObjectiveRun(e.Seq, ReceiptsService.Runs([e]).Single())).ToList();
        var missing = new List<string> { "Host and historical installed revisions are unknown unless recorded in individual evidence.", "Unreported costs and tokens remain unknown; no incomplete total is calculated.", "Regression coverage is limited to explicit objective regression evidence." };
        if (runs.Any(r => string.IsNullOrEmpty(r.Run.RunId))) missing.Add("Legacy reports without run IDs remain separate; identity cannot be deduplicated.");
        runs = runs.Where(r => string.IsNullOrEmpty(r.Run.RunId)).Concat(runs.Where(r => !string.IsNullOrEmpty(r.Run.RunId))
            .GroupBy(r => r.Run.RunId).Select(g => g.MaxBy(r => r.Sequence)!)).OrderBy(r => r.Sequence).ToList();
        var deliveries = new List<ObjectiveDelivery>();
        var waiting = 0d; var duplicateParkings = 0;
        foreach (var group in history.GroupBy(e => e.TaskId!.Value))
        {
            var events = group.ToList();
            foreach (var interval in TaskStateTimeline.ReplayWithPayload(events.Select(e => (e.Type, e.At, (string?)e.PayloadJson))))
            {
                if (interval.State is not (TaskState.Backlog or TaskState.Blocked or TaskState.Validating)) continue;
                var start = interval.From > from ? interval.From : from;
                var end = interval.Until is { } until && until < to ? until : to;
                if (end > start) waiting += (end - start).TotalSeconds;
            }
            HashSet<string> previous = [];
            foreach (var e in events)
            {
                if (e.Type == "task.dependencies_ready") previous.Clear();
                if (e.Type != "task.dependencies_set") continue;
                using var json = JsonDocument.Parse(e.PayloadJson);
                var dependencies = json.RootElement.TryGetProperty("tasks", out var array) && array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToHashSet() : [];
                if (e.At >= from && e.At <= to && previous.Overlaps(dependencies)) duplicateParkings++;
                previous = dependencies;
            }
            var landed = events.FirstOrDefault(e => e.Type is "task.landed" or "task.pr_opened");
            var created = events.FirstOrDefault(e => e.Type == "task.added");
            if (landed is not null && landed.At >= from && landed.At <= to)
            {
                if (created is null) { missing.Add($"{Wire.TaskId(group.Key)} has no creation evidence."); continue; }
                using var payload = JsonDocument.Parse(landed.PayloadJson);
                var revision = payload.RootElement.TryGetProperty("implementationSha", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null;
                deliveries.Add(new(Wire.TaskId(group.Key), (landed.At - created.At).TotalSeconds, revision));
            }
        }
        var ordered = deliveries.Select(d => d.Seconds).Order().ToArray();
        if (ordered.Length < 10) missing.Add("Delivery sample is small; no causal improvement is established.");
        var starts = window.Count(e => e.Type is "worker.started" or "conductor.started" or "integration.started");
        if (starts == 0) missing.Add("No explicit actual-start records; staffing attempts are not process starts.");
        return new(from, to, o.Links.Select(l => l.Task).Distinct().ToList(), runs, window.Count(e => e.Type == "conductor.staffing"),
            starts == 0 ? null : starts, reports.Count, runs.Sum(r => (double)r.Run.Seconds), waiting, duplicateParkings,
            window.Count(e => e.Type == "request.answered" && e.Actor == "founder" && e.ActorAgentId is null), deliveries,
            ordered.Length == 0 ? null : (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2,
            ordered.Length == 0 ? null : ordered[(int)Math.Ceiling(.9 * ordered.Length) - 1], missing);
    }
}
