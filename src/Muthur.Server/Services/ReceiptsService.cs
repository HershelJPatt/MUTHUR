using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>
/// What the organization spent itself on over a window. One read of the ledger, counting each event as the thing
/// it actually is: a registration is an identity taken, a staffing is a session the conductor asked for, and the
/// two are reported side by side rather than summed, because one conductor session writes both.
/// Mutates nothing and records no ledger event.
/// </summary>
public sealed class ReceiptsService(Ledger ledger)
{
    public const int MinHours = 1;
    public const int MaxHours = 720;

    private static readonly string[] Registrations = ["agent.registered", "agent.reregistered"];
    private static readonly string[] WorkerRunTypes = ["worker.finished", "worker.failed"];

    /// <summary>Everything the window is read for: the session-bearing five, quota reports and failed verdicts.</summary>
    private static readonly string[] Counted =
    [
        "agent.registered", "agent.reregistered", "conductor.staffing", "worker.finished", "worker.failed",
        "account.limited", "validation.failed",
    ];

    /// <summary>
    /// The order a founder reads them in. <c>Validated</c> is absent: it is the gap between a pass and a land,
    /// and T-26 already surfaces that.
    /// </summary>
    private static readonly TaskState[] Timed =
        [TaskState.InProgress, TaskState.Validating, TaskState.Blocked, TaskState.Backlog];

    public Task<ReceiptsDto> ReadAsync(int hours, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, now) =>
        {
            // Clamped rather than refused: someone typing --hours 100000 wants everything, not an error.
            var since = now - TimeSpan.FromHours(Math.Clamp(hours, MinHours, MaxHours));

            var window = await db.Events
                .Where(e => e.At >= since && e.At <= now && Counted.Contains(e.Type))
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
            var receipts = TaskReceipts(tasks, window, history, since, now);
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
                runs);
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
    private static List<WorkerRunDto> Runs(IReadOnlyList<LedgerEvent> window) =>
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
                    // The one cost in receipts, on the one row that knows it. Nothing sums this: the runs that
                    // report nothing are not a random sample, so a total would omit the largest category.
                    Money(p, "costUsd"),
                    e.Type == "worker.finished",
                    e.At,
                    // Null on every run an orchestrator started itself, because the payload carries no such key.
                    Text(p, "parent"));
            })
            .ToList();

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
            var runs = mine.Count(e => WorkerRunTypes.Contains(e.Type));
            var failures = mine.Count(e => e.Type == "validation.failed");
            // Receipts is a record of what was spent, not a second board.
            if (staffings + runs + failures == 0) continue;

            // By Seq, never by At: a project with no validators records task.implemented and task.validated in
            // one mutation, so they share an At and only their order tells them apart.
            var intervals = TaskStateTimeline.Replay(
                history.Where(e => e.TaskId == task.Id).OrderBy(e => e.Seq).Select(e => (e.Type, e.At)));
            var time = TaskStateTimeline.Within(intervals, since, now);
            receipts.Add(new Receipt(task.Id, new TaskReceiptDto(
                Wire.TaskId(task.Id),
                task.Title,
                task.State,
                staffings,
                runs,
                mine.Count(e => e.Type == "worker.failed"),
                failures,
                time.GetValueOrDefault(TaskState.Validating).TotalSeconds,
                time.GetValueOrDefault(TaskState.InProgress).TotalSeconds,
                // Whether the spend ended in a landing: read from the history, because a landing is not one of
                // the event types the window is filtered to, and dated because a task that landed last week
                // spent nothing this one.
                history.Any(e => e.TaskId == task.Id && e.At >= since && e.At <= now &&
                    e.Type is "task.landed" or "task.pr_opened")),
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

    private static int Number(JsonElement payload, string name) =>
        Has(payload, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static decimal? Money(JsonElement payload, string name) =>
        Has(payload, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : null;
}
