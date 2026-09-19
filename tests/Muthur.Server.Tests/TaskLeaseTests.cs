using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Data;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class TaskLeaseTests : IDisposable
{
    // The_ledger_cannot_be_rewritten asks the database to do what the append-only triggers refuse, and EF
    // logs each refused command at Error. Those two lines are the assertion, not a finding.
    private readonly HubFactory _hub = new() { ExpectsLoggedErrors = true };

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task Adding_a_task_requires_an_identity_and_a_project()
    {
        var anonymous = await _hub.CreateClient().PostAsJsonAsync(Routes.Tasks, new AddTaskRequest("nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var agent = await _hub.RegisterAgentAsync("alpha");
        var noProject = await agent.PostAsJsonAsync(Routes.Tasks, new AddTaskRequest("still nope"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noProject.StatusCode);
        Assert.Equal("no_projects", (await noProject.ReadErrorAsync()).Code);

        await _hub.AddProjectAsync();
        var task = await agent.AddTaskAsync("first");
        Assert.Equal("T-1", task.Id);
        Assert.Equal(TaskState.Backlog, task.State);
        Assert.Equal("demo", task.Project);
    }

    [Fact]
    public async Task Racing_claims_have_exactly_one_winner()
    {
        await _hub.AddProjectAsync();
        var agents = new List<HttpClient>();
        for (var i = 0; i < 8; i++) agents.Add(await _hub.RegisterAgentAsync($"racer-{i}"));
        var task = await agents[0].AddTaskAsync("contested");

        var results = await Task.WhenAll(agents.Select(a => a.ClaimAsync(task.Id)));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var loser = results.First(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("not_claimable", (await loser.ReadErrorAsync()).Code);

        var detail = await agents[0].GetTaskAsync(task.Id);
        Assert.Single(detail.Events, e => e.Type == "task.claimed");
    }

    [Fact]
    public async Task Lapsed_claim_can_be_taken_over_but_a_live_one_cannot()
    {
        await _hub.AddProjectAsync();
        var first = await _hub.RegisterAgentAsync("first");
        var second = await _hub.RegisterAgentAsync("second");
        var task = await first.AddTaskAsync("work");

        await (await first.ClaimAsync(task.Id, leaseMinutes: 10)).ReadTaskAsync();
        _hub.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(HttpStatusCode.Conflict, (await second.ClaimAsync(task.Id)).StatusCode);

        _hub.Clock.Advance(TimeSpan.FromMinutes(2));
        var taken = await (await second.ClaimAsync(task.Id)).ReadTaskAsync();
        Assert.Equal("second", taken.Owner);

        var claimed = (await second.GetTaskAsync(task.Id)).Events.Last(e => e.Type == "task.claimed");
        Assert.Equal("first", claimed.Payload.GetProperty("tookOverFrom").GetString());
    }

    [Fact]
    public async Task Heartbeat_renews_the_claim()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("steady");
        var other = await _hub.RegisterAgentAsync("other");
        var task = await agent.AddTaskAsync("long job");
        await agent.ClaimAsync(task.Id);

        for (var i = 0; i < 4; i++)
        {
            _hub.Clock.Advance(TimeSpan.FromMinutes(20));
            (await agent.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest("still on it"))).EnsureSuccessStatusCode();
        }

        Assert.Equal(HttpStatusCode.Conflict, (await other.ClaimAsync(task.Id)).StatusCode);
    }

    [Fact]
    public async Task Sweep_returns_lapsed_tasks_to_the_backlog()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("gone");
        var task = await agent.AddTaskAsync("abandoned");
        await agent.ClaimAsync(task.Id);

        var tasks = _hub.Services.GetRequiredService<TaskService>();
        Assert.Equal(0, await tasks.SweepExpiredClaimsAsync());

        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await tasks.SweepExpiredClaimsAsync());

        var detail = await _hub.CreateClient().GetTaskAsync(task.Id);
        Assert.Equal(TaskState.Backlog, detail.Task.State);
        Assert.Null(detail.Task.Owner);
        Assert.Contains(detail.Events, e => e.Type == "task.claim_expired" && e.Actor == "muthur");
    }

    [Fact]
    public async Task Only_the_owner_or_founder_may_release()
    {
        await _hub.AddProjectAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var intruder = await _hub.RegisterAgentAsync("intruder");
        var task = await owner.AddTaskAsync("mine");
        await owner.ClaimAsync(task.Id);

        var denied = await intruder.PostActionAsync(task.Id, "release", new ReleaseTaskRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
        Assert.Equal("not_owner", (await denied.ReadErrorAsync()).Code);

        var released = await (await _hub.Founder().PostActionAsync(task.Id, "release", new ReleaseTaskRequest("reassigning"))).ReadTaskAsync();
        Assert.Equal(TaskState.Backlog, released.State);
    }

    [Fact]
    public async Task The_ledger_cannot_be_rewritten()
    {
        await _hub.AddProjectAsync();
        await using var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync();
        Assert.True(await db.Events.AnyAsync());

        var update = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE events SET actor = 'mallory'"));
        Assert.Contains("append-only", update.Message);
        var delete = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM events"));
        Assert.Contains("append-only", delete.Message);
    }
}
