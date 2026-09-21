using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// A hold: "do not land this yet, and here is why", recorded and visible. Never a lock — land warns and
/// proceeds, which is the founder's T-16 answer applied to intentions. The override is an event of its own
/// because the question this task was filed to answer needs a number, and holding used to leave no trace.
/// </summary>
public sealed class TaskHoldTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new(integrationChecks: true);

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private static Task<HttpResponseMessage> HoldAsync(HttpClient client, string id, string? reason) =>
        client.PostActionAsync(id, "hold", new HoldRequest(reason));

    /// <summary>A validated task of the owner's, ready to land, over a real checkout.</summary>
    private async Task<(HttpClient Owner, string Id)> ReadyToLandAsync()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    [Fact]
    public async Task Anyone_may_hold_anyone_elses_task_and_the_hold_says_who_and_until_when()
    {
        // The difference from 'attended', and the whole point: the incident this was filed about was one
        // orchestrator holding another's work.
        var (owner, id) = await ReadyToLandAsync();
        var other = await _hub.RegisterAgentAsync("bottom-left");
        var placed = _hub.Clock.GetUtcNow();

        var held = await (await HoldAsync(other, id, "  it collides with T-23's migration  ")).ReadTaskAsync();

        Assert.Equal("it collides with T-23's migration", held.HoldReason);
        Assert.Equal("bottom-left", held.HoldBy);
        Assert.Equal(placed + TimeSpan.FromMinutes(120), held.HoldExpires);

        var recorded = Assert.Single((await owner.GetTaskAsync(id)).Events, e => e.Type == "task.held");
        Assert.Equal("bottom-left", recorded.Payload.GetProperty("by").GetString());
        Assert.Equal("it collides with T-23's migration", recorded.Payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Clearing_lifts_it_and_names_what_it_was()
    {
        var (owner, id) = await ReadyToLandAsync();
        var other = await _hub.RegisterAgentAsync("bottom-left");
        (await HoldAsync(other, id, "hold on, main is red")).EnsureSuccessStatusCode();

        var lifted = await (await HoldAsync(owner, id, null)).ReadTaskAsync();

        Assert.Null(lifted.HoldReason);
        Assert.Null(lifted.HoldBy);
        Assert.Null(lifted.HoldExpires);
        var cleared = Assert.Single((await owner.GetTaskAsync(id)).Events, e => e.Type == "task.hold_cleared");
        Assert.Equal("hold on, main is red", cleared.Payload.GetProperty("was").GetString());

        // Clearing a task nobody holds is not an error, and records nothing further.
        (await HoldAsync(owner, id, null)).EnsureSuccessStatusCode();
        Assert.Single((await owner.GetTaskAsync(id)).Events, e => e.Type == "task.hold_cleared");
    }

    [Fact]
    public async Task A_hold_does_not_stop_a_land_it_only_makes_the_override_countable()
    {
        var (owner, id) = await ReadyToLandAsync();
        var other = await _hub.RegisterAgentAsync("bottom-left");
        var placed = _hub.Clock.GetUtcNow();
        (await HoldAsync(other, id, "it collides with T-23's migration")).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromMinutes(20));

        await _hub.PassIntegrationAsync(_repo, id);
        var landed = await (await owner.PostAsync(Routes.TaskAction(id, "land"), null)).ReadTaskAsync();

        Assert.Equal(TaskState.Done, landed.State);   // information, never a gate

        var events = (await owner.GetTaskAsync(id)).Events;
        var overridden = Assert.Single(events, e => e.Type == "task.hold_overridden");
        Assert.Equal("bottom-left", overridden.Payload.GetProperty("by").GetString());
        Assert.Equal("owner", overridden.Payload.GetProperty("landedBy").GetString());
        Assert.Equal("it collides with T-23's migration", overridden.Payload.GetProperty("reason").GetString());
        Assert.Equal(placed, overridden.Payload.GetProperty("placedAt").GetDateTimeOffset());

        // And on the land itself, so a reader of that one row sees it too.
        var land = Assert.Single(events, e => e.Type == "task.landed");
        Assert.Equal("bottom-left", land.Payload.GetProperty("overrodeHold").GetProperty("by").GetString());

        // The hold is not cleared by the land: the next lander should see it has been landed over once.
        Assert.Equal("bottom-left", (await owner.GetTaskAsync(id)).Task.HoldBy);
    }

    [Fact]
    public async Task The_founder_and_the_holder_are_told_who_when_and_why_verbatim()
    {
        var (owner, id) = await ReadyToLandAsync();
        var other = await _hub.RegisterAgentAsync("bottom-left");
        (await HoldAsync(other, id, "it collides with T-23's migration")).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromMinutes(20));

        await _hub.PassIntegrationAsync(_repo, id);
        (await owner.PostAsync(Routes.TaskAction(id, "land"), null)).EnsureSuccessStatusCode();

        const string Said = "owner landed T-1, which bottom-left held 20m ago: \"it collides with T-23's migration\".";
        var toFounder = (await _hub.Founder().GetFromJsonAsync(Routes.Messages + "?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!;
        Assert.Single(toFounder, x => x.Body == Said);
        var toHolder = (await other.GetFromJsonAsync(Routes.Inbox, MuthurJsonContext.Default.InboxDto))!;
        Assert.Single(toHolder.Messages, x => x.Body == Said);
    }

    [Fact]
    public async Task An_expired_hold_is_not_a_hold()
    {
        // A note nobody cleared is text the next lander learns to skip, so it stops counting on its own.
        var (owner, id) = await ReadyToLandAsync();
        var other = await _hub.RegisterAgentAsync("bottom-left");
        (await HoldAsync(other, id, "hold on, main is red")).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromMinutes(121));

        Assert.DoesNotContain("<dt>Held</dt>", await _hub.CreateClient().GetStringAsync("/tasks/" + id));

        await _hub.PassIntegrationAsync(_repo, id);
        (await owner.PostAsync(Routes.TaskAction(id, "land"), null)).EnsureSuccessStatusCode();

        Assert.DoesNotContain((await owner.GetTaskAsync(id)).Events, e => e.Type == "task.hold_overridden");
        Assert.Empty((await _hub.Founder().GetFromJsonAsync(Routes.Messages + "?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!);
    }

    [Fact]
    public async Task The_board_says_who_holds_it_and_for_how_long()
    {
        var (_, id) = await ReadyToLandAsync();
        var other = await _hub.RegisterAgentAsync("bottom-left");
        (await HoldAsync(other, id, "it collides with T-23's migration")).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromMinutes(20));

        // The task page, not the board: the board's cards render inside a Virtualize, which emits nothing to
        // a fetch. The lander deciding whether to override a hold is reading this page anyway.
        var page = await _hub.CreateClient().GetStringAsync("/tasks/" + id);

        Assert.Contains("<dt>Held</dt>", page);
        Assert.Contains("by bottom-left, 20m ago", page);
        Assert.Contains("migration", page);
    }
}
