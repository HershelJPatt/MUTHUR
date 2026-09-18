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
    /// <summary>External sources polled for inbound items, e.g. "github:owner/repo".</summary>
    public List<string> IngestSources { get; set; } = [];
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
    /// <summary>How many agents may hold this role at once. Only a validator role may exceed 1: "who is on call" has one answer.</summary>
    public int Holders { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One agent's hold on a role. A role may have up to <see cref="Role.Holders"/> of these; the hold is a lease.</summary>
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

public enum InboundStatus { Unclaimed, Claimed, Converted, Dismissed }

/// <summary>Something that arrived from outside (an issue, an email, a chat message) and needs someone to decide what it becomes.</summary>
public sealed class InboundItem
{
    /// <summary>Sequential; shown as "I-{Id}".</summary>
    public int Id { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    /// <summary>Where it came from, e.g. "github:owner/repo" or "manual".</summary>
    public required string Source { get; set; }
    /// <summary>Identity within the source; (Source, ExternalId) is unique, so re-polling never duplicates.</summary>
    public required string ExternalId { get; set; }
    public required string Title { get; set; }
    public string Body { get; set; } = "";
    public string? Url { get; set; }
    public string? Author { get; set; }
    public InboundStatus Status { get; set; }
    public Guid? ClaimedByAgentId { get; set; }
    public Agent? ClaimedBy { get; set; }
    public int? TaskId { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How far a source has been read. Lets the hub catch up after being off for hours.</summary>
public sealed class IngestCursor
{
    public required string Source { get; set; }
    public string? Cursor { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A place the organization is allowed to send to. Only the founder defines these: it is the allowlist.</summary>
public sealed class OutboundTarget
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    /// <summary>Delivery mechanism: "file", "discord-webhook", "github-issue", …</summary>
    public required string Channel { get; set; }
    /// <summary>Channel-specific address. Often a secret itself (a webhook URL), so it is never shown to agents.</summary>
    public required string Address { get; set; }
    public bool RequiresFounderApproval { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum OutboundStatus { PendingReview, Rejected, AwaitingFounder, Approved, Sent, Failed }

/// <summary>Bytes that want to leave the machine. Immutable once drafted: what was reviewed is what is sent.</summary>
public sealed class OutboundMessage
{
    /// <summary>Sequential; shown as "O-{Id}".</summary>
    public int Id { get; set; }
    public Guid TargetId { get; set; }
    public OutboundTarget? Target { get; set; }
    public int? TaskId { get; set; }
    public required string Body { get; set; }
    public required string BodySha256 { get; set; }
    /// <summary>Masked excerpts that might be credentials. A flagged message needs the founder's approval whatever its target.</summary>
    public List<string> Flags { get; set; } = [];
    public OutboundStatus Status { get; set; }
    public Guid? AuthorAgentId { get; set; }
    public required string AuthorName { get; set; }
    public string? AuthorModel { get; set; }
    public Guid? ReviewerAgentId { get; set; }
    public string? ReviewerName { get; set; }
    public string? ReviewerModel { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? FounderApprovedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
