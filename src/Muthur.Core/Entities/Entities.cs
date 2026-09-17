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
