namespace Muthur.Contracts;

public sealed record HarnessCandidateDto(string Harness, string Model, string? Account, bool Limited, DateTimeOffset? LimitedUntil);

public sealed record TierDto(string Tier, IReadOnlyList<HarnessCandidateDto> Candidates);

public sealed record AccountLimitRequest(string Account, DateTimeOffset? Until);

/// <summary>What an orchestrator reports after a headless worker ran, so the ledger knows which model did which unit.</summary>
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
    decimal? CostUsd);

/// <summary>Whether the conductor is staffing validation, the limits it staffs within, and what it last did.</summary>
public sealed record ConductorStatusDto(
    bool Enabled, int Running, int MaxSessions, int SessionMinutes, int MaxAttempts, int IntervalSeconds,
    int StallProbeMinutes, DateTimeOffset? LastPass, string? LastAction);

public sealed record ConductorSwitch(bool Enabled);
