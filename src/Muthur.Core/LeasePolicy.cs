using Muthur.Contracts;
using Muthur.Core.Entities;

namespace Muthur.Core;

/// <summary>Claims and role holds are leases: they lapse unless the holder keeps showing signs of life.</summary>
public sealed record LeasePolicy(TimeSpan ClaimLease, TimeSpan RoleLease, TimeSpan AgentStaleAfter)
{
    public static readonly LeasePolicy Default = new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(3));

    public static readonly TimeSpan MaxClaimLease = TimeSpan.FromHours(8);

    public TimeSpan ResolveClaimLease(int? requestedMinutes)
    {
        if (requestedMinutes is null) return ClaimLease;
        if (requestedMinutes <= 0) throw Fail.Rule("invalid_lease", "Lease minutes must be positive.");
        var requested = TimeSpan.FromMinutes(requestedMinutes.Value);
        return requested > MaxClaimLease ? MaxClaimLease : requested;
    }

    /// <summary>A task can be claimed from backlog, or taken over when its in-progress claim has lapsed.</summary>
    public static bool IsClaimable(WorkTask task, DateTimeOffset now) =>
        task.State == TaskState.Backlog ||
        (task.State == TaskState.InProgress && IsLapsed(task, now));

    public static bool IsLapsed(WorkTask task, DateTimeOffset now) =>
        task.State == TaskState.InProgress && (task.ClaimExpires is null || task.ClaimExpires <= now);

    public AgentStatus StatusOf(Agent agent, DateTimeOffset now)
    {
        if (agent.LimitedUntil is { } until && until > now) return AgentStatus.Limited;
        return now - agent.LastHeartbeat <= AgentStaleAfter ? AgentStatus.Live : AgentStatus.Stale;
    }
}
