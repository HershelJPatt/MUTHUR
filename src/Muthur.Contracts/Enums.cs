using System.Text.Json.Serialization;

namespace Muthur.Contracts;

public enum TaskState
{
    [JsonStringEnumMemberName("backlog")] Backlog,
    [JsonStringEnumMemberName("in_progress")] InProgress,
    [JsonStringEnumMemberName("blocked")] Blocked,
    [JsonStringEnumMemberName("validating")] Validating,
    [JsonStringEnumMemberName("validated")] Validated,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

public enum LandMode
{
    /// <summary>The hub merges the task branch into the default branch itself.</summary>
    [JsonStringEnumMemberName("merge")] Merge,
    /// <summary>The hub pushes the branch and opens a pull request; a human merges.</summary>
    [JsonStringEnumMemberName("pr")] Pr,
}

public enum Verdict
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("yes")] Yes,
    [JsonStringEnumMemberName("no")] No,
}

public enum AgentStatus
{
    [JsonStringEnumMemberName("live")] Live,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("limited")] Limited,
}

/// <summary>Wire names for enums where they appear in query strings and CLI arguments.</summary>
public static class Wire
{
    public static string ToWire(this TaskState s) => s switch
    {
        TaskState.Backlog => "backlog",
        TaskState.InProgress => "in_progress",
        TaskState.Blocked => "blocked",
        TaskState.Validating => "validating",
        TaskState.Validated => "validated",
        TaskState.Done => "done",
        TaskState.Cancelled => "cancelled",
        _ => s.ToString(),
    };

    public static readonly TaskState[] AllTaskStates =
    [
        TaskState.Backlog, TaskState.InProgress, TaskState.Blocked, TaskState.Validating,
        TaskState.Validated, TaskState.Done, TaskState.Cancelled,
    ];

    public static bool TryParseTaskState(string text, out TaskState state)
    {
        foreach (var candidate in AllTaskStates)
        {
            if (string.Equals(candidate.ToWire(), text.Replace('-', '_'), StringComparison.OrdinalIgnoreCase))
            {
                state = candidate;
                return true;
            }
        }
        state = default;
        return false;
    }

    public static string ToWire(this Verdict v) => v switch { Verdict.Yes => "yes", Verdict.No => "no", _ => "pending" };

    public static string ToWire(this LandMode m) => m == LandMode.Pr ? "pr" : "merge";

    public static bool TryParseLandMode(string text, out LandMode mode)
    {
        mode = string.Equals(text, "pr", StringComparison.OrdinalIgnoreCase) ? LandMode.Pr : LandMode.Merge;
        return string.Equals(text, "pr", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "merge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"T-12", "t-12" or "12" → 12.</summary>
    public static bool TryParseTaskId(string text, out int id)
    {
        var digits = text.StartsWith("T-", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return int.TryParse(digits, out id) && id > 0;
    }

    public static string TaskId(int id) => "T-" + id;
}
