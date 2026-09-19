using Muthur.Contracts;

namespace Muthur.Core;

/// <summary>
/// Where a task's time went, reconstructed from the ledger. No event names the state a task entered, so the
/// event type is the record: this is that mapping, in one place, and the replay that turns it into intervals.
/// </summary>
public static class TaskStateTimeline
{
    /// <summary>One continuous stay in one state. <paramref name="Until"/> is null while it is still there.</summary>
    public readonly record struct Interval(TaskState State, DateTimeOffset From, DateTimeOffset? Until);

    /// <summary>The state an event type moves a task into, or null for the events that move nothing.</summary>
    public static TaskState? Enters(string eventType) => eventType switch
    {
        "task.added" => TaskState.Backlog,
        "task.claimed" => TaskState.InProgress,
        "task.released" => TaskState.Backlog,
        "task.claim_expired" => TaskState.Backlog,
        "task.reopened" => TaskState.Backlog,
        "task.blocked" => TaskState.Blocked,
        "task.unblocked" => TaskState.InProgress,
        "task.implemented" => TaskState.Validating,
        "task.validation_failed" => TaskState.InProgress,
        "task.validation_blocked" => TaskState.InProgress,
        "task.validated" => TaskState.Validated,
        "task.land_failed" => TaskState.InProgress,
        "task.landed" => TaskState.Done,
        "task.pr_opened" => TaskState.Done,
        "task.cancelled" => TaskState.Cancelled,
        _ => null,
    };

    /// <summary>
    /// Events for one task, in <c>Seq</c> order, oldest first. Consecutive events entering the same state
    /// extend the stay rather than starting a new one.
    /// </summary>
    public static IReadOnlyList<Interval> Replay(IEnumerable<(string Type, DateTimeOffset At)> events)
    {
        var intervals = new List<Interval>();
        foreach (var (type, at) in events)
        {
            if (Enters(type) is not { } state)
                continue; // an event that changes no state closes nothing: the stay it falls inside continues
            if (intervals.Count > 0)
            {
                var open = intervals[^1];
                if (open.State == state)
                    continue;
                intervals[^1] = open with { Until = at };
            }
            intervals.Add(new Interval(state, at, null));
        }
        return intervals;
    }

    /// <summary>How long the intervals overlap [<paramref name="from"/>, <paramref name="to"/>], per state.</summary>
    public static IReadOnlyDictionary<TaskState, TimeSpan> Within(
        IReadOnlyList<Interval> intervals, DateTimeOffset from, DateTimeOffset to)
    {
        var total = new Dictionary<TaskState, TimeSpan>();
        foreach (var interval in intervals)
        {
            var closed = interval.Until ?? to;
            var start = interval.From > from ? interval.From : from;
            var end = closed < to ? closed : to;
            // end == start is kept, not dropped: a task that never waited spent zero seconds validating,
            // which is a different fact from never having been there.
            if (end < start)
                continue;
            total[interval.State] = total.GetValueOrDefault(interval.State) + (end - start);
        }
        return total;
    }
}
