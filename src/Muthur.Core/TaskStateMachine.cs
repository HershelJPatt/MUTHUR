using Muthur.Contracts;

namespace Muthur.Core;

/// <summary>
/// backlog → in_progress → validating → validated → done, with blocked as a side state of in_progress,
/// validation failure bouncing back to in_progress, and cancel/reopen at the edges.
/// </summary>
public static class TaskStateMachine
{
    private static readonly Dictionary<TaskState, TaskState[]> Allowed = new()
    {
        [TaskState.Backlog] = [TaskState.InProgress, TaskState.Cancelled],
        [TaskState.InProgress] = [TaskState.Backlog, TaskState.Blocked, TaskState.Validating, TaskState.Validated, TaskState.Cancelled],
        [TaskState.Blocked] = [TaskState.InProgress, TaskState.Backlog, TaskState.Cancelled],
        [TaskState.Validating] = [TaskState.Validated, TaskState.InProgress, TaskState.Cancelled],
        [TaskState.Validated] = [TaskState.Done, TaskState.InProgress, TaskState.Cancelled],
        [TaskState.Done] = [],
        [TaskState.Cancelled] = [TaskState.Backlog],
    };

    public static bool CanTransition(TaskState from, TaskState to) => Allowed[from].Contains(to);

    public static void EnsureCanTransition(string taskId, TaskState from, TaskState to)
    {
        if (!CanTransition(from, to))
            throw Fail.Rule("invalid_transition", $"{taskId} is '{from.ToWire()}' and cannot move to '{to.ToWire()}'.");
    }

    public static bool IsOpen(TaskState state) => state is not (TaskState.Done or TaskState.Cancelled);
}
