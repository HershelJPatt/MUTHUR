using System.Net;

namespace Muthur.Server.Tests;

public sealed class DashboardTaskDetailTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task The_task_page_shows_the_task_and_its_history()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("inspector");
        var task = await agent.AddTaskAsync("Inspect me", priority: 2);
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();

        var html = await _hub.CreateClient().GetStringAsync("/tasks/T-1");

        Assert.Contains("Inspect me", html);
        Assert.Contains("in_progress", html);
        Assert.Contains("priority 2", html);
        Assert.Contains("inspector", html);
        Assert.Contains("task.added", html);
        Assert.Contains("task.claimed", html);
    }

    [Fact]
    public async Task A_task_that_does_not_exist_renders_the_service_message()
    {
        var response = await _hub.CreateClient().GetAsync("/tasks/T-999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("does not exist", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_id_that_is_not_a_task_id_renders_the_service_message()
    {
        var response = await _hub.CreateClient().GetAsync("/tasks/banana");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not a task id", await response.Content.ReadAsStringAsync());
    }
}
