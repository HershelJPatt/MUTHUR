using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>
/// What a conductor-started session cost, on the ledger. One <c>conductor.session_finished</c> per harness attempt,
/// whether or not the session did its job: the receipts that judge the organization's spend read this row, and a
/// session that timed out or hit a quota is exactly the kind of spend they exist to show. The verdict, the claim
/// and the failure classification are recorded elsewhere; this row is only the bill.
/// </summary>
internal static class SessionReceipts
{
    public const string Event = "conductor.session_finished";

    /// <summary>
    /// A timeout is two different things depending on what the session left behind: with notes, the next session
    /// resumes; without them, it restarts from the codebase, which is the cost the card should name.
    /// </summary>
    public static IReadOnlyList<WorkerAttempt> NotesAware(IReadOnlyList<WorkerAttempt> attempts, bool hasNotes) =>
        hasNotes ? attempts : [.. attempts.Select(a => a.FailureKind == "timeout" ? a with { FailureKind = "timeout_without_notes" } : a)];

    public static Task RecordAsync(Ledger ledger, int taskId, string role, IReadOnlyList<WorkerAttempt> attempts, CancellationToken ct) =>
        attempts.Count == 0
            ? Task.CompletedTask
            : ledger.MutateAsync(Caller.Founder, m =>
            {
                foreach (var attempt in attempts)
                    m.Record(Event, taskId, new
                    {
                        role,
                        harness = attempt.Candidate.Harness,
                        model = attempt.Candidate.Model.Length > 0 ? attempt.Candidate.Model : "default",
                        account = attempt.Candidate.Account,
                        seconds = (int)attempt.Duration.TotalSeconds,
                        costUsd = attempt.Outcome.CostUsd,
                        inputTokens = attempt.Outcome.InputTokens,
                        outputTokens = attempt.Outcome.OutputTokens,
                        cacheReadTokens = attempt.Outcome.CacheReadTokens,
                        totalTokens = attempt.Outcome.TotalTokens,
                        failureKind = attempt.FailureKind,
                        exitCode = attempt.ExitCode,
                        started = attempt.Started,
                        runId = attempt.RunId,
                        promptBytes = attempt.PromptBytes,
                    });
                return Task.CompletedTask;
            }, ct);
}
