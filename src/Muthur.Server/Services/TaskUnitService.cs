using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed partial class TaskUnitService(Ledger ledger, IProcessRunner runner)
{
    [GeneratedRegex("\\A[a-z][a-z0-9-]{0,39}\\z")]
    private static partial Regex UnitIdPattern();

    private static string Key(int id) => $"task.units/{id}";
    private static string Json(TaskUnitGraph graph) => JsonSerializer.Serialize(graph, MuthurJsonContext.Default.TaskUnitGraph);
    internal static async Task<TaskUnitGraph?> ReadGraph(MuthurDb db, int id, CancellationToken ct)
    {
        var row = await db.Meta.SingleOrDefaultAsync(x => x.Key == Key(id), ct);
        return row is null ? null : JsonSerializer.Deserialize(row.Value, MuthurJsonContext.Default.TaskUnitGraph);
    }

    public Task<TaskUnitGraph?> GetAsync(string id, CancellationToken ct = default) => ledger.ReadAsync(async (db, _) =>
        await ReadGraph(db, (await TaskService.LoadAsync(db, id, ct)).Id, ct), ct);

    public Task<TaskResumePacket> ResumeAsync(string id, CancellationToken ct = default) => ledger.ReadAsync(async (db, _) =>
        await TaskUnitContext.ReadAsync(db, await TaskService.LoadAsync(db, id, ct), ct), ct);

    public Task<TaskUnitGraph> DefineAsync(Caller caller, string id, DefineTaskUnitsRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await Owned(m, id, ct);
            var previous = await ReadGraph(m.Db, task.Id, ct);
            Cas(previous?.Revision ?? 0, request.ExpectedRevision);
            ValidateDefinition(request.Units);
            var git = new TaskUnitGit(runner, task.Project!.RepoPath, ct);
            await git.BranchName(request.IntegrationBranch);
            if (request.IntegrationBranch == task.Project.DefaultBranch)
                throw Fail.Rule("invalid_unit_branch", "The integration branch must not be the default branch.");
            TaskUnitGit.PathName(task.SpecPath);
            var blob = TaskUnitGit.ObjectId(request.SpecBlob);
            if (await git.FileBlob(await git.Head(request.IntegrationBranch), task.SpecPath!) != blob)
                throw Fail.Rule("unit_spec_changed", "The integration spec does not match the supplied blob; redefine the graph.");
            var graph = new TaskUnitGraph(1, (previous?.Revision ?? 0) + 1, task.SpecPath!, blob,
                request.IntegrationBranch, request.Units.Select(x => new TaskUnit(x.Id, x.Dependencies, x.RequiredChecks)).ToList());
            if (previous?.SpecBlob == blob)
            {
                var definition = previous with { Revision = graph.Revision, Units = previous.Units.Select(x => x with { Attempt = null, InvalidationReason = null }).ToList() };
                if (Json(definition) != Json(graph))
                    throw Fail.Rule("unit_definition_changed", "Changing a graph definition requires a changed spec blob.");
                return previous;
            }
            await Save(m, task.Id, graph, "task.unit_defined", new
            {
                graph, previousGraph = previous, reason = previous is null ? "initial definition" : "spec blob changed; prior graph invalidated"
            }, ct);
            return graph;
        }, ct);

    public Task<TaskUnitGraph> CheckpointAsync(Caller caller, string id, TaskUnitCheckpointRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var task = await Owned(m, id, ct);
            var graph = await ReadGraph(m.Db, task.Id, ct) ?? throw Fail.Rule("unit_graph_missing", "Define the graph first.");
            Cas(graph.Revision, request.ExpectedRevision);
            var unit = graph.Units.SingleOrDefault(x => x.Id == request.UnitId) ?? throw Fail.Rule("unit_unknown", "Unknown unit ID.");
            if (request.AttemptId == Guid.Empty) throw Fail.Rule("unit_attempt_required", "Provide a new nonempty attempt UUID.");
            if (request.NextAction?.Length > 500) throw Fail.Rule("unit_next_action", "Next action is limited to 500 characters.");
            var git = new TaskUnitGit(runner, task.Project!.RepoPath, ct);
            if (request.Action != "start" && unit.Attempt?.AttemptId != request.AttemptId)
                throw Fail.Conflict("stale_attempt", "This is not the current attempt.");
            var updated = unit;
            var invalidateDependents = false;
            switch (request.Action)
            {
                case "start":
                {
                    var events = await m.Db.Events.Where(x => x.TaskId == task.Id && x.Type == "task.unit_started").Select(x => x.PayloadJson).ToListAsync(ct);
                    if (events.Any(x =>
                    {
                        using var doc = JsonDocument.Parse(x);
                        return doc.RootElement.GetProperty("attemptId").GetGuid() == request.AttemptId;
                    })) throw Fail.Conflict("stale_attempt", "An attempt UUID cannot be reused.");
                    if (unit.Attempt is not null) Reason(request.Reason);
                    var head = await Integration(git, task, graph);
                    var baseCommit = TaskUnitGit.ObjectId(request.BaseCommit);
                    if (baseCommit != head) throw Fail.Rule("unit_base_changed", "Dispatch base must equal the integration branch HEAD.");
                    await git.BranchName(request.OutputBranch);
                    if (request.OutputBranch == graph.IntegrationBranch || request.OutputBranch == task.Project.DefaultBranch)
                        throw Fail.Rule("invalid_unit_branch", "Output must be a separate non-default local branch.");
                    await Dependencies(git, task, graph, unit, head, false);
                    updated = unit with { InvalidationReason = null, Attempt = new TaskUnitAttempt(request.AttemptId,
                        baseCommit, request.OutputBranch!, null, "running", "pending", null, [], null, null,
                        request.NextAction ?? "obtain output", unit.Dependencies.ToDictionary(x => x, x => graph.Units.Single(u => u.Id == x).Attempt!.AttemptId),
                        m.Now, m.Now, caller.Name) };
                    invalidateDependents = unit.Attempt is not null;
                    break;
                }
                case "report":
                {
                    if (request.Checks is not null || request.ReviewEvidencePath is not null || request.ReviewEvidenceCommit is not null)
                        throw Fail.Rule("unit_report_proof", "Report does not accept review or check proofs.");
                    var a = unit.Attempt!;
                    var head = await Integration(git, task, graph, a);
                    await Dependencies(git, task, graph, unit, head, true);
                    var output = request.OutputCommit is null ? null : TaskUnitGit.ObjectId(request.OutputCommit);
                    if (output is null) Reason(request.Reason);
                    else
                    {
                        await Output(git, graph, a with { OutputCommit = output });
                        if (await git.Head(a.OutputBranch) != output) throw Fail.Rule("unit_output_changed", "Reported output must equal output branch HEAD.");
                    }
                    var state = output is null ? "failed" : "done";
                    if (a.OutputCommit == output && a.ReportedState == state && (request.NextAction is null || request.NextAction == a.NextAction)) return graph;
                    if (a.ReviewState == "accepted") throw Fail.Conflict("unit_already_accepted", "Invalidate the accepted attempt before changing its report.");
                    if (unit.InvalidationReason is not null && a.ReviewState == "rejected")
                        throw Fail.Rule("unit_invalidated", "Start a new attempt after invalidation.");
                    updated = unit with { Attempt = Clear(a) with { OutputCommit = output, ReportedState = state,
                        NextAction = request.NextAction ?? (output is null ? "redispatch" : "verify") }, InvalidationReason = output is null ? request.Reason : null };
                    break;
                }
                case "verify":
                case "integrate":
                {
                    var a = unit.Attempt!;
                    var head = await Integration(git, task, graph, a);
                    await Output(git, graph, a);
                    await Dependencies(git, task, graph, unit, head, true);
                    if (unit.InvalidationReason is not null) throw Fail.Rule("unit_invalidated", "Start a new attempt after invalidation.");
                    if (request.Action == "verify")
                    {
                        a = a with { Checks = request.Checks ?? [], ReviewEvidencePath = request.ReviewEvidencePath, ReviewEvidenceCommit = request.ReviewEvidenceCommit };
                        await Evidence(git, unit, a);
                        a = a with { ReviewState = "accepted" };
                    }
                    else
                    {
                        if (a.ReviewState != "accepted") throw Fail.Rule("unit_not_accepted", "Independent unit review is required before integration.");
                        await Evidence(git, unit, a);
                    }
                    var integrated = await git.Ancestor(a.OutputCommit!, head);
                    if (!integrated && request.Action == "integrate") throw Fail.Rule("unit_not_integrated", "Output is not an ancestor of integration HEAD.");
                    updated = unit with { Attempt = a with { IntegrationCommit = integrated ? head : null, NextAction = integrated ? "complete" : "integrate" } };
                    break;
                }
                case "reconcile":
                {
                    try
                    {
                        var a = unit.Attempt!;
                        var head = await Integration(git, task, graph, a);
                        await Dependencies(git, task, graph, unit, head, true);
                        var outputHead = await git.Head(a.OutputBranch);
                        await Output(git, graph, a with { OutputCommit = outputHead });
                        if (a.ReviewState == "accepted")
                        {
                            await Output(git, graph, a);
                            await Evidence(git, unit, a);
                            var integrated = await git.Ancestor(a.OutputCommit!, head);
                            updated = unit with { Attempt = a with { IntegrationCommit = integrated ? head : null, NextAction = integrated ? "complete" : "integrate" } };
                        }
                        else if (unit.InvalidationReason is not null)
                            updated = unit;
                        else if (outputHead == a.BaseCommit)
                        {
                            if (a.OutputCommit is not null && a.OutputCommit != a.BaseCommit)
                                throw Fail.Rule("unit_output_changed", "Output branch was rewound; recover artifacts or redispatch.");
                            updated = unit with { Attempt = Clear(a) with { ReportedState = "running", NextAction = "obtain output" } };
                        }
                        else
                            updated = unit with { Attempt = Clear(a) with { OutputCommit = outputHead, ReportedState = "recovered", NextAction = "verify" } };
                    }
                    catch (MuthurException ex) when (ex.Kind == ErrorKind.RuleViolation)
                    {
                        updated = Invalid(unit, ex.Message, ex.Code == "unit_spec_changed" ? "redefine graph" : "recover artifacts or redispatch");
                        invalidateDependents = true;
                    }
                    break;
                }
                case "invalidate":
                    Reason(request.Reason);
                    updated = Invalid(unit, request.Reason!, "redispatch");
                    invalidateDependents = true;
                    break;
                default: throw Fail.Rule("unit_action", "Use start, report, reconcile, verify, invalidate or integrate.");
            }
            if (updated == unit) return graph;
            updated = updated with { Attempt = updated.Attempt! with { UpdatedAt = m.Now, ReportingActor = caller.Name } };
            var affected = new HashSet<string> { unit.Id };
            if (invalidateDependents)
                while (graph.Units.Any(x => !affected.Contains(x.Id) && x.Dependencies.Any(affected.Contains) && affected.Add(x.Id))) { }
            var result = graph with { Revision = graph.Revision + 1, Units = graph.Units.Select(x => x.Id == unit.Id ? updated :
                affected.Contains(x.Id) ? Invalid(x, $"Dependency {unit.Id}: {request.Reason ?? updated.InvalidationReason ?? "attempt replaced"}", "redispatch") : x).ToList() };
            // Structural equality also covers reconstructed proof lists on exact verify replay.
            if (Json(result with { Revision = graph.Revision, Units = result.Units.Select(x => x.Id == unit.Id ? x with
                { Attempt = x.Attempt! with { UpdatedAt = unit.Attempt?.UpdatedAt ?? m.Now, ReportingActor = unit.Attempt?.ReportingActor ?? caller.Name } } : x).ToList() }) == Json(graph)) return graph;
            var eventName = request.Action switch { "start" => "started", "report" => "reported", "verify" => "verified", "integrate" => "integrated", "invalidate" => "invalidated", _ => "reconciled" };
            await Save(m, task.Id, result, "task.unit_" + eventName, new { request.AttemptId, request.UnitId, request.Reason, graph = result }, ct);
            return result;
        }, ct);

    private static async Task<WorkTask> Owned(Mutation m, string id, CancellationToken ct)
    {
        m.Caller.RequireIdentified();
        var task = await TaskService.LoadAsync(m.Db, id, ct);
        TaskService.RequireOwnerOrFounder(task, m.Caller);
        if (task.State != TaskState.InProgress || (!m.Caller.IsFounder && (task.ClaimExpires is null || task.ClaimExpires <= m.Now)))
            throw Fail.Rule("unit_owner_inactive", "Unit mutations require an InProgress task and a live owner claim, or the founder.");
        return task;
    }

    private static void Cas(long revision, long expected)
    {
        if (revision != expected) throw Fail.Conflict("checkpoint_conflict", $"Expected revision {expected}; current revision is {revision}.");
    }

    private static void Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw Fail.Rule("unit_reason_required", "A reason is required.");
    }

    private static TaskUnitAttempt Clear(TaskUnitAttempt a) => a with { ReviewState = "pending", IntegrationCommit = null, Checks = [], ReviewEvidencePath = null, ReviewEvidenceCommit = null };
    private static TaskUnit Invalid(TaskUnit u, string reason, string next) => u with { InvalidationReason = reason,
        Attempt = u.Attempt is null ? null : Clear(u.Attempt) with { ReviewState = "rejected", NextAction = next } };

    private static async Task Save(Mutation m, int id, TaskUnitGraph graph, string type, object payload, CancellationToken ct)
    {
        var row = await m.Db.Meta.SingleOrDefaultAsync(x => x.Key == Key(id), ct);
        if (row is null) m.Db.Meta.Add(new MetaEntry { Key = Key(id), Value = Json(graph) });
        else row.Value = Json(graph);
        m.Record(type, id, payload);
    }

    private static void ValidateDefinition(IReadOnlyList<TaskUnitDefinition>? units)
    {
        if (units is null || units.Count is < 1 or > 50 || units.Any(x => x is null || x.Id is null || !UnitIdPattern().IsMatch(x.Id)) ||
            units.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != units.Count)
            throw Fail.Rule("unit_definition", "Provide 1–50 units with unique valid case-sensitive IDs.");
        var map = units.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var u in units)
        {
            if (u.Dependencies is null || u.RequiredChecks is null || u.Dependencies.Count > 20 || u.RequiredChecks.Count > 20 ||
                u.Dependencies.Distinct(StringComparer.Ordinal).Count() != u.Dependencies.Count || u.Dependencies.Any(x => x is null || x == u.Id || !map.ContainsKey(x)) ||
                u.RequiredChecks.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100) || u.RequiredChecks.Distinct(StringComparer.Ordinal).Count() != u.RequiredChecks.Count)
                throw Fail.Rule("unit_definition", "Dependencies and required checks must be unique, bounded and valid.");
        }
        var done = new HashSet<string>();
        var visiting = new HashSet<string>();
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw Fail.Rule("unit_cycle", "The unit graph must be acyclic.");
            foreach (var dependency in map[id].Dependencies) Visit(dependency);
            visiting.Remove(id);
            done.Add(id);
        }
        foreach (var u in units) Visit(u.Id);
    }

    private static async Task<string> Integration(TaskUnitGit git, WorkTask task, TaskUnitGraph graph, TaskUnitAttempt? a = null)
    {
        if (task.SpecPath != graph.SpecPath || graph.IntegrationBranch == task.Project!.DefaultBranch)
            throw Fail.Rule("unit_spec_changed", "Task spec or integration branch changed; redefine graph.");
        var head = await git.Head(graph.IntegrationBranch);
        if (await git.FileBlob(head, graph.SpecPath) != graph.SpecBlob)
            throw Fail.Rule("unit_spec_changed", "Integration spec changed; redefine graph.");
        if (a is not null && (!await git.Ancestor(a.BaseCommit, head) || await git.FileBlob(a.BaseCommit, graph.SpecPath) != graph.SpecBlob))
            throw Fail.Rule("unit_base_changed", "Integration no longer descends from the dispatched base with its pinned spec.");
        return head;
    }

    private static async Task Output(TaskUnitGit git, TaskUnitGraph graph, TaskUnitAttempt a)
    {
        if (a.OutputCommit is null) throw Fail.Rule("unit_output_missing", "Report or reconcile an output first.");
        if (!await git.Ancestor(a.BaseCommit, a.OutputCommit) || !await git.Ancestor(a.OutputCommit, await git.Head(a.OutputBranch)))
            throw Fail.Rule("unit_output_changed", "Pinned output no longer descends from base or is unreachable from its branch.");
        if (await git.FileBlob(a.OutputCommit, graph.SpecPath) != graph.SpecBlob)
            throw Fail.Rule("unit_spec_changed", "Output spec changed; redefine graph.");
    }

    private static async Task Dependencies(TaskUnitGit git, WorkTask task, TaskUnitGraph graph, TaskUnit u, string head, bool snapshot, HashSet<string>? visited = null)
    {
        visited ??= [];
        foreach (var id in u.Dependencies)
        {
            var dep = graph.Units.Single(x => x.Id == id);
            var a = dep.Attempt;
            if (a is null || dep.InvalidationReason is not null || a.ReviewState != "accepted" || a.IntegrationCommit is null ||
                (snapshot && u.Attempt!.DependencyAttempts.GetValueOrDefault(id) != a.AttemptId))
                throw Fail.Rule("unit_dependency_changed", "Dependency attempts must be current, accepted and integrated.");
            if (!visited.Add(id)) continue;
            await Integration(git, task, graph, a);
            await Output(git, graph, a);
            await Evidence(git, dep, a);
            if (!await git.Ancestor(a.OutputCommit!, head)) throw Fail.Rule("unit_dependency_changed", "A dependency is no longer integrated.");
            await Dependencies(git, task, graph, dep, head, true, visited);
        }
    }

    private static async Task Evidence(TaskUnitGit git, TaskUnit u, TaskUnitAttempt a)
    {
        if (a.Checks.Count != u.RequiredChecks.Count || a.Checks.Any(x => x is null || !u.RequiredChecks.Contains(x.Name, StringComparer.Ordinal) || x.ExitCode != 0) ||
            a.Checks.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != a.Checks.Count)
            throw Fail.Rule("unit_checks_required", "Every required named check must appear exactly once with exit code zero.");
        async Task Proof(string? commit, string? path)
        {
            var full = TaskUnitGit.ObjectId(commit);
            TaskUnitGit.PathName(path);
            if (!await git.Ancestor(a.OutputCommit!, full)) throw Fail.Rule("unit_evidence_stale", "Evidence commit must descend from output.");
            await git.FileBlob(full, path!);
        }
        foreach (var check in a.Checks) await Proof(check.EvidenceCommit, check.EvidencePath);
        await Proof(a.ReviewEvidenceCommit, a.ReviewEvidencePath);
    }
}
