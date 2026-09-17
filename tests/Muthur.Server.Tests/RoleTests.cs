using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class RoleTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private async Task<IReadOnlyList<RoleDto>> RolesAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync(Routes.Roles, MuthurJsonContext.Default.IReadOnlyListRoleDto))!;

    [Fact]
    public async Task Only_the_founder_writes_briefs_and_agents_read_them()
    {
        var agent = await _hub.RegisterAgentAsync("reader");
        var denied = await agent.PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("comms-oncall", "do whatever"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "# Windows validator\nBuild, launch, drive."))).EnsureSuccessStatusCode();

        var brief = await agent.GetFromJsonAsync(Routes.RoleAction("win-validator", "brief"), MuthurJsonContext.Default.RoleBriefDto);
        Assert.Contains("Build, launch, drive.", brief!.Brief);
        Assert.True(brief.IsValidator, "keys ending in -validator default to validator roles");
    }

    [Fact]
    public async Task A_role_has_one_holder_and_the_hold_is_a_lease()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("comms-oncall"))).EnsureSuccessStatusCode();
        var first = await _hub.RegisterAgentAsync("first");
        var second = await _hub.RegisterAgentAsync("second");

        (await first.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();
        var contested = await second.PostAsync(Routes.RoleAction("comms-oncall", "take"), null);
        Assert.Equal(HttpStatusCode.Conflict, contested.StatusCode);
        Assert.Equal("role_held", (await contested.ReadErrorAsync()).Code);

        // Signs of life keep the hold…
        for (var i = 0; i < 3; i++)
        {
            _hub.Clock.Advance(TimeSpan.FromMinutes(20));
            (await first.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest())).EnsureSuccessStatusCode();
        }
        Assert.Equal("first", (await RolesAsync()).Single().Holder);

        // …silence loses it.
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null((await RolesAsync()).Single().Holder);
        (await second.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();
        Assert.Equal("second", (await RolesAsync()).Single().Holder);

        var agents = await _hub.CreateClient().GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.IReadOnlyListAgentDto);
        Assert.Equal(["comms-oncall"], agents!.Single(a => a.Name == "second").Roles);
        Assert.Empty(agents!.Single(a => a.Name == "first").Roles);
    }

    [Fact]
    public async Task Sweep_frees_lapsed_holds_and_records_it()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("observability-oncall"))).EnsureSuccessStatusCode();
        var agent = await _hub.RegisterAgentAsync("sleepy");
        (await agent.PostAsync(Routes.RoleAction("observability-oncall", "take"), null)).EnsureSuccessStatusCode();
        var roles = _hub.Services.GetRequiredService<RoleService>();

        Assert.Equal(0, await roles.SweepExpiredHoldsAsync());
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await roles.SweepExpiredHoldsAsync());

        var events = await _hub.CreateClient().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Contains(events!, e => e.Type == "role.expired" && e.Payload.GetProperty("agent").GetString() == "sleepy");
    }

    [Fact]
    public async Task Only_the_holder_or_founder_releases()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("release-oncall"))).EnsureSuccessStatusCode();
        var holder = await _hub.RegisterAgentAsync("holder");
        var other = await _hub.RegisterAgentAsync("other");
        (await holder.PostAsync(Routes.RoleAction("release-oncall", "take"), null)).EnsureSuccessStatusCode();

        var denied = await other.PostAsync(Routes.RoleAction("release-oncall", "release"), null);
        Assert.Equal("not_holder", (await denied.ReadErrorAsync()).Code);

        var released = await holder.PostAsync(Routes.RoleAction("release-oncall", "release"), null);
        Assert.Null((await released.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RoleDto))!.Holder); // an acknowledgement, like every other command
        Assert.Null((await RolesAsync()).Single().Holder);
    }
}
