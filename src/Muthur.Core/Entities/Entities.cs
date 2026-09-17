using Muthur.Contracts;

namespace Muthur.Core.Entities;

public sealed class Project
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public required string RepoPath { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public LandMode LandMode { get; set; }
    /// <summary>Validator role keys that must all say "yes" before a task may land.</summary>
    public List<string> RequiredValidators { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Agent
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset LastHeartbeat { get; set; }
    public DateTimeOffset? LimitedUntil { get; set; }
    public string? Summary { get; set; }
    public required string Harness { get; set; }
    public required string Model { get; set; }
    public string? Tier { get; set; }
    public string? Account { get; set; }

    public string ModelLabel => $"{Harness}/{Model}";
}

/// <summary>A unit of work. Named WorkTask to stay clear of System.Threading.Tasks.Task.</summary>
public sealed class WorkTask
{
    /// <summary>Sequential; shown to humans and agents as "T-{Id}".</summary>
    public int Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public required string Title { get; set; }
    public string Body { get; set; } = "";
    public TaskState State { get; set; }
    public int Priority { get; set; }
    public Guid? OwnerAgentId { get; set; }
    public Agent? Owner { get; set; }
    public DateTimeOffset? ClaimExpires { get; set; }
    public string? SpecPath { get; set; }
    public string? Branch { get; set; }
    public string? PrUrl { get; set; }
    public int? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
}

/// <summary>One row of the append-only ledger. Never updated, never deleted.</summary>
public sealed class LedgerEvent
{
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public Guid? ActorAgentId { get; set; }
    /// <summary>"harness/model" of the acting agent, for cross-provider rules and later analysis.</summary>
    public string? ActorModel { get; set; }
    public required string Type { get; set; }
    public int? TaskId { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

/// <summary>A standing responsibility (validator, comms, observability…) described by a brief.</summary>
public sealed class Role
{
    public required string Key { get; set; }
    public string BriefMd { get; set; } = "";
    /// <summary>Validator roles may give verdicts on tasks and can be listed in a project's required validators.</summary>
    public bool IsValidator { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Who currently holds a role. One holder per role; the hold is a lease.</summary>
public sealed class RoleHold
{
    public required string RoleKey { get; set; }
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset LeaseExpires { get; set; }
}

/// <summary>Current verdict of one required validator on one task. History lives in the ledger.</summary>
public sealed class TaskValidation
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public required string ValidatorKey { get; set; }
    public Verdict Verdict { get; set; }
    public string? Evidence { get; set; }
    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }
    public DateTimeOffset? At { get; set; }
}

public enum Recipient { Agent, Role, Founder }

/// <summary>A message on the internal bus: agent ↔ agent, agent → role, agent ↔ founder, hub → anyone.</summary>
public sealed class Message
{
    public long Id { get; set; }
    /// <summary>Null when the sender is the founder or the hub itself.</summary>
    public Guid? FromAgentId { get; set; }
    public required string FromName { get; set; }
    public Recipient ToKind { get; set; }
    /// <summary>Agent name or role key; null for the founder.</summary>
    public string? ToKey { get; set; }
    public required string Body { get; set; }
    /// <summary>The sender cannot continue until this is answered.</summary>
    public bool Blocking { get; set; }
    public int? TaskId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public string? ReadBy { get; set; }
}

public enum RequestStatus { Open, Answered, Cancelled }

/// <summary>A decision only a founder can make. While open, the task it concerns is blocked.</summary>
public sealed class FounderRequest
{
    public int Id { get; set; }
    public int? TaskId { get; set; }
    public Guid AgentId { get; set; }
    public required string AgentName { get; set; }
    public required string Question { get; set; }
    public List<string> Options { get; set; } = [];
    public RequestStatus Status { get; set; }
    public string? Answer { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
}

/// <summary>A subscription or API account that is out of quota until <see cref="LimitedUntil"/>. Work is routed around it.</summary>
public sealed class AccountLimit
{
    public required string Account { get; set; }
    public DateTimeOffset LimitedUntil { get; set; }
    public required string ReportedBy { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
}
