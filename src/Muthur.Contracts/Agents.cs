namespace Muthur.Contracts;

/// <summary>
/// Harness identifiers have surrounding whitespace trimmed and case preserved. Unknown identifiers are allowed;
/// kit and catalog identifiers must match case for doctor checks.
/// </summary>
public sealed record RegisterAgentRequest(string Name, string Harness, string Model, string? Tier = null, string? Account = null);

/// <summary><see cref="Token"/> is shown exactly once; the CLI stores it under {MUTHUR_HOME}/agents/{name}.token.</summary>
public sealed record RegisterAgentResponse(AgentDto Agent, string Token);

public sealed record HeartbeatRequest(string? Summary = null);

public sealed record LimitedRequest(DateTimeOffset? Until);

public sealed record AgentDto(
    Guid Id,
    string Name,
    AgentStatus Status,
    DateTimeOffset LastHeartbeat,
    DateTimeOffset? LimitedUntil,
    string? Summary,
    string Harness,
    string Model,
    string? Tier,
    string? Account,
    IReadOnlyList<string> Roles,
    int OpenTasks,
    bool ConductorStaffed);

/// <summary>
/// The roster as one view: the rows it shows, and how many it left out. The count is part of the payload
/// rather than a thing the reader recomputes, because a filtered list that does not say what it dropped
/// reads exactly like a quiet organization. Zero when <c>all</c> was asked for — nothing was omitted.
/// </summary>
public sealed record AgentRosterDto(IReadOnlyList<AgentDto> Agents, int ConductorHidden);
