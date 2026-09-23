namespace Muthur.Contracts;

/// <param name="Count">Sessions that took this identity in the window.</param>
public sealed record SessionGroupDto(string? Tier, string Harness, string Model, string? Account, int Count);

/// <param name="ConductorSessions">Validator sessions the conductor started for this task.</param>
/// <param name="WorkerRuns">Headless worker runs reported against it, and how many failed.</param>
/// <param name="ValidationFailures">Verdicts of "no" in the window: the resubmission count.</param>
/// <param name="CostUsd">
/// What it cost to land, so far: the sum over this task's session and worker rows that reported a cost, or null
/// when none of them did. A task whose rows only partly report is partly priced, and the rows say which.
/// </param>
/// <param name="InputTokens">Summed over the rows that reported them, by the same rule as <paramref name="CostUsd"/>.</param>
/// <param name="Tokens">
/// What the rows report having used, summed: input plus output where a row has both, its one total where it has
/// only that. Cache reads are on the rows and never in here — they are the part that was not paid for twice.
/// </param>
/// <param name="SessionSeconds">Wall time of the conductor-started sessions that ran for this task.</param>
/// <param name="FailedWorkerMinutes">Minutes spent in worker runs that failed: the waste, in the unit the plan counts it in.</param>
/// <param name="BlockedWorkerRuns">Worker runs that ended <c>blocked</c>: the environment stopped them, not the work.</param>
/// <param name="TimeoutSessions">Sessions that hit the wall clock and left nothing.</param>
public sealed record TaskReceiptDto(
    string Task, string Title, TaskState State,
    int ConductorSessions, int WorkerRuns, int WorkerRunsFailed, int ValidationFailures,
    double ValidatingSeconds, double InProgressSeconds, bool Landed,
    decimal? CostUsd = null, int? InputTokens = null, int? OutputTokens = null,
    double SessionSeconds = 0, double FailedWorkerMinutes = 0, int BlockedWorkerRuns = 0, int TimeoutSessions = 0,
    int? Tokens = null);

/// <param name="Longest">The single longest stay any one task had in this state, and which task.</param>
public sealed record StateTimeDto(TaskState State, double Seconds, int Tasks, double Longest, string? LongestTask);

/// <param name="Times">How many times this account was reported out of quota in the window.</param>
public sealed record AccountReceiptDto(string Account, int Times, DateTimeOffset? LimitedUntil);

/// <param name="Worker">"harness/model", exactly as the event records it — the row must name its harness.</param>
/// <param name="CostUsd">
/// What the harness reported, or null when it reports none. Summed into <see cref="ReceiptsDto.CostUsd"/> only
/// alongside <see cref="ReceiptsDto.CostReportedRuns"/>, which says how many rows the sum is made of.
/// </param>
/// <param name="Parent">
/// The run this one was planned by, or null when an orchestrator started it directly. What makes the cost of a
/// two-level fan-out attributable to the run that caused it.
/// </param>
public sealed record WorkerRunDto(
    string? Task, string Tier, string Worker, string? Account, string? Unit,
    int Seconds, decimal? CostUsd, bool Success, DateTimeOffset At, string? Parent = null,
    int? InputTokens = null, int? OutputTokens = null,
    string? RunId = null, string? Status = null, string? FailureKind = null,
    string? BaseCommit = null, string? HeadCommit = null, string? SpecBlob = null, int? ExitCode = null,
    string? WorkKind = null, string? PolicyVersion = null, string? ReasoningEffort = null,
    int? CacheReadTokens = null, int? TotalTokens = null);

/// <summary>One harness attempt of a conductor-started session, productive or not: a <c>conductor.session_finished</c> event.</summary>
/// <param name="Role">"#orchestrator", or the validator role key the session held.</param>
/// <param name="CostUsd">What the harness reported for the attempt, or null when it reports none.</param>
/// <param name="FailureKind"><c>timeout</c>, <c>quota</c>, <c>process_exit</c>, <c>missing_or_failed_report</c>, or null when it finished.</param>
/// <param name="TotalTokens">The one number a harness reports when it does not split input from output.</param>
/// <param name="Started">Whether a process really ran; false when the launcher refused before one did.</param>
public sealed record SessionRunDto(
    string? Task, string Role, string Harness, string Model, string? Account,
    int Seconds, decimal? CostUsd, int? InputTokens, int? OutputTokens, int? CacheReadTokens, int? TotalTokens,
    string? FailureKind, bool Started, DateTimeOffset At);

/// <summary>Sessions and worker runs on one harness and model, with what they reported.</summary>
/// <param name="CostUsd">Summed over the rows that reported one; null when none did.</param>
/// <param name="Tokens">Input plus output per row where both are known, else the row's one total; summed over the rows that have either.</param>
/// <param name="Seconds">Wall time of every row, reported or not: time is always known.</param>
public sealed record HarnessReceiptDto(
    string Harness, string Model, int Sessions, int WorkerRuns,
    decimal? CostUsd, int? InputTokens, int? OutputTokens, int? Tokens, double Seconds);

/// <param name="Sessions">Identities taken in the window: <c>agent.registered</c> plus <c>agent.reregistered</c>.</param>
/// <param name="ConductorSessions">
/// Validator sessions the conductor started. Reported beside <paramref name="Sessions"/> and never added to it:
/// a staffing and the registration it causes are one session seen from two ends.
/// </param>
/// <param name="Runs">One row per worker run in the window, newest first.</param>
/// <param name="CostUsd">
/// The sum over every session and worker row that reported a cost, or null when none did. Read it with the pair
/// beside it: <paramref name="CostReportedRuns"/> rows are in the sum and <paramref name="CostUnreportedRuns"/>
/// are not, and the rows that report nothing are not a random sample — so the pair is what says how much of
/// the window is priced, and a total shown without it is a confident answer to a different question.
/// </param>
/// <param name="SessionRuns">One row per conductor session attempt in the window, newest first.</param>
/// <param name="ByHarness">The same rows grouped by harness and model, most rows first.</param>
/// <param name="Tokens">Every row's tokens summed by the rule <see cref="TaskReceiptDto.Tokens"/> states, or null when no row reports any.</param>
public sealed record ReceiptsDto(
    DateTimeOffset Since,
    DateTimeOffset At,
    int Sessions,
    int ConductorSessions,
    int WorkerRuns,
    double WorkerSeconds,
    IReadOnlyList<SessionGroupDto> ByAccount,
    IReadOnlyList<TaskReceiptDto> Tasks,
    IReadOnlyList<StateTimeDto> StateTime,
    IReadOnlyList<AccountReceiptDto> Accounts,
    IReadOnlyList<WorkerRunDto> Runs,
    decimal? CostUsd = null,
    int CostReportedRuns = 0,
    int CostUnreportedRuns = 0,
    IReadOnlyList<SessionRunDto>? SessionRuns = null,
    IReadOnlyList<HarnessReceiptDto>? ByHarness = null,
    int? Tokens = null);
