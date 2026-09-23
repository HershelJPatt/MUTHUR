using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>
/// What the organization spent itself on over a window. One read of the ledger, counting each event as the thing
/// it actually is: a registration is an identity taken, a staffing is a session the conductor asked for, and the
/// two are reported side by side rather than summed, because one conductor session writes both. Money is summed,
/// and every sum comes with the count of rows that are in it and the count that are not, because the rows that
/// report no cost are not a random sample.
/// Mutates nothing and records no ledger event.
/// </summary>
public sealed class ReceiptsService(Ledger ledger)
{
    public const int MinHours = 1;
    public const int MaxHours = 720;

    private static readonly string[] Registrations = ["agent.registered", "agent.reregistered"];
    private static readonly string[] WorkerRunTypes = ["worker.finished", "worker.failed"];
    private const string SessionFinished = "conductor.session_finished";

    /// <summary>Everything the window is read for: the session-bearing five, finished sessions, quota reports and failed verdicts.</summary>
    private static readonly string[] Counted =
    [
        "agent.registered", "agent.reregistered", "conductor.staffing", "worker.finished", "worker.failed",
        SessionFinished, "account.limited", "validation.failed",
    ];

    /// <summary>
    /// The order a founder reads them in. <c>Validated</c> is absent: it is the gap between a pass and a land,
    /// and T-26 already surfaces that.
    /// </summary>
    private static readonly TaskState[] Timed =
        [TaskState.InProgress, TaskState.Validating, TaskState.Blocked, TaskState.Backlog];

