using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The utility tier's guess at a task's work-kind is stored beside the task, recorded as an event, shown on the
/// task and in the orchestrator's opening prompt, and never mistaken for the spec's own line.
/// </summary>
public sealed class TaskWorkKindTests : IDisposable
{
    private readonly HubFactory _hub = new();
    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task A_suggestion_is_stored_recorded_and_shown_on_the_task()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("intake");
        var task = await agent.AddTaskAsync("Rename Foo to Bar everywhere");

        Assert.Null((await agent.GetTaskAsync(task.Id)).WorkKindSuggested);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _hub.CreateClient(null).PostActionAsync(task.Id, "work-kind", new WorkKindRequest("mechanical"))).StatusCode);
        var invalid = await agent.PostActionAsync(task.Id, "work-kind", new WorkKindRequest("cheap"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("invalid_work_kind", (await invalid.ReadErrorAsync()).Code);

        (await agent.PostActionAsync(task.Id, "work-kind", new WorkKindRequest("mechanical"))).EnsureSuccessStatusCode();
        var detail = await agent.GetTaskAsync(task.Id);
        Assert.Equal("mechanical", detail.WorkKindSuggested);
        var recorded = Assert.Single(detail.Events, e => e.Type == "task.work_kind_suggested");
        Assert.Equal("mechanical", recorded.Payload.GetProperty("workKind").GetString());
        Assert.Equal("intake", recorded.Payload.GetProperty("by").GetString());

        // Once owned, only the owner or the founder may change it; the founder overwrites rather than appends.
        var owner = await _hub.RegisterAgentAsync("owner");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostActionAsync(task.Id, "work-kind", new WorkKindRequest("general"))).StatusCode);
        (await _hub.Founder().PostActionAsync(task.Id, "work-kind", new WorkKindRequest("general"))).EnsureSuccessStatusCode();
        detail = await agent.GetTaskAsync(task.Id);
        Assert.Equal("general", detail.WorkKindSuggested);
        Assert.Equal(2, detail.Events.Count(e => e.Type == "task.work_kind_suggested"));
    }

    [Fact]
    public void The_orchestrator_prompt_shows_the_suggestion_as_advisory_and_nothing_when_there_is_none()
    {
        var assignment = new OrchestratorAssignment(1, "T-1", "Rename Foo to Bar everywhere", "demo");
        var candidate = new Muthur.Launch.HarnessCandidate("codex", "gpt", "acct");

        var prompt = OrchestratorSessionLauncher.Prompt(assignment, candidate, "mechanical");

        Assert.Contains("Suggested work-kind: mechanical (advisory; the spec's work-kind line decides).", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Suggested work-kind", OrchestratorSessionLauncher.Prompt(assignment, candidate), StringComparison.Ordinal);
    }
}
