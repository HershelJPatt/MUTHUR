namespace Muthur.Contracts;

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
    int OpenTasks);
