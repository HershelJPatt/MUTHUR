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

/// <summary>One validator session the conductor believes it has running.</summary>
/// <param name="Task">"T-n".</param>
/// <param name="Role">The validator role the session was staffed for.</param>
public sealed record ConductorSessionDto(string Task, string Role);

/// <summary>A (task, role) pair the conductor has stopped staffing until one probe is let through.</summary>
/// <param name="Reason">"never started", "ran and failed" or "no verdict" — the three ways a session produces nothing.</param>
/// <param name="Failures">Consecutive unproductive sessions for this pair.</param>
/// <param name="NextProbeAt">When a probe is let through again.</param>
public sealed record ConductorStallDto(string Task, string Role, string Reason, int Failures, DateTimeOffset? NextProbeAt);

/// <summary>Whether the conductor is staffing validation, the limits it staffs within, and what it last did.</summary>
/// <param name="Running">How many sessions it believes are running. Always <c>Sessions.Count</c>; kept because every caller reads it.</param>
/// <param name="Sessions">Which pairs those are — "enabled, 2 running" does not say which two.</param>
/// <param name="Stalls">Only pairs that have actually stalled. A pair with attempts left is not something a founder acts on.</param>
public sealed record ConductorStatusDto(
    bool Enabled, int Running, int MaxSessions, int SessionMinutes, int MaxAttempts, int IntervalSeconds,
    int StallProbeMinutes, DateTimeOffset? LastPass, string? LastAction,
    IReadOnlyList<ConductorSessionDto> Sessions, IReadOnlyList<ConductorStallDto> Stalls);

public sealed record ConductorSwitch(bool Enabled);
