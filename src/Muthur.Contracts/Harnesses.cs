namespace Muthur.Contracts;

public sealed record ProbeAdmissionRequest(string Task, string Tier, string Harness, string Model, string Account, string RunId);

public sealed record ProbeAdmissionDto(string ReservationId, string Task, string RunId, bool MayExecute);

public sealed record ProbeReleaseRequest(string ReservationId);

public sealed record HarnessCandidateDto(string Harness, string Model, string? Account, bool Limited, DateTimeOffset? LimitedUntil, string? ReasoningEffort, int? MaxTurns = null);

public sealed record TierDto(string Tier, IReadOnlyList<HarnessCandidateDto> Candidates);

public sealed record AccountLimitRequest(string Account, DateTimeOffset? Until);

/// <summary>What an orchestrator reports after a headless worker ran, so the ledger knows which model did which unit.</summary>
/// <param name="Parent">
/// The branch of the run whose plan this unit came from, or null for a run an orchestrator started directly.
/// Last, and defaulted, so callers that report an unplanned run are untouched.
/// </param>
/// <param name="PromptBytes">The prompt the worker process was handed, in UTF-8 bytes; null when none started.</param>
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
    string? Parent = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? RunId = null, string? Status = null, string? FailureKind = null,
    string? BaseCommit = null, string? HeadCommit = null, string? SpecBlob = null, int? ExitCode = null,
    IReadOnlyList<WorkerAttemptReport>? Attempts = null, string? WorkKind = null, string? PolicyVersion = null, string? ReasoningEffort = null,
    int? CacheReadTokens = null, int? TotalTokens = null, int? PromptBytes = null);

/// <summary>One validator session the conductor believes it has running.</summary>
/// <param name="Task">"T-n".</param>
/// <param name="Role">The validator role the session was staffed for.</param>
public sealed record ConductorSessionDto(string Task, string Role);

/// <summary>A (task, role) pair the conductor has stopped staffing until one probe is let through.</summary>
/// <param name="Reason">"never started", "ran and failed" or "no verdict" — the three ways a session produces nothing.</param>
/// <param name="Failures">Consecutive unproductive sessions for this pair.</param>
/// <param name="NextProbeAt">When a probe is let through again.</param>
public sealed record ConductorStallDto(string Task, string Role, string Reason, int Failures, DateTimeOffset? NextProbeAt);

/// <summary>
/// Whether the conductor is staffing validation, the limits it staffs within, and what it last did.
/// </summary>
/// <param name="Running">How many sessions it believes are running. Always <c>Sessions.Count</c>; kept because every caller reads it.</param>
/// <param name="MaxSessions">What the configuration file asks for, whatever the founder has since said.</param>
/// <param name="Ceiling">Sessions a pass will actually run: the founder's number, lowered while they sleep.</param>
/// <param name="CeilingReason">One sentence saying where <paramref name="Ceiling"/> came from.</param>
/// <param name="Orchestrators">
/// Whether it may also start work from the backlog. The most expensive switch here, so it is readable without
/// anyone having to go through the ledger to find out which way it was last thrown.
/// </param>
/// <param name="Sessions">Which pairs those running sessions are — "enabled, 2 running" does not say which two.</param>
/// <param name="Stalls">Only pairs that have actually stalled. A pair with attempts left is not something a founder acts on.</param>
public sealed record ConductorStatusDto(
    bool Enabled, int Running, int MaxSessions, int SessionMinutes, int MaxAttempts, int IntervalSeconds,
    int StallProbeMinutes, DateTimeOffset? LastPass, string? LastAction, int Ceiling, string CeilingReason,
    bool Orchestrators,
    IReadOnlyList<ConductorSessionDto> Sessions, IReadOnlyList<ConductorStallDto> Stalls,
    string? StaffingWait = null, IReadOnlyList<LandingWaitDto>? Landings = null,
    IReadOnlyList<IncidentSuppressionDto>? IncidentSuppressions = null);

public sealed record LandingWaitDto(string Task, DateTimeOffset Since, string Reason);

public sealed record ConductorSwitch(bool Enabled);

/// <summary>
/// Whether the conductor may also start orchestrator sessions from the backlog. Its own switch, and its own
/// route, because it is the half that begins new work rather than finishing work somebody else began.
/// </summary>
public sealed record ConductorOrchestratorSwitch(bool Enabled);

/// <summary>The founder moving the ceiling: a number of sessions, an unattended window, or <c>Clear</c> for both.</summary>
public sealed record ConductorSessionsRequest(int? Sessions, string? UnattendedFrom, string? UnattendedTo, int? UnattendedSessions, bool Clear);
