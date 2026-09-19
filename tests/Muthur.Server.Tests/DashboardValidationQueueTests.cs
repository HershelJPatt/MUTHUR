using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>The Board's validation queue — how deep the bottleneck is, rendered by the real dashboard.</summary>
public sealed class DashboardValidationQueueTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private async Task<string> ValidatingTaskAsync(HttpClient owner, string title, string file)
    {
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        var branch = $"task/{task.Id}-work";
        _repo.BranchWithFile(branch, file, $"{file}\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return task.Id;
    }

    [Fact]
    public async Task The_board_shows_how_many_tasks_wait_on_each_validator_role_and_how_long_the_oldest_has()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "Brief", Holders: 2))).EnsureSuccessStatusCode();
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("web-validator", "nothing waits on this"))).EnsureSuccessStatusCode();
        var owner = await _hub.RegisterAgentAsync("owner");

        await ValidatingTaskAsync(owner, "First", "first.txt");
        _hub.Clock.Advance(TimeSpan.FromMinutes(90));
        await ValidatingTaskAsync(owner, "Second", "second.txt");
        // The hold is a lease, so it is taken on this side of the wait: one live holder of the two the role allows.
        var validator = await _hub.RegisterAgentAsync("one");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        var page = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("Validation queue", page);
        Assert.Contains("2 waiting", page);              // the panel's head, and the win-validator row
        Assert.Contains("win-validator", page);
        Assert.Contains("1/2", page);                    // one of two holders
        Assert.Contains("oldest 1h", page);              // …and the header stat carries the same wait
        Assert.Contains("web-validator", page);
        Assert.Contains("0/1", page);
        Assert.Contains("clear", page);
    }

    [Fact]
    public async Task With_no_validator_roles_the_panel_says_so_rather_than_showing_an_empty_box()
    {
        var page = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("Validation queue", page);
        Assert.Contains("No validator roles defined.", page);
        Assert.DoesNotContain("oldest", page);
    }
}
