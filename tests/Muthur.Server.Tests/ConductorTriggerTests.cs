using Microsoft.Extensions.Time.Testing;
using Muthur.Core.Entities;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The conductor runs on ledger events, and sweeps on the interval only for what no event announces. What
/// matters is which events wake it: one that does not would put a whole interval back into every phase boundary.
/// </summary>
public sealed class ConductorTriggerTests
{
    private static LedgerEvent Event(string type) => new() { Type = type, Actor = "test", PayloadJson = "{}", At = DateTimeOffset.UnixEpoch };

    [Theory]
    [InlineData("task.implemented")]
    [InlineData("task.validated")]
    [InlineData("task.validation_failed")]
    [InlineData("integration.passed")]
    [InlineData("task.landed")]
    [InlineData("request.answered")]
    [InlineData("conductor.child_exited")]
    [InlineData("task.added")]
    public async Task A_phase_boundary_wakes_the_conductor(string type)
    {
        var feed = new EventFeed();
        using var trigger = new ConductorTrigger(feed);
        var clock = new FakeTimeProvider();

        feed.Publish([Event(type)]);

        Assert.True(trigger.Pending);
        Assert.True(await trigger.WaitAsync(TimeSpan.FromMinutes(1), clock, CancellationToken.None));
        Assert.False(trigger.Pending);
    }

    [Fact]
    public void An_event_that_changes_nothing_staffable_does_not()
    {
        var feed = new EventFeed();
        using var trigger = new ConductorTrigger(feed);

        feed.Publish([Event("agent.heartbeat"), Event("message.sent")]);

        Assert.False(trigger.Pending);
    }

    [Fact]
    public void A_burst_is_one_wake_not_several()
    {
        var feed = new EventFeed();
        using var trigger = new ConductorTrigger(feed);

        feed.Publish([Event("task.implemented"), Event("conductor.orchestrator_exited"), Event("conductor.child_exited")]);
        feed.Publish([Event("task.validated")]);

        Assert.True(trigger.Pending);
    }

    [Fact]
    public async Task With_nothing_to_wake_it_the_interval_sweep_still_runs()
    {
        var feed = new EventFeed();
        using var trigger = new ConductorTrigger(feed);
        var clock = new FakeTimeProvider();

        var waiting = trigger.WaitAsync(TimeSpan.FromSeconds(15), clock, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(15));

        Assert.False(await waiting);   // the sweep, not an event
    }

    [Fact]
    public async Task Stopping_the_hub_stops_the_wait()
    {
        var feed = new EventFeed();
        using var trigger = new ConductorTrigger(feed);
        using var stopping = new CancellationTokenSource();

        var waiting = trigger.WaitAsync(TimeSpan.FromMinutes(1), new FakeTimeProvider(), stopping.Token);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
