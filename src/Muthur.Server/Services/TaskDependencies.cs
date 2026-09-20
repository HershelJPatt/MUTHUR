using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

internal static class TaskDependencies
{
    internal static async Task<HashSet<string>> CompletedAsync(MuthurDb db, CancellationToken ct) =>
        (await db.Tasks.Where(t => t.State == TaskState.Done).Select(t => t.Id).ToListAsync(ct))
        .Select(Wire.TaskId).ToHashSet(StringComparer.Ordinal);

    internal static Task ResolveAsync(Ledger ledger, CancellationToken ct) => ledger.MutateAsync(Caller.Founder, async m =>
    {
        var completed = await CompletedAsync(m.Db, ct);
        var tasks = await m.Db.Tasks.Where(t => t.State == TaskState.Backlog || t.State == TaskState.Blocked).ToListAsync(ct);
        foreach (var task in tasks.Where(t => t.DependsOn.Count > 0 && t.DependsOn.All(completed.Contains)))
        {
            var dependencies = task.DependsOn;
            task.DependsOn = [];
            task.DependencyReason = null;
            task.UpdatedAt = m.Now;
            m.Record("task.dependencies_ready", task.Id, new { tasks = dependencies });
        }
    }, ct);
}
