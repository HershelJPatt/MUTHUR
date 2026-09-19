using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class AgentTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task A_name_belongs_to_whoever_registered_it()
    {
        var original = await _hub.RegisterAgentAsync("top-left");

        var squatter = await _hub.CreateClient().PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest("top-left", "codex", "gpt"));
        Assert.Equal(HttpStatusCode.Conflict, squatter.StatusCode);
        Assert.Equal("agent_exists", (await squatter.ReadErrorAsync()).Code);

        // The agent itself may re-register (new session, new harness); the old token stops working.
        var again = await original.PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest("top-left", "codex", "gpt-5"));
        again.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await original.GetAsync(Routes.AgentMe)).StatusCode);

        var rotated = (await again.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse))!;
        var me = await _hub.CreateClient(rotated.Token).GetFromJsonAsync(Routes.AgentMe, MuthurJsonContext.Default.AgentDto);
        Assert.Equal("codex", me!.Harness);
    }

    [Fact]
    public async Task Status_follows_heartbeat_and_rate_limit()
    {
        var agent = await _hub.RegisterAgentAsync("pulse");
        async Task<AgentStatus> StatusAsync() =>
            (await _hub.CreateClient().GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.AgentRosterDto))!.Agents.Single().Status;

        Assert.Equal(AgentStatus.Live, await StatusAsync());

        _hub.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AgentStatus.Stale, await StatusAsync());

        var until = _hub.Clock.GetUtcNow().AddHours(2);
        (await agent.PostAsJsonAsync(Routes.AgentLimited, new LimitedRequest(until))).EnsureSuccessStatusCode();
        Assert.Equal(AgentStatus.Limited, await StatusAsync());

        _hub.Clock.Advance(TimeSpan.FromHours(3));
        (await agent.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest())).EnsureSuccessStatusCode();
        Assert.Equal(AgentStatus.Live, await StatusAsync());
    }

    [Fact]
    public async Task Events_record_which_model_acted()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("scribe", harness: "codex", model: "gpt-5");
        var task = await agent.AddTaskAsync("attribution");

        var added = (await agent.GetTaskAsync(task.Id)).Events.Single(e => e.Type == "task.added");
        Assert.Equal("scribe", added.Actor);
        Assert.Equal("codex/gpt-5", added.ActorModel);
    }

    [Fact]
    public async Task Only_the_founder_may_define_projects()
    {
        var agent = await _hub.RegisterAgentAsync("ambitious");
        var response = await agent.PostAsJsonAsync(Routes.Projects, new AddProjectRequest("rogue", "C:/somewhere"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_agent_a_person_registered_is_standing_and_hides_nobody()
    {
        await _hub.RegisterAgentAsync("top-left");

        var roster = await RosterAsync();

        var registered = Assert.Single(roster.Agents);
        Assert.Equal("top-left", registered.Name);
        Assert.False(registered.ConductorStaffed);
        Assert.Equal(0, roster.ConductorHidden);
    }

    [Fact]
    public async Task A_session_the_conductor_staffed_is_counted_rather_than_listed()
    {
        var identity = await StaffAsync("win-validator");
        Assert.Equal("conductor-win-validator-t-1", identity.Name);

        // The whole point of the count: a roster that drops rows without saying so reads like a quiet organization.
        var roster = await RosterAsync();
        Assert.DoesNotContain(roster.Agents, a => a.Name == identity.Name);
        Assert.Equal(1, roster.ConductorHidden);

        var all = await RosterAsync(all: true);
        Assert.True(Assert.Single(all.Agents, a => a.Name == identity.Name).ConductorStaffed);
    }

    [Fact]
    public async Task Re_registering_a_staffed_session_through_the_public_endpoint_does_not_make_it_standing()
    {
        // The orchestrate procedure tells a session whose token is gone to run `muthur agent register` again.
        // Nothing about the marker crosses the wire, so the only honest answer is to leave it alone.
        var identity = await StaffAsync("win-validator");

        var again = await _hub.CreateClient(identity.Token).PostAsJsonAsync(Routes.AgentRegister,
            new RegisterAgentRequest(identity.Name, "codex", "gpt-5"));
        again.EnsureSuccessStatusCode();

        var reregistered = (await again.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse))!.Agent;
        Assert.True(reregistered.ConductorStaffed);

        var roster = await RosterAsync();
        Assert.DoesNotContain(roster.Agents, a => a.Name == identity.Name);
        Assert.Equal(1, roster.ConductorHidden);
    }

    [Fact]
    public async Task All_returns_every_row_and_says_it_left_nothing_out()
    {
        await _hub.RegisterAgentAsync("top-left");
        var staffed = await StaffAsync("win-validator");

        var all = await RosterAsync(all: true);

        Assert.Equal([staffed.Name, "top-left"], all.Agents.Select(a => a.Name));
        Assert.Equal([true, false], all.Agents.Select(a => a.ConductorStaffed));
        Assert.Equal(0, all.ConductorHidden);
    }

    private async Task<AgentRosterDto> RosterAsync(bool all = false) =>
        (await _hub.CreateClient().GetFromJsonAsync(Routes.Agents + (all ? "?all=true" : ""), MuthurJsonContext.Default.AgentRosterDto))!;

    /// <summary>Staffs one validator session the way the conductor does — the real launcher over the hub's own services.</summary>
    private Task<Muthur.Launch.AgentIdentity> StaffAsync(string role) =>
        ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services).IdentityFor(
            new ConductorAssignment(1, "T-1", "Build T-1", "muthur", role, AvoidHarness: null),
            new Muthur.Launch.HarnessCandidate("codex", "opus", "chatgpt-subscription"),
            default);
}
