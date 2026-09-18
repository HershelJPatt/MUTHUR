using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Data;

namespace Muthur.Server.Services;

/// <summary>Read helpers for per-task validation verdicts.</summary>
public static class Validations
{
    public static async Task<Dictionary<int, IReadOnlyList<ValidationDto>>> ForTasksAsync(MuthurDb db, IReadOnlyList<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return [];
        var rows = await db.TaskValidations.Include(v => v.Agent).Include(v => v.ClaimedBy)
            .Where(v => taskIds.Contains(v.TaskId))
            .OrderBy(v => v.ValidatorKey)
            .ToListAsync(ct);
        return rows.GroupBy(v => v.TaskId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ValidationDto>)g.Select(v => new ValidationDto(
                v.ValidatorKey, v.Verdict.ToWire(), v.Agent?.Name, v.Evidence, v.At,
                v.WaitingSince, v.ClaimedBy?.Name, v.ClaimExpires)).ToList());
    }
}
