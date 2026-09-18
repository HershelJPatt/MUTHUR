using Microsoft.EntityFrameworkCore;
using Muthur.Core;
using Muthur.Data;

namespace Muthur.Server.Services;

/// <summary>Role-hold lease helpers shared by the agent and role services.</summary>
public static class RoleLeases
{
    /// <summary>Any sign of life from an agent renews every role it holds.</summary>
    public static async Task RenewAsync(Mutation m, Guid agentId, LeasePolicy leases, CancellationToken ct)
    {
        var renewTo = m.Now + leases.RoleLease;
        var holds = await m.Db.RoleHolds.Where(h => h.AgentId == agentId && h.LeaseExpires > m.Now).ToListAsync(ct);
        foreach (var hold in holds) hold.LeaseExpires = renewTo;
    }

    /// <summary>Agent id → keys of the roles it holds right now (lapsed holds do not count).</summary>
    public static async Task<Dictionary<Guid, List<string>>> HeldByAgentAsync(MuthurDb db, DateTimeOffset now, CancellationToken ct)
    {
        var holds = await db.RoleHolds.Where(h => h.LeaseExpires > now).OrderBy(h => h.RoleKey).ToListAsync(ct);
        return holds.GroupBy(h => h.AgentId).ToDictionary(g => g.Key, g => g.Select(h => h.RoleKey).ToList());
    }

    public static Task<bool> HoldsAsync(MuthurDb db, Guid agentId, string roleKey, DateTimeOffset now, CancellationToken ct) =>
        db.RoleHolds.AnyAsync(h => h.AgentId == agentId && h.RoleKey == roleKey && h.LeaseExpires > now, ct);

    /// <summary>How many agents hold a role right now, for callers that only need the number against its capacity.</summary>
    public static Task<int> LiveHolderCountAsync(MuthurDb db, string roleKey, DateTimeOffset now, CancellationToken ct) =>
        db.RoleHolds.CountAsync(h => h.RoleKey == roleKey && h.LeaseExpires > now, ct);
}
