using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Server.Components.Shared;

namespace Muthur.Server.Services;

/// <summary>Whether every standing responsibility is held by something that is still answering.</summary>
public sealed class DoctorRoleCheck(Ledger ledger, MuthurOptions options) : IDoctorCheck
{
    public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<CheckDto>>(async (db, _) =>
        {
            var roles = await db.Roles.OrderBy(r => r.Key).ToListAsync(ct);
            if (roles.Count == 0) return [];

            var holds = await db.RoleHolds.Include(h => h.Agent)
                .Where(h => h.LeaseExpires > context.Now)
                .ToDictionaryAsync(h => h.RoleKey, StringComparer.Ordinal, ct);
            var projects = await db.Projects.OrderBy(p => p.Key).ToListAsync(ct);

            var validating = (await db.Tasks.Where(t => t.State == TaskState.Validating).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
            var pending = await db.TaskValidations.Where(v => v.Verdict == Verdict.Pending)
                .Select(v => new { v.ValidatorKey, v.TaskId }).ToListAsync(ct);
            var waiting = pending.Where(v => validating.Contains(v.TaskId))
                .GroupBy(v => v.ValidatorKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(v => v.TaskId).Distinct().Count(), StringComparer.Ordinal);

            return roles.Select(role => Inspect(
                role,
                holds.GetValueOrDefault(role.Key),
                waiting.GetValueOrDefault(role.Key),
                projects.Where(p => p.RequiredValidators.Contains(role.Key)).Select(p => p.Key).ToList(),
                context.Now)).ToList();
        }, ct);

    private CheckDto Inspect(Role role, RoleHold? hold, int waiting, IReadOnlyList<string> requiredBy, DateTimeOffset now)
    {
        if (hold?.Agent is { } agent)
            return now - agent.LastHeartbeat > TimeSpan.FromSeconds(options.AgentStaleSeconds)
                ? Check(CheckStatus.Warn, $"Held by '{agent.Name}', last seen {Format.Age(agent.LastHeartbeat, now)} ago; the hold lapses at {hold.LeaseExpires:u} unless it speaks.")
                : Check(CheckStatus.Ok, $"Held by '{agent.Name}'.");

        if (waiting > 0)
            return Check(CheckStatus.Warn, $"Unheld, and {waiting} task(s) are waiting on it.");

        return requiredBy.Count > 0
            ? Check(CheckStatus.Ok, $"Open. Required by {string.Join(", ", requiredBy)}.")
            : Check(CheckStatus.Ok, "Open.");

        CheckDto Check(CheckStatus status, string detail) => new("role", role.Key, status, detail);
    }
}