    /// <param name="taskId">When given, only that task's events are read: its rows, its receipt, and totals over them alone.</param>
    public Task<ReceiptsDto> ReadAsync(int hours, int? taskId = null, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, now) =>
        {
            // Clamped rather than refused: someone typing --hours 100000 wants everything, not an error.
            var since = now - TimeSpan.FromHours(Math.Clamp(hours, MinHours, MaxHours));

            var window = await db.Events
                .Where(e => e.At >= since && e.At <= now && Counted.Contains(e.Type) && (taskId == null || e.TaskId == taskId))
                .OrderBy(e => e.Seq)
                .ToListAsync(ct);
            var taskIds = window.Where(e => e.TaskId != null).Select(e => e.TaskId!.Value).Distinct().ToList();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(ct);
            // Every event of those tasks, not only the ones in the window: what state a task sat in when the
            // window opened is only knowable from before it. A task's whole history is a handful of rows.
            var history = await db.Events
                .Where(e => e.TaskId != null && taskIds.Contains(e.TaskId!.Value))
                .OrderBy(e => e.Seq)
                .ToListAsync(ct);
            var limits = await db.AccountLimits.Where(l => l.LimitedUntil > now).ToListAsync(ct);

            var runs = Runs(window);
            var sessions = Sessions(window);
            var receipts = TaskReceipts(tasks, window, history, since, now);
            var costs = runs.Select(r => r.CostUsd).Concat(sessions.Select(s => s.CostUsd)).ToList();
            var tokens = Sum(runs.Select(Tokens).Concat(sessions.Select(Tokens)));
            return new ReceiptsDto(
                since,
                now,
                window.Count(e => Registrations.Contains(e.Type)),
                window.Count(e => e.Type == "conductor.staffing"),
                runs.Count,
                runs.Sum(r => (double)r.Seconds),
                ByAccount(window),
                receipts.Select(r => r.Dto).ToList(),
                StateTime(receipts, since, now),
                Accounts(window, limits),
                runs,
                Sum(costs),
                costs.Count(c => c is not null),
                costs.Count(c => c is null),
                sessions,
                ByHarness(runs, sessions),
                tokens);
        }, ct);

    /// <summary>
    /// Registrations grouped by the identity they took. Registrations are the honest source for this: the launcher
    /// takes an identity only after the harness is found and its executable resolved, so one of these means a
    /// process really started on that account.
    /// </summary>
    private static List<SessionGroupDto> ByAccount(IReadOnlyList<LedgerEvent> window) =>
        window
            .Where(e => Registrations.Contains(e.Type))
            .Select(e => Payload(e.PayloadJson))
            .Select(p => (Tier: Text(p, "tier"), Harness: Text(p, "harness") ?? "", Model: Text(p, "model") ?? "", Account: Text(p, "account")))
            .GroupBy(identity => identity)
            .Select(g => new SessionGroupDto(g.Key.Tier, g.Key.Harness, g.Key.Model, g.Key.Account, g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Harness, StringComparer.Ordinal)
            .ToList();

    /// <summary>Newest first, by <c>Seq</c>: two runs reported in the same mutation share an <c>At</c>.</summary>
    internal static List<WorkerRunDto> Runs(IReadOnlyList<LedgerEvent> window) =>
        window
            .Where(e => WorkerRunTypes.Contains(e.Type))
            .OrderByDescending(e => e.Seq)
            .Select(e =>
            {
                var p = Payload(e.PayloadJson);
                return new WorkerRunDto(
                    e.TaskId is { } id ? Wire.TaskId(id) : null,
                    Text(p, "tier") ?? "",
                    Text(p, "worker") ?? "",
                    Text(p, "account"),
                    Text(p, "unit"),
                    Number(p, "seconds"),
                    Money(p, "costUsd"),
                    e.Type == "worker.finished",
                    e.At,
                    // Null on every run an orchestrator started itself, because the payload carries no such key.
                    Text(p, "parent"),
                    Count(p, "inputTokens"),
                    Count(p, "outputTokens"),
                    Text(p, "runId"), Text(p, "status"), Text(p, "failureKind"), Text(p, "baseCommit"), Text(p, "headCommit"), Text(p, "specBlob"),
                    Count(p, "exitCode"),
                    Text(p, "workKind"), Text(p, "policyVersion"), Text(p, "reasoningEffort"),
                    Count(p, "cacheReadTokens"), Count(p, "totalTokens"));
            })
            .ToList();

    /// <summary>One row per harness attempt of a conductor-started session, newest first by <c>Seq</c>.</summary>
    internal static List<SessionRunDto> Sessions(IReadOnlyList<LedgerEvent> window) =>
        window
            .Where(e => e.Type == SessionFinished)
            .OrderByDescending(e => e.Seq)
            .Select(e =>
            {
                var p = Payload(e.PayloadJson);
                return new SessionRunDto(
                    e.TaskId is { } id ? Wire.TaskId(id) : null,
                    Text(p, "role") ?? "",
                    Text(p, "harness") ?? "",
                    Text(p, "model") ?? "",
                    Text(p, "account"),
                    Number(p, "seconds"),
                    Money(p, "costUsd"),
                    Count(p, "inputTokens"),
                    Count(p, "outputTokens"),
                    Count(p, "cacheReadTokens"),
                    Count(p, "totalTokens"),
                    Text(p, "failureKind"),
                    // Absent reads as started: the event exists because an attempt did, and only a launcher that
                    // refused before a process ran says otherwise.
                    !Has(p, "started", out var started) || started.ValueKind != JsonValueKind.False,
                    e.At);
            })
            .ToList();

    /// <summary>Sessions and worker runs on one harness and model. A worker row names its harness as "harness/model".</summary>
    private static List<HarnessReceiptDto> ByHarness(IReadOnlyList<WorkerRunDto> runs, IReadOnlyList<SessionRunDto> sessions)
    {
        var rows = sessions
            .Select(s => (s.Harness, s.Model, Session: true, Seconds: (double)s.Seconds, s.CostUsd, s.InputTokens, s.OutputTokens, Tokens: Tokens(s)))
            .Concat(runs.Select(r =>
            {
                var slash = r.Worker.IndexOf('/');
                var harness = slash < 0 ? r.Worker : r.Worker[..slash];
                var model = slash < 0 ? "" : r.Worker[(slash + 1)..];
                return (Harness: harness, Model: model, Session: false, Seconds: (double)r.Seconds, r.CostUsd, r.InputTokens, r.OutputTokens, Tokens: Tokens(r));
            }));
        return rows
            .GroupBy(row => (row.Harness, row.Model))
            .Select(g => new HarnessReceiptDto(
                g.Key.Harness, g.Key.Model,
                g.Count(row => row.Session), g.Count(row => !row.Session),
                Sum(g.Select(row => row.CostUsd)),
                Sum(g.Select(row => row.InputTokens)),
                Sum(g.Select(row => row.OutputTokens)),
                Sum(g.Select(row => row.Tokens)),
                g.Sum(row => row.Seconds)))
            .OrderByDescending(h => h.Sessions + h.WorkerRuns)
            .ThenBy(h => h.Harness, StringComparer.Ordinal)
            .ThenBy(h => h.Model, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Receipt(int Id, TaskReceiptDto Dto,
        IReadOnlyList<TaskStateTimeline.Interval> Intervals, IReadOnlyDictionary<TaskState, TimeSpan> Time);

    private static List<Receipt> TaskReceipts(
        IReadOnlyList<WorkTask> tasks, IReadOnlyList<LedgerEvent> window, IReadOnlyList<LedgerEvent> history,
        DateTimeOffset since, DateTimeOffset now)
    {
        var receipts = new List<Receipt>();
        foreach (var task in tasks)
        {
            var mine = window.Where(e => e.TaskId == task.Id).ToList();
            var staffings = mine.Count(e => e.Type == "conductor.staffing");
            var runs = Runs(mine);
            var sessions = Sessions(mine);
            var failures = mine.Count(e => e.Type == "validation.failed");
            // Receipts is a record of what was spent, not a second board.
            if (staffings + runs.Count + sessions.Count + failures == 0) continue;

            // By Seq, never by At: a project with no validators records task.implemented and task.validated in
            // one mutation, so they share an At and only their order tells them apart.
            var intervals = TaskStateTimeline.ReplayWithPayload(
                history.Where(e => e.TaskId == task.Id).OrderBy(e => e.Seq).Select(e => (e.Type, e.At, (string?)e.PayloadJson)));
            var time = TaskStateTimeline.Within(intervals, since, now);
            receipts.Add(new Receipt(task.Id, new TaskReceiptDto(
                Wire.TaskId(task.Id),
                task.Title,
                task.State,
                staffings,
                runs.Count,
                runs.Count(r => !r.Success),
                failures,
                time.GetValueOrDefault(TaskState.Validating).TotalSeconds,
                time.GetValueOrDefault(TaskState.InProgress).TotalSeconds,
                // Whether the spend ended in a landing: read from the history, because a landing is not one of
                // the event types the window is filtered to, and dated because a task that landed last week
                // spent nothing this one.
                history.Any(e => e.TaskId == task.Id && e.At >= since && e.At <= now &&
                    e.Type is "task.landed" or "task.pr_opened"),
                Sum(runs.Select(r => r.CostUsd).Concat(sessions.Select(s => s.CostUsd))),
                Sum(runs.Select(r => r.InputTokens).Concat(sessions.Select(s => s.InputTokens))),
                Sum(runs.Select(r => r.OutputTokens).Concat(sessions.Select(s => s.OutputTokens))),
                sessions.Sum(s => (double)s.Seconds),
                runs.Where(r => !r.Success).Sum(r => r.Seconds / 60d),
                runs.Count(r => r.Status == "blocked"),
                sessions.Count(s => s.FailureKind == "timeout"),
                Sum(runs.Select(Tokens).Concat(sessions.Select(Tokens)))),
                intervals, time));
        }

        return receipts
            .OrderByDescending(r => r.Dto.ConductorSessions + r.Dto.WorkerRuns)
            .ThenByDescending(r => r.Dto.ValidationFailures)
            .ThenBy(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// The prescribed order, imposed rather than enumerated: <see cref="TaskStateTimeline.Within"/> answers with a
    /// plain dictionary, which guarantees no order at all.
    /// </summary>
    private static List<StateTimeDto> StateTime(IReadOnlyList<Receipt> receipts, DateTimeOffset since, DateTimeOffset now)
    {
        var rows = new List<StateTimeDto>();
        foreach (var state in Timed)
        {
            var seconds = 0d;
            var tasks = 0;
            var longest = -1d;   // so a stay of zero seconds still names its task
            string? longestTask = null;
            foreach (var receipt in receipts)
            {
                if (!receipt.Time.TryGetValue(state, out var total)) continue;
                tasks++;
                seconds += total.TotalSeconds;
                foreach (var stay in receipt.Intervals.Where(i => i.State == state))
                {
                    // One continuous stay, clipped to the window by the same rule the totals use.
                    if (TaskStateTimeline.Within([stay], since, now).TryGetValue(state, out var length) &&
                        length.TotalSeconds > longest)
                    {
                        longest = length.TotalSeconds;
                        longestTask = receipt.Dto.Task;
                    }
                }
            }
            if (tasks > 0) rows.Add(new StateTimeDto(state, seconds, tasks, Math.Max(longest, 0), longestTask));
        }
        return rows;
    }

    private static List<AccountReceiptDto> Accounts(IReadOnlyList<LedgerEvent> window, IReadOnlyList<AccountLimit> limits)
    {
        var reported = window
            .Where(e => e.Type == "account.limited")
            .Select(e => Text(Payload(e.PayloadJson), "account"))
            .Where(account => account is { Length: > 0 })
            .GroupBy(account => account!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // An account still limited now belongs here even if it was reported before the window opened: the
        // ceiling it hit is in force while the founder is reading.
        return reported.Keys
            .Union(limits.Select(l => l.Account), StringComparer.Ordinal)
            .Select(account => new AccountReceiptDto(
                account,
                reported.GetValueOrDefault(account),
                limits.FirstOrDefault(l => string.Equals(l.Account, account, StringComparison.Ordinal))?.LimitedUntil))
            .OrderByDescending(a => a.Times)
            .ThenBy(a => a.Account, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What one row used: input plus output when the harness split them, its one total when it did not, null when
    /// it reported neither. Cache reads are never added: they are the part of the context that was not paid for again.
    /// </summary>
    private static int? Tokens(WorkerRunDto run) => Tokens(run.InputTokens, run.OutputTokens, run.TotalTokens);

    private static int? Tokens(SessionRunDto session) => Tokens(session.InputTokens, session.OutputTokens, session.TotalTokens);

    private static int? Tokens(int? input, int? output, int? total) =>
        input is { } i && output is { } o ? i + o : total;

    /// <summary>The sum of what was reported, or null when nothing was: a null row adds nothing, and no rows add to nothing.</summary>
    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        decimal? total = null;
        foreach (var value in values)
            if (value is { } v) total = (total ?? 0) + v;
        return total;
    }

    private static int? Sum(IEnumerable<int?> values)
    {
        int? total = null;
        foreach (var value in values)
            if (value is { } v) total = (total ?? 0) + v;
        return total;
    }

    /// <summary>
    /// The payload as an object, or nothing at all when it will not parse. A receipts view must never be the thing
    /// that fails: an unreadable payload costs its own dimensions, not the report the founder came for.
    /// </summary>
    private static JsonElement Payload(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Cloned: a JsonElement does not outlive the document it was read from.
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool Has(JsonElement payload, string name, out JsonElement value)
    {
        value = default;
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out value);
    }

    private static string? Text(JsonElement payload, string name) =>
        Has(payload, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement payload, string name) => Count(payload, name) ?? 0;

    /// <summary>An integer the payload may not carry, kept null rather than zeroed: absent and none are different facts.</summary>
    private static int? Count(JsonElement payload, string name) =>
        Has(payload, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static decimal? Money(JsonElement payload, string name) =>
        Has(payload, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : null;
}
