namespace Muthur.Contracts;

public sealed record HarnessCandidateDto(string Harness, string Model, string? Account, bool Limited, DateTimeOffset? LimitedUntil);

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

/// <summary>Whether the conductor is staffing validation, the limits it staffs within, and what it last did.</summary>
public sealed record ConductorStatusDto(
    bool Enabled, int Running, int MaxSessions, int SessionMinutes, int MaxAttempts, int IntervalSeconds,
    int StallProbeMinutes, DateTimeOffset? LastPass, string? LastAction);

public sealed record ConductorSwitch(bool Enabled);
