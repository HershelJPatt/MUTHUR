using Muthur.Contracts;
using Muthur.Core;

namespace Muthur.Core.Tests;

public sealed class TaskStateTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minute <paramref name="minutes"/> of the day the fixtures live in.</summary>
    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    [Theory]
    [InlineData("task.claimed", "{\"tasks\":[\"T-67\"]}", TaskState.Backlog)]
    [InlineData("task.claimed", "{\"tasks\":[]}", TaskState.InProgress)]
    [InlineData("task.blocked", "{\"tasks\":[\"T-67\"]}", TaskState.Blocked)]
    [InlineData("task.claimed", "invalid", TaskState.InProgress)]
    [InlineData("task.claimed", "null", TaskState.InProgress)]
    public void Historical_dependency_events_only_release_active_work(string initial, string payload, TaskState expected)
    {
        var intervals = TaskStateTimeline.ReplayWithPayload([(initial, At(0), null), ("task.dependencies_set", At(10), payload)]);
        Assert.Equal(expected, intervals[^1].State);
        var totals = TaskStateTimeline.Within(intervals, At(0), At(60));
        Assert.Equal(TimeSpan.FromMinutes(expected == TaskState.Backlog ? 50 : 60), totals[expected]);
    }

    [Fact]
    public void Explicit_dependency_release_and_legacy_replay_have_identical_times()
    {
        (string, DateTimeOffset, string?)[] old = [("task.claimed", At(0), null), ("task.dependencies_set", At(10), "{\"tasks\":[\"T-2\"]}")];
        Assert.Equal(TaskStateTimeline.ReplayWithPayload(old),
            TaskStateTimeline.ReplayWithPayload([old[0], ("task.released", At(10), null), old[1]]));
    }

    [Theory]
    [InlineData("task.added", TaskState.Backlog)]
    [InlineData("task.claimed", TaskState.InProgress)]
    [InlineData("task.released", TaskState.Backlog)]
    [InlineData("task.claim_expired", TaskState.Backlog)]
    [InlineData("task.reopened", TaskState.Backlog)]
    [InlineData("task.blocked", TaskState.Blocked)]
    [InlineData("task.unblocked", TaskState.InProgress)]
    [InlineData("task.implemented", TaskState.Validating)]
    [InlineData("task.validation_failed", TaskState.InProgress)]
    [InlineData("task.validation_blocked", TaskState.InProgress)]
    [InlineData("task.validated", TaskState.Validated)]
    [InlineData("task.land_failed", TaskState.InProgress)]
    [InlineData("task.landed", TaskState.Done)]
    [InlineData("task.pr_opened", TaskState.Done)]
    [InlineData("task.cancelled", TaskState.Cancelled)]
    [InlineData("task.spec_set", null)]
    [InlineData("task.land_refused", null)]
    [InlineData("task.priority_changed", null)]
    [InlineData("conductor.staffing", null)]
    [InlineData("", null)]
    public void Enters_maps_the_events_that_move_a_task(string eventType, TaskState? expected) =>
        Assert.Equal(expected, TaskStateTimeline.Enters(eventType));

    [Fact]
    public void A_full_lifecycle_replays_to_its_intervals()
    {
        var intervals = TaskStateTimeline.Replay(Lifecycle());

        TaskStateTimeline.Interval[] expected =
        [
            new(TaskState.Backlog, At(0), At(10)),
            new(TaskState.InProgress, At(10), At(20)),
            new(TaskState.Blocked, At(20), At(30)),
            new(TaskState.InProgress, At(30), At(40)),
            new(TaskState.Validating, At(40), At(50)),
            new(TaskState.InProgress, At(50), At(60)),
            new(TaskState.Validating, At(60), At(70)),
            new(TaskState.Validated, At(70), At(80)),
            new(TaskState.Done, At(80), null),
        ];
        Assert.Equal(expected, intervals);

        // The two stays a founder would count: ten minutes waiting for the failed verdict, ten for the passing one.
        var spent = TaskStateTimeline.Within(intervals, At(0), At(80));
        Assert.Equal(TimeSpan.FromMinutes(20), spent[TaskState.Validating]);
        Assert.Equal(TimeSpan.FromMinutes(30), spent[TaskState.InProgress]);
    }

    [Fact]
    public void Events_that_enter_no_state_are_skipped_without_closing_anything()
    {
        var noise = Lifecycle().ToList();
        noise.Insert(2, ("task.spec_set", At(15)));
        noise.Insert(6, ("task.priority_changed", At(35)));
        noise.Add(("task.land_refused", At(90)));

        Assert.Equal(TaskStateTimeline.Replay(Lifecycle()), TaskStateTimeline.Replay(noise));
    }

    [Fact]
    public void Repeating_a_state_extends_the_stay_rather_than_splitting_it()
    {
        var intervals = TaskStateTimeline.Replay(
        [
            ("task.added", At(0)),
            ("task.claimed", At(10)),
            ("task.released", At(20)),
            ("task.released", At(30)),
        ]);

        TaskStateTimeline.Interval[] expected =
        [
            new(TaskState.Backlog, At(0), At(10)),
            new(TaskState.InProgress, At(10), At(20)),
            new(TaskState.Backlog, At(20), null),
        ];
        Assert.Equal(expected, intervals);
    }

    [Fact]
    public void Nothing_replays_to_no_intervals()
    {
        Assert.Empty(TaskStateTimeline.Replay([]));
        Assert.Empty(TaskStateTimeline.Replay([("task.spec_set", At(0)), ("task.attended", At(1))]));
    }

    [Fact]
    public void A_window_that_opens_mid_life_replays_from_the_event_it_has()
    {
        var intervals = TaskStateTimeline.Replay([("task.validation_failed", At(10)), ("task.implemented", At(20))]);

        TaskStateTimeline.Interval[] expected =
        [
            new(TaskState.InProgress, At(10), At(20)),
            new(TaskState.Validating, At(20), null),
        ];
        Assert.Equal(expected, intervals);
    }

    [Fact]
    public void The_last_interval_is_open_and_Within_closes_it_at_to()
    {
        var intervals = TaskStateTimeline.Replay([("task.added", At(0)), ("task.claimed", At(10))]);

        Assert.Null(intervals[^1].Until);
        var spent = TaskStateTimeline.Within(intervals, At(0), At(40));
        Assert.Equal(TimeSpan.FromMinutes(30), spent[TaskState.InProgress]);
        Assert.Equal(TimeSpan.FromMinutes(10), spent[TaskState.Backlog]);
    }

    [Fact]
    public void Within_clips_both_ends_of_the_window()
    {
        var intervals = TaskStateTimeline.Replay(
        [
            ("task.added", At(0)),
            ("task.claimed", At(20)),
            ("task.implemented", At(60)),
        ]);

        var spent = TaskStateTimeline.Within(intervals, At(10), At(40));
        Assert.Equal(TimeSpan.FromMinutes(10), spent[TaskState.Backlog]);     // clipped at the window's start
        Assert.Equal(TimeSpan.FromMinutes(20), spent[TaskState.InProgress]);  // clipped at its end
        Assert.False(spent.ContainsKey(TaskState.Validating));                // began after the window closed
    }

    [Fact]
    public void A_task_that_never_waited_has_a_zero_length_validating_stay()
    {
        var intervals = TaskStateTimeline.Replay(
        [
            ("task.claimed", At(0)),
            ("task.implemented", At(10)),
            ("task.validated", At(10)),
            ("task.landed", At(20)),
        ]);

        Assert.Equal(new TaskStateTimeline.Interval(TaskState.Validating, At(10), At(10)), intervals[1]);
        var spent = TaskStateTimeline.Within(intervals, At(0), At(20));
        Assert.True(spent.ContainsKey(TaskState.Validating));
        Assert.Equal(TimeSpan.Zero, spent[TaskState.Validating]);
    }

    /// <summary>added → claimed → blocked → unblocked → implemented → failed → implemented → validated → landed.</summary>
    private static List<(string Type, DateTimeOffset At)> Lifecycle() =>
    [
        ("task.added", At(0)),
        ("task.claimed", At(10)),
        ("task.blocked", At(20)),
        ("task.unblocked", At(30)),
        ("task.implemented", At(40)),
        ("task.validation_failed", At(50)),
        ("task.implemented", At(60)),
        ("task.validated", At(70)),
        ("task.landed", At(80)),
    ];
}
