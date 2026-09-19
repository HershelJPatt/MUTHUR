using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class DashboardAgentsTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    /// <summary>The rendered `agent-task` lines, in page order — one per agent that is working on something.</summary>
    private static List<string> TaskLines(string html)
    {
        var lines = new List<string>();
        const string marker = "class=\"agent-task\">";
        for (var at = html.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = html.IndexOf(marker, at + 1, StringComparison.Ordinal))
        {
            var start = at + marker.Length;
            lines.Add(html[start..html.IndexOf("</div>", start, StringComparison.Ordinal)]);
        }
        return lines;
    }

    [Fact]
    public async Task Agents_panel_lists_live_agents_before_stale_ones()
    {
        await _hub.RegisterAgentAsync("zeta");
        _hub.Clock.Advance(TimeSpan.FromMinutes(10));
        await _hub.RegisterAgentAsync("alpha", harness: "codex", model: "gpt-5", tier: "implementer");

        var html = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("alpha", html);
        Assert.Contains("codex/gpt-5", html);
        Assert.Contains("implementer", html);
        Assert.Contains("1 live of 2", html);
        Assert.Contains("agent-stale", html);
        Assert.True(html.IndexOf("alpha", StringComparison.Ordinal) < html.IndexOf("zeta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agent_with_a_task_in_progress_shows_its_id_and_title()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var worker = await _hub.RegisterAgentAsync("worker");
        var task = await worker.AddTaskAsync("Wire the agents rail");
        (await worker.ClaimAsync(task.Id)).EnsureSuccessStatusCode();

        var html = await _hub.CreateClient().GetStringAsync("/");

        var line = Assert.Single(TaskLines(html));
        Assert.Contains(task.Id, line);
        Assert.Contains("Wire the agents rail", line);
    }

    [Fact]
    public async Task A_task_in_validation_is_held_but_not_worked_on_so_the_pill_counts_it_and_no_task_line_claims_it()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "Brief"))).EnsureSuccessStatusCode();
        var worker = await _hub.RegisterAgentAsync("worker");
        var task = await worker.AddTaskAsync("Handed to validation");
        (await worker.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await worker.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        var branch = $"task/{task.Id}-work";
        _repo.BranchWithFile(branch, "work.txt", "work\n");
        (await worker.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();

        var html = await _hub.CreateClient().GetStringAsync("/");

        // The agent still holds the task — but it handed the work away, so the rail must not say it is on it.
        Assert.Contains("1 task", html);
        Assert.Empty(TaskLines(html));
    }

    [Fact]
    public async Task An_agent_working_two_tasks_shows_the_higher_priority_one()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var worker = await _hub.RegisterAgentAsync("worker");
        var routine = await worker.AddTaskAsync("Tidy the changelog");
        var urgent = await worker.AddTaskAsync("Stop the bleeding", priority: 5);
        (await worker.ClaimAsync(routine.Id)).EnsureSuccessStatusCode();
        (await worker.ClaimAsync(urgent.Id)).EnsureSuccessStatusCode();

        var html = await _hub.CreateClient().GetStringAsync("/");

        var line = Assert.Single(TaskLines(html));
        Assert.Contains(urgent.Id, line);
        Assert.Contains("Stop the bleeding", line);
        Assert.DoesNotContain("Tidy the changelog", line);
    }
}
