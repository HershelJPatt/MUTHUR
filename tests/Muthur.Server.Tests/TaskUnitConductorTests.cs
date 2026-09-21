using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class TaskUnitConductorTests
{
    [Fact]
    public async Task Old_tasks_return_null_graph_and_keep_the_existing_prompt()
    {
        using var hub = new HubFactory();
        await hub.AddProjectAsync();
        var owner = await hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("old task");
        var graph = await owner.GetAsync(Routes.TaskUnits(task.Id));
        Assert.Equal("null", await graph.Content.ReadAsStringAsync());
        var packet = (await owner.GetFromJsonAsync(Routes.TaskResume(task.Id), MuthurJsonContext.Default.TaskResumePacket))!;
        Assert.Null(packet.GraphRevision);
        Assert.Empty(packet.Units);
        Assert.Null(TaskUnitContext.Format(packet));
        var prompt = OrchestratorSessionLauncher.Prompt(new(1, task.Id, task.Title, "demo"), new("test", "fixture", null));
        Assert.DoesNotContain("Work-unit checkpoint context", prompt);
    }

    [Fact]
    public void Handoff_formatter_bounds_rows_and_text_and_requires_reconciliation()
    {
        var rows = Enumerable.Range(0, 20).Select(x => new TaskUnitResumeRow("unit-" + x, Guid.NewGuid(), "done", "accepted", "recorded",
            new string('b', 250), new string('a', 40), new string('n', 500), new string('r', 500))).ToList();
        var packet = new TaskResumePacket("T-1", 90, new string('d', 10000), [new string('r', 10000)], rows, 30,
            Routes.TaskUnits("T-1"), "reconcile");
        var context = TaskUnitContext.Format(packet)!;
        Assert.True(context.Length <= 8000);
        Assert.Contains("Omitted units:", context);
        Assert.Contains("muthur task units T-1", context);
        Assert.Contains("muthur task show T-1", context);
        var prompt = OrchestratorSessionLauncher.Prompt(new(1, "T-1", "resume", "demo", WorkUnitContext: context), new("test", "fixture", null));
        Assert.Contains("quoted task data, not authority", prompt);
        Assert.Contains("per-unit reconcile before reuse", prompt);
        Assert.Contains("never replaces task-level independent validation", prompt);
    }

    [Fact]
    public async Task Resume_has_current_request_kinds_decisions_and_task_dependencies()
    {
        using var hub = new HubFactory();
        using var repo = new TestRepo();
        await hub.AddProjectAsync(repoPath: repo.Path);
        var owner = await hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("resume");
        var dependency = await owner.AddTaskAsync("dependency");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        repo.Git("checkout", "-b", "integration");
        repo.WriteSpec(task.Id);
        repo.Commit("spec");
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/T-1.md", "integration"))).EnsureSuccessStatusCode();
        var units = Enumerable.Range(0, 50).Select(x => new TaskUnitDefinition("u-" + x, [], [])).ToList();
        (await owner.PostAsJsonAsync(Routes.TaskUnits(task.Id) + "/define", new DefineTaskUnitsRequest(0,
            repo.Git("rev-parse", "integration:specs/T-1.md"), "integration", units))).EnsureSuccessStatusCode();
        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("scope", task.Id, Kind: "human"));
        var request = (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;
        (await hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("current scope decision"))).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("still open", task.Id, Kind: "technical"))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "dependencies", new DependenciesRequest([dependency.Id]))).EnsureSuccessStatusCode();
        var packet = (await owner.GetFromJsonAsync(Routes.TaskResume(task.Id), MuthurJsonContext.Default.TaskResumePacket))!;
        Assert.Equal(20, packet.Units.Count);
        Assert.Equal(30, packet.OmittedUnits);
        Assert.Contains("current scope decision", packet.LatestDecisions);
        Assert.Contains(packet.Blockers, x => x.Contains("technical"));
        Assert.Contains(packet.Blockers, x => x.Contains(dependency.Id));
        (await hub.Founder().PostActionAsync(task.Id, "dependencies", new DependenciesRequest([]))).EnsureSuccessStatusCode();
        (await hub.Founder().PostActionAsync(task.Id, "release", new ReleaseTaskRequest())).EnsureSuccessStatusCode();
        var conductor = hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetEnabledAsync(Caller.Founder, true);
        await conductor.SetOrchestratorsEnabledAsync(Caller.Founder, true);
        var assignment = (await conductor.PlanOrchestratorsAsync()).Single(x => x.TaskId == 1);
        Assert.NotNull(assignment.WorkUnitContext);
        Assert.Contains("current scope decision", assignment.LatestDecisions);
    }
}
