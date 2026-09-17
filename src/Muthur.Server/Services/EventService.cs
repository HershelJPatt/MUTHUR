using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;

namespace Muthur.Server.Services;

public sealed class EventService(Ledger ledger)
{
    /// <summary>With <paramref name="since"/>: events after that seq, oldest first. Without: the latest events, oldest first.</summary>
    public Task<IReadOnlyList<EventDto>> ListAsync(long? since, string? taskId, int limit, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<EventDto>>(async (db, _) =>
        {
            limit = Math.Clamp(limit, 1, 1000);
            var events = db.Events.AsQueryable();
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                if (!Wire.TryParseTaskId(taskId, out var number))
                    throw Fail.Rule("invalid_task_id", $"'{taskId}' is not a task id (expected T-<number>).");
                events = events.Where(e => e.TaskId == number);
            }

            var rows = since is { } seq
                ? await events.Where(e => e.Seq > seq).OrderBy(e => e.Seq).Take(limit).ToListAsync(ct)
                : (await events.OrderByDescending(e => e.Seq).Take(limit).ToListAsync(ct)).OrderBy(e => e.Seq).ToList();
            return rows.Select(e => e.ToDto()).ToList();
        }, ct);
}
