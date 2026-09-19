using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur task land` says out loud when it has just landed over somebody's hold. The hub records the
/// override and messages the founder and the holder, but the person who did it sees a JSON task and no
/// sentence — and a validator found exactly that. This is derived from the response the land already
/// returns, because a land deliberately does not clear the hold it overrode.
/// </summary>
public sealed class TaskHoldWarningTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ApiResult Landed(string? by, string? reason, DateTimeOffset? expires, TaskState state = TaskState.Done)
    {
        var task = new TaskDto("T-1", "muthur", "Build it", "", state, 0, "owner", null, null, "task/T-1-x",
            null, null, Now, Now, Now, [], null, reason, by, expires);
        return new ApiResult(200, JsonSerializer.Serialize(task, MuthurJsonContext.Default.TaskDto));
    }

    [Fact]
    public void A_land_over_a_live_hold_names_who_held_it_and_why()
    {
        var warning = TaskCommands.OverrodeHold(Landed("bottom-left", "it collides with T-23's migration", Now.AddHours(1)), Now);

        Assert.NotNull(warning);
        Assert.Contains("bottom-left held T-1", warning);
        Assert.Contains("it collides with T-23's migration", warning);   // verbatim, not summarised
        Assert.Contains("you landed it anyway", warning);
        Assert.Contains("muthur task hold T-1 --clear", warning);        // and how to stop saying it
    }

    [Fact]
    public void A_hold_that_has_expired_is_not_warned_about()
    {
        Assert.Null(TaskCommands.OverrodeHold(Landed("bottom-left", "main was red", Now.AddMinutes(-1)), Now));
    }

    [Fact]
    public void A_task_nobody_held_says_nothing()
    {
        Assert.Null(TaskCommands.OverrodeHold(Landed(null, null, null), Now));
    }

    /// <summary>A pull-request project leaves the task validated, not done: nothing was landed over yet.</summary>
    [Fact]
    public void A_response_that_is_not_a_completed_land_says_nothing()
    {
        Assert.Null(TaskCommands.OverrodeHold(Landed("bottom-left", "hold on", Now.AddHours(1), TaskState.Validated), Now));
    }

    [Fact]
    public void A_refusal_or_an_unreadable_body_says_nothing_rather_than_throwing()
    {
        Assert.Null(TaskCommands.OverrodeHold(new ApiResult(409, "{\"code\":\"land_conflict\"}"), Now));
        Assert.Null(TaskCommands.OverrodeHold(new ApiResult(200, "not json at all"), Now));
        Assert.Null(TaskCommands.OverrodeHold(new ApiResult(200, ""), Now));
    }
}
