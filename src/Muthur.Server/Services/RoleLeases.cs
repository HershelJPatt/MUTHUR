using Muthur.Core;
using Muthur.Data;

namespace Muthur.Server.Services;

/// <summary>Role-hold lease helpers shared by the agent and role services.</summary>
public static class RoleLeases
{
    public static Task RenewAsync(Mutation m, Guid agentId, LeasePolicy leases, CancellationToken ct) => Task.CompletedTask;

    public static Task<Dictionary<Guid, List<string>>> HeldByAgentAsync(MuthurDb db, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(new Dictionary<Guid, List<string>>());
}
