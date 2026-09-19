namespace Muthur.Contracts;

/// <param name="Count">Sessions that took this identity in the window.</param>
public sealed record SessionGroupDto(string? Tier, string Harness, string Model, string? Account, int Count);

/// <param name="ConductorSessions">Validator sessions the conductor started for this task.</param>
/// <param name="WorkerRuns">Headless worker runs reported against it, and how many failed.</param>
/// <param name="ValidationFailures">Verdicts of "no" in the window: the resubmission count.</param>
public sealed record TaskReceiptDto(
    string Task, string Title, TaskState State,
    int ConductorSessions, int WorkerRuns, int WorkerRunsFailed, int ValidationFailures,
    double ValidatingSeconds, double InProgressSeconds, bool Landed);

/// <param name="Longest">The single longest stay any one task had in this state, and which task.</param>
public sealed record StateTimeDto(TaskState State, double Seconds, int Tasks, double Longest, string? LongestTask);

/// <param name="Times">How many times this account was reported out of quota in the window.</param>
public sealed record AccountReceiptDto(string Account, int Times, DateTimeOffset? LimitedUntil);

/// <param name="Worker">"harness/model", exactly as the event records it — the row must name its harness.</param>
/// <param name="CostUsd">What the harness reported, or null when it reports none. Never summed.</param>
public sealed record WorkerRunDto(
    string? Task, string Tier, string Worker, string? Account, string? Unit,
    int Seconds, decimal? CostUsd, bool Success, DateTimeOffset At);

/// <param name="Sessions">Identities taken in the window: <c>agent.registered</c> plus <c>agent.reregistered</c>.</param>
/// <param name="ConductorSessions">
/// Validator sessions the conductor started. Reported beside <paramref name="Sessions"/> and never added to it:
/// a staffing and the registration it causes are one session seen from two ends.
/// </param>
/// <param name="Runs">One row per worker run in the window, newest first. The only place money appears.</param>
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
    IReadOnlyList<WorkerRunDto> Runs);
