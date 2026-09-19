namespace Muthur.Contracts;

public sealed record HarnessCandidateDto(string Harness, string Model, string? Account, bool Limited, DateTimeOffset? LimitedUntil, string? ReasoningEffort);

public sealed record TierDto(string Tier, IReadOnlyList<HarnessCandidateDto> Candidates);

public sealed record AccountLimitRequest(string Account, DateTimeOffset? Until);

/// <summary>What an orchestrator reports after a headless worker ran, so the ledger knows which model did which unit.</summary>
/// <param name="Parent">
/// The branch of the run whose plan this unit came from, or null for a run an orchestrator started directly.
/// Last, and defaulted, so callers that report an unplanned run are untouched.
/// </param>
public sealed record WorkerRunReport(
    string? Task,
    string Tier,
    string Harness,
    string Model,
    string? Account,
    string Branch,
    string? Unit,
    bool Success,
    int DurationSeconds,
    decimal? CostUsd,
    string? Parent = null);

/// <summary>
/// Whether the conductor is staffing validation, the limits it staffs within, and what it last did.
/// </summary>
/// <param name="MaxSessions">What the configuration file asks for, whatever the founder has since said.</param>
/// <param name="Ceiling">Sessions a pass will actually run: the founder's number, lowered while they sleep.</param>
/// <param name="CeilingReason">One sentence saying where <paramref name="Ceiling"/> came from.</param>
/// <param name="Orchestrators">
/// Whether it may also start work from the backlog. The most expensive switch here, so it is readable without
/// anyone having to go through the ledger to find out which way it was last thrown.
/// </param>
public sealed record ConductorStatusDto(
    bool Enabled, int Running, int MaxSessions, int SessionMinutes, int MaxAttempts, int IntervalSeconds,
    int StallProbeMinutes, DateTimeOffset? LastPass, string? LastAction, int Ceiling, string CeilingReason,
    bool Orchestrators);

public sealed record ConductorSwitch(bool Enabled);

/// <summary>
/// Whether the conductor may also start orchestrator sessions from the backlog. Its own switch, and its own
/// route, because it is the half that begins new work rather than finishing work somebody else began.
/// </summary>
public sealed record ConductorOrchestratorSwitch(bool Enabled);

/// <summary>The founder moving the ceiling: a number of sessions, an unattended window, or <c>Clear</c> for both.</summary>
public sealed record ConductorSessionsRequest(int? Sessions, string? UnattendedFrom, string? UnattendedTo, int? UnattendedSessions, bool Clear);
