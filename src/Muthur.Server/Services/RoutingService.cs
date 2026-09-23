using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed class RoutingService(Ledger ledger)
{
    public Task<RoutingHistory> HistoryAsync(int hours, CancellationToken ct = default) => ledger.ReadAsync(async (db, now) =>
    {
        if (hours is < 1 or > 720) throw Fail.Rule("routing_window", "Hours must be between 1 and 720.");
        var from = now.AddHours(-hours);
        var events = await db.Events.Where(e => e.At >= from && e.At <= now && (e.Type == "worker.finished" || e.Type == "worker.failed" || e.Type == "validation.failed"))
            .OrderBy(e => e.Seq).ToListAsync(ct);
        var failures = events.Where(e => e.Type == "validation.failed" && e.TaskId is not null)
            .Select(e => new ValidationFailureDto(Wire.TaskId(e.TaskId!.Value), e.At)).ToList();
        var done = await TaskDependencies.CompletedAsync(db, ct);
        var tasks = await db.Tasks.Include(t => t.Project).Where(t => t.State == TaskState.Backlog).ToListAsync(ct);
        var ready = new List<TaskDto>();
        foreach (var task in tasks.Where(t => t.DependsOn.All(done.Contains)).OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id))
            ready.Add(await Validations.MapAsync(db, task, null, ct));
        return new RoutingHistory(from, now, await db.Events.Select(e => (long?)e.Seq).MaxAsync(ct) ?? 0, ReceiptsService.Runs(events), ready,
            await db.Tasks.CountAsync(t => t.State == TaskState.Validating, ct), failures);
    }, ct);

    public Task<RoutingSnapshot> RecordAsync(Caller caller, string taskId, RoutingReport report, CancellationToken ct = default) => ledger.MutateAsync(caller, async m =>
    {
        caller.RequireIdentified();
        var task = await TaskService.LoadAsync(m.Db, taskId, ct);
        if (!caller.IsAgent || task.OwnerAgentId != caller.AgentId || task.State != TaskState.InProgress)
            throw Fail.Unauthorized("Only the identified owner of an in-progress task may record its recommendation.");
        var json = JsonSerializer.Serialize(report, MuthurJsonContext.Default.RoutingReport);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 262144 || report.SchemaVersion != 1 || report.PolicyVersion != "measured-quality-v1"
            || report.Task != taskId || report.From >= report.To || report.To - report.From > TimeSpan.FromHours(720)
            || report.GeneratedAt < report.To || report.GeneratedAt > m.Now.AddMinutes(5) || report.Candidates is null || report.Requirements is null
            || report.BaseCommit.Length != 40 || report.SpecBlob.Length != 40 || string.IsNullOrWhiteSpace(report.Rationale)
            || report.Candidates.Select(c => c.CatalogPosition).Distinct().Count() != report.Candidates.Count
            || report.RecommendedCatalogPosition is { } position && !report.Candidates.Any(c => c.CatalogPosition == position && c.Eligible && c.Available))
            throw Fail.Rule("routing_report", "Malformed, unsupported or oversized routing report.");
        m.Record("routing.recommended", task.Id, report);
        await m.Db.SaveChangesAsync(ct);
        var e = m.Recorded[^1];
        return new RoutingSnapshot(e.Seq, e.At, e.Actor, report);
    }, ct);

    public Task<IReadOnlyList<RoutingSnapshot>> ShowAsync(string taskId, CancellationToken ct = default) => ledger.ReadAsync<IReadOnlyList<RoutingSnapshot>>(async (db, _) =>
    {
        var task = await TaskService.LoadAsync(db, taskId, ct);
        return (await db.Events.Where(e => e.TaskId == task.Id && e.Type == "routing.recommended").OrderBy(e => e.Seq).ToListAsync(ct))
            .Select(e => new RoutingSnapshot(e.Seq, e.At, e.Actor, JsonSerializer.Deserialize(e.PayloadJson, MuthurJsonContext.Default.RoutingReport)!)).ToList();
    }, ct);
}
