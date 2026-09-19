using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// The rail's roles panel — who holds each post, or that nobody does. The validation queue answers how deep
/// the queue is; this answers who holds what, including the on-call roles no other panel names.
/// </summary>
public sealed class DashboardRolesTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private async Task DefineAsync(string key, int? holders = null) =>
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(key, "Brief", Holders: holders)))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task A_held_role_names_its_holder()
    {
        await DefineAsync("comms-oncall");
        var agent = await _hub.RegisterAgentAsync("herald");
        (await agent.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();

        var html = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("Roles", html);
        Assert.Contains("comms-oncall", html);
        Assert.Contains("<span class=\"pill pill-role\">herald</span>", html);
        Assert.Contains("1 of 1 held", html);
    }

    [Fact]
    public async Task A_role_nobody_holds_reads_unheld()
    {
        await DefineAsync("knowledge-oncall");

        var html = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("knowledge-oncall", html);
        // The plain pill, not an amber one: a free role is a normal state of the organization, not a fault.
        Assert.Contains("<span class=\"pill\">unheld</span>", html);
        Assert.Contains("0 of 1 held", html);
    }

    [Fact]
    public async Task A_role_whose_lease_lapsed_reads_unheld_without_anything_being_written()
    {
        await DefineAsync("release-oncall");
        var agent = await _hub.RegisterAgentAsync("shipper");
        (await agent.PostAsync(Routes.RoleAction("release-oncall", "take"), null)).EnsureSuccessStatusCode();
        Assert.Contains("shipper", await _hub.CreateClient().GetStringAsync("/"));

        // Nothing releases the role and nothing sweeps it: the lease simply runs out.
        _hub.Clock.Advance(TimeSpan.FromHours(2));

        var html = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("release-oncall", html);
        Assert.Contains("<span class=\"pill\">unheld</span>", html);
        Assert.DoesNotContain("<span class=\"pill pill-role\">shipper</span>", html);
        Assert.Contains("0 of 1 held", html);
    }

    [Fact]
    public async Task A_validator_role_with_room_for_several_shows_how_many_of_them_hold_it()
    {
        await DefineAsync("win-validator", holders: 2);
        var agent = await _hub.RegisterAgentAsync("tester");
        (await agent.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        var html = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("win-validator", html);
        Assert.Contains("<span class=\"tag\">validator</span>", html);
        Assert.Contains("1 of 2", html);
    }

    [Fact]
    public async Task A_hub_with_no_roles_says_so_rather_than_showing_an_empty_box()
    {
        var html = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("Roles", html);
        Assert.Contains("no roles defined", html);
        Assert.Contains("0 of 0 held", html);
    }

    [Fact]
    public async Task Needs_you_carries_the_same_rail()
    {
        await DefineAsync("observability-oncall");
        var agent = await _hub.RegisterAgentAsync("watcher");
        (await agent.PostAsync(Routes.RoleAction("observability-oncall", "take"), null)).EnsureSuccessStatusCode();

        var html = await _hub.CreateClient().GetStringAsync("/needs-you");

        Assert.Contains("Roles", html);
        Assert.Contains("observability-oncall", html);
        Assert.Contains("<span class=\"pill pill-role\">watcher</span>", html);
    }
}
