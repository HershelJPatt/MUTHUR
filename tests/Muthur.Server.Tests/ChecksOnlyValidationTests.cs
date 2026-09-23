using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The deterministic validator (plan U2.2): where the project switched checks-only validation on at the
/// implementation commit and the spec declared nothing that takes judgment, the subject's checks are the verdict,
/// run by the conductor in the pass, under one registered identity, taking no seat. Everything else is still a
/// session, exactly as before.
/// </summary>
public sealed class ChecksOnlyValidationTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new(integrationChecks: true);

    public ChecksOnlyValidationTests()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "0";
    }

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();

    /// <summary>The project file at the commit the subject is judged at, which is where the switch lives.</summary>
    private void SwitchOn()
    {
        _repo.Write("muthur.project.json", "{\"build\":\"echo fixture-build\",\"test\":\"echo fixture-test\",\"checksOnlyValidation\":true}");
        _repo.Commit("checks-only validation on");
    }

    private async Task SetUpAsync(bool checksOnly)
    {
        if (checksOnly) SwitchOn();
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "# win-validator\nDrive it."))).EnsureSuccessStatusCode();
    }

    /// <summary>A task in validating whose committed spec carries <paramref name="specBody"/> above its Verification section.</summary>
    private async Task<(HttpClient Owner, string TaskId)> TaskInValidationAsync(string specBody = "", string? verification = null)
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var file = $"specs/{task.Id}.md";
        _repo.Write(file, $"# {task.Id} — a spec for the test\n{specBody}{verification ?? TestRepo.Verification}");
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(file))).EnsureSuccessStatusCode();
        _repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    private async Task SettledAsync()
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (Conductor.RunningCount > 0 && Environment.TickCount64 < deadline) await Task.Yield();
        Assert.Equal(0, Conductor.RunningCount);
    }

    /// <summary>Until no integration runner is out. It is not a session, so <see cref="SettledAsync"/> does not see it.</summary>
    private async Task IntegrationSettledAsync()
    {
        var deadline = Environment.TickCount64 + 30_000;
        while ((await Conductor.StatusAsync()).Sessions.Any(s => s.Role == ConductorService.IntegrationRole) && Environment.TickCount64 < deadline) await Task.Yield();
        Assert.DoesNotContain((await Conductor.StatusAsync()).Sessions, s => s.Role == ConductorService.IntegrationRole);
    }

    private async Task<IReadOnlyList<EventDto>> EventsAsync() =>
        (await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto))!;

    [Fact]
    public async Task With_the_switch_off_a_session_is_planned_as_today()
    {
        await SetUpAsync(checksOnly: false);
        var (_, id) = await TaskInValidationAsync();

        Assert.Empty(await Conductor.PlanDeterministicAsync());
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.Equal(id, Assert.Single(_hub.Validators.Started).TaskKey);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type == "validation.checks_run");
    }

    [Fact]
    public async Task With_the_switch_on_a_spec_that_asks_for_judgment_still_gets_a_session()
    {
        await SetUpAsync(checksOnly: true);
        var (_, id) = await TaskInValidationAsync("\nvalidation: judgment\n");

        Assert.Empty(await Conductor.PlanDeterministicAsync());
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.Equal(id, Assert.Single(_hub.Validators.Started).TaskKey);
    }

    [Fact]
    public async Task With_the_switch_on_and_commands_only_the_checks_are_the_verdict_and_no_seat_is_used()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "0";   // no seat at all: the verdict must not need one
        await SetUpAsync(checksOnly: true);
        var (owner, id) = await TaskInValidationAsync();

        var planned = Assert.Single(await Conductor.PlanDeterministicAsync());
        Assert.Equal("win-validator", planned.RoleKey);
        Assert.Equal(["echo fixture-build", "echo fixture-test", "echo verified"], planned.Commands);
        Assert.Empty(await Conductor.PlanAsync());

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(0, Conductor.RunningCount);
        Assert.Empty(_hub.Validators.Started);

        var task = (await owner.GetTaskAsync(id)).Task;
        Assert.Equal(TaskState.Validated, task.State);
        var verdict = Assert.Single(task.Validations);
        Assert.Equal("yes", verdict.Verdict);
        Assert.Contains("pass exit=0: echo verified", verdict.Evidence);
        Assert.Contains("fixture-build", verdict.Evidence);

        var events = await EventsAsync();
        var passed = Assert.Single(events, e => e.Type == "validation.passed");
        Assert.Equal(AgentService.DeterministicValidatorModel, passed.ActorModel);
        Assert.Equal(AgentService.DeterministicValidator, passed.Actor);
        var ran = Assert.Single(events, e => e.Type == "validation.checks_run");
        Assert.True(ran.Seq < passed.Seq);
        Assert.Equal(id, ran.Payload.GetProperty("task").GetString());
        Assert.Equal("win-validator", ran.Payload.GetProperty("role").GetString());
        Assert.Equal(3, ran.Payload.GetProperty("commands").GetArrayLength());
        Assert.True(ran.Payload.GetProperty("seconds").GetDouble() >= 0);
        Assert.Contains(events, e => e.Type == "agent.registered" && e.Payload.GetProperty("agent").GetString() == AgentService.DeterministicValidator);
        Assert.False(Directory.Exists(Path.Combine(_repo.Path, ".worktrees", $"checks-{task.CurrentSubject!.Id:N}")));

        // Nothing left to decide: a second pass runs no checks and registers nothing twice. What it does start
        // is the integration runner every validated task gets - a script, not a seat - which has no CLI here.
        await Conductor.RunPassAsync();
        await IntegrationSettledAsync();
        Assert.Single(await EventsAsync(), e => e.Type == "validation.checks_run");
        Assert.Single(await EventsAsync(), e => e.Type == "agent.registered" && e.Payload.GetProperty("agent").GetString() == AgentService.DeterministicValidator);

        // And it lands through the path every validated task takes.
        await _hub.PassIntegrationAsync(_repo, id);
        (await owner.PostAsync(Routes.TaskAction(id, "land"), null)).EnsureSuccessStatusCode();
        Assert.Equal(TaskState.Done, (await owner.GetTaskAsync(id)).Task.State);
    }

    [Fact]
    public async Task A_failing_command_fails_the_round_with_its_exit_code_and_returns_the_task_to_its_owner()
    {
        await SetUpAsync(checksOnly: true);
        var (owner, id) = await TaskInValidationAsync(verification: "\n## Verification\n\n```\necho about to fail\nexit 3\necho never runs\n```\n");

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);

        var task = (await owner.GetTaskAsync(id)).Task;
        Assert.Equal(TaskState.InProgress, task.State);
        Assert.Equal("owner", task.Owner);
        var events = await EventsAsync();
        var failed = Assert.Single(events, e => e.Type == "validation.failed");
        Assert.Equal(AgentService.DeterministicValidatorModel, failed.ActorModel);
        var evidence = failed.Payload.GetProperty("evidence").GetString()!;
        Assert.Contains("fail exit=3: exit 3", evidence);
        Assert.Contains("skipped exit=-: echo never runs", evidence);
        Assert.Contains("pass exit=0: echo fixture-build", evidence);
        Assert.Contains("about to fail", evidence);
        Assert.Contains(events, e => e.Type == "validation.checks_run" && e.Payload.GetProperty("passed").GetBoolean() == false);
        Assert.Contains(events, e => e.Type == "task.validation_failed");
    }
}
