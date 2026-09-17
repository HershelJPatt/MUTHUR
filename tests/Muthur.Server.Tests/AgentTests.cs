using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

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
            (await _hub.CreateClient().GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.IReadOnlyListAgentDto))!.Single().Status;

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
}
