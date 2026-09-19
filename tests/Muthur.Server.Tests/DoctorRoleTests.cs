using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>The role checks: is every standing responsibility held by something that is still answering?</summary>
public sealed class DoctorRoleTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private async Task<CheckDto> RoleAsync(string key) =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe=false", MuthurJsonContext.Default.DoctorDto))!
        .Checks.Single(c => c.Category == "role" && c.Subject == key);

    private async Task DefineAsync(string key) =>
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(key))).EnsureSuccessStatusCode();

    [Fact]
    public async Task A_role_a_live_agent_holds_is_ok()
    {
        await DefineAsync("comms-oncall");
        var agent = await _hub.RegisterAgentAsync("talker");
        (await agent.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();

        var role = await RoleAsync("comms-oncall");
        Assert.Equal(CheckStatus.Ok, role.Status);
        Assert.Equal("Held by 'talker'.", role.Detail);
        Assert.Null(role.LastSuccess);
    }

    [Fact]
    public async Task A_holder_that_has_gone_quiet_is_a_warning_while_its_hold_still_stands()
    {
        await DefineAsync("comms-oncall");
        var agent = await _hub.RegisterAgentAsync("talker");
        (await agent.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();

        // Staleness is the clock moving, not the test waiting: past AgentStaleSeconds, well inside the 30-minute lease.
        _hub.Clock.Advance(TimeSpan.FromMinutes(4));

        var role = await RoleAsync("comms-oncall");
        Assert.Equal(CheckStatus.Warn, role.Status);
        Assert.Contains("Held by 'talker', last seen 4m ago; the hold lapses at", role.Detail, StringComparison.Ordinal);
        Assert.EndsWith("unless it speaks.", role.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_that_lapsed_while_a_task_waits_on_it_names_the_count()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path, validators: ["win-validator"]);
        await DefineAsync("win-validator");

        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var validator = await _hub.RegisterAgentAsync("win");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        Assert.Equal(CheckStatus.Ok, (await RoleAsync("win-validator")).Status);

        // Silence loses the hold, and the task it was going to look at is still waiting.
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));

        var role = await RoleAsync("win-validator");
        Assert.Equal(CheckStatus.Warn, role.Status);
        Assert.Equal("Unheld, and 1 task(s) are waiting on it.", role.Detail);
    }
}
