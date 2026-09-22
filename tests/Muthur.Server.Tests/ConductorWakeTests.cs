using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Server;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The conductor runs a pass when a task crosses a handoff, not when its interval next comes round. Every
/// wait here is on the fake clock or on a condition; the clock is advanced only where the test says so.
/// </summary>
public sealed class ConductorWakeTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new(integrationChecks: true);

    public ConductorWakeTests()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "0";
    }

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private ConductorWake Wake => _hub.Services.GetRequiredService<ConductorWake>();
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>A wait parked after the setup's own transitions have been consumed, so it sees only what follows.</summary>
    private async Task<Task<string?>> ParkAsync()
    {
        while (Wake.Pending) await Wake.WaitAsync(Interval, CancellationToken.None);
        return Wake.WaitAsync(Interval, CancellationToken.None);
    }

    private async Task DefineAsync(string role) =>
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(role, $"# {role}\nDrive it.", Holders: 1))).EnsureSuccessStatusCode();

    private async Task<(HttpClient Owner, string TaskId)> ClaimedTaskAsync(string branch = "task/T-1-feature")
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec()))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(branch, "feature.txt", "feature\n");
        return (owner, task.Id);
    }

    private async Task<(HttpClient Owner, HttpClient Checker, string TaskId, Guid Subject)> ValidatingTaskAsync()
    {
        await DefineAsync("win-validator");
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        var (owner, id) = await ClaimedTaskAsync();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        var checker = await _hub.RegisterAgentAsync("checker");
        (await checker.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        return (owner, checker, id, (await checker.GetTaskAsync(id)).Task.CurrentSubject!.Id);
    }

    [Fact]
    public async Task A_claim_wakes_a_parked_wait_without_the_clock_moving()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var before = _hub.Clock.GetUtcNow();
        var wait = Wake.WaitAsync(Interval, CancellationToken.None);

        var agent = await _hub.RegisterAgentAsync("owner");
        var task = await agent.AddTaskAsync("Build the feature");
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();

        Assert.Equal("task.claimed", await Eventually.CompletesAsync(wait, "the claim never woke the conductor"));
        Assert.Equal(before, _hub.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Implemented_and_integration_passed_wake()
    {
        // No validators required: implemented and validated are recorded in one mutation, and the first wins.
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var (owner, id) = await ClaimedTaskAsync();
        var wait = await ParkAsync();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        Assert.Equal("task.implemented", await Eventually.CompletesAsync(wait, "implemented never woke the conductor"));

        wait = await ParkAsync();
        await _hub.PassIntegrationAsync(_repo, id);
        Assert.Equal("integration.passed", await Eventually.CompletesAsync(wait, "the integration verdict never woke the conductor"));
    }

    [Fact]
    public async Task A_passing_verdict_wakes()
    {
        var (_, checker, id, subject) = await ValidatingTaskAsync();
        var wait = await ParkAsync();
        (await checker.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "Ran it; reproduce with dotnet test.", SubjectId: subject))).EnsureSuccessStatusCode();
        Assert.Equal("task.validated", await Eventually.CompletesAsync(wait, "the verdict never woke the conductor"));
    }

    [Fact]
    public async Task A_failed_verdict_and_an_answered_question_wake()
    {
        var (owner, checker, id, subject) = await ValidatingTaskAsync();
        var wait = await ParkAsync();
        (await checker.PostActionAsync(id, "fail", new VerdictRequest("win-validator", "wrong column; reproduce with dotnet test", SubjectId: subject))).EnsureSuccessStatusCode();
        Assert.Equal("task.validation_failed", await Eventually.CompletesAsync(wait, "the failed verdict never woke the conductor"));

        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("csv or xlsx?", id, ["csv", "xlsx"]));
        asked.EnsureSuccessStatusCode();
        var request = (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;
        wait = await ParkAsync();
        Assert.False(wait.IsCompleted);   // asking is not a handoff; the session that asked is still there
        (await _hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("csv"))).EnsureSuccessStatusCode();
        Assert.Equal("request.answered", await Eventually.CompletesAsync(wait, "the answer never woke the conductor"));
    }

    [Fact]
    public async Task Switching_the_conductor_on_wakes()
    {
        (await _hub.Founder().PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(false))).EnsureSuccessStatusCode();
        var wait = await ParkAsync();
        (await _hub.Founder().PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true))).EnsureSuccessStatusCode();
        Assert.Equal("conductor.on", await Eventually.CompletesAsync(wait, "turning the conductor on never woke it"));
    }

    [Fact]
    public async Task A_session_ending_nudges_once_its_slot_is_free()
    {
        // The staffing itself is not a transition; what wakes the wait is the fake session returning.
        await DefineAsync("win-validator");
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        var (owner, id) = await ClaimedTaskAsync();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var wait = await ParkAsync();
        Assert.Equal(1, await _hub.Services.GetRequiredService<ConductorService>().RunPassAsync());
        Assert.Equal("session.exited", await Eventually.CompletesAsync(wait, "the session ending never woke the conductor"));
        Assert.Equal(0, _hub.Services.GetRequiredService<ConductorService>().RunningCount);
    }

    [Fact]
    public async Task Other_events_do_not_wake_it()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var agent = await _hub.RegisterAgentAsync("owner");
        var wait = await ParkAsync();

        await agent.AddTaskAsync("Build the feature");
        (await agent.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest("alive"))).EnsureSuccessStatusCode();

        Assert.False(wait.IsCompleted);
        Assert.False(Wake.Pending);
        _hub.Clock.Advance(Interval);
        Assert.Null(await Eventually.CompletesAsync(wait, "the fallback never elapsed"));
    }

    [Fact]
    public async Task Nudges_coalesce_and_are_never_lost()
    {
        var feed = _hub.Services.GetRequiredService<EventFeed>();
        var wake = Wake;
        feed.Publish([new LedgerEvent { Actor = "test", Type = "task.claimed" }]);
        feed.Publish([new LedgerEvent { Actor = "test", Type = "task.validated" }]);
        Assert.True(wake.Pending);

        var first = wake.WaitAsync(Interval, CancellationToken.None);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal("task.claimed", await first);
        Assert.False(wake.Pending);

        var second = wake.WaitAsync(Interval, CancellationToken.None);
        Assert.False(second.IsCompleted);
        _hub.Clock.Advance(Interval);
        Assert.Null(await Eventually.CompletesAsync(second, "the fallback never elapsed"));
    }

    [Fact]
    public async Task A_cancelled_wait_throws_and_leaves_nothing_parked()
    {
        using var cts = new CancellationTokenSource();
        var wait = Wake.WaitAsync(Interval, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Eventually.CompletesAsync(wait, "cancellation never surfaced"));

        Wake.Nudge("task.claimed");   // nothing is parked any more, so this is kept for the next wait
        Assert.True(Wake.Pending);
        Assert.Equal("task.claimed", await Wake.WaitAsync(Interval, CancellationToken.None));
    }

    [Fact]
    public async Task The_worker_staffs_on_the_wake_not_the_tick()
    {
        await DefineAsync("win-validator");
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        var (owner, id) = await ClaimedTaskAsync();

        var worker = ActivatorUtilities.CreateInstance<ConductorWorker>(_hub.Services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // The first pass runs at start and finds nothing to staff; wait until the worker is parked on the wake.
            await Eventually.TrueAsync(() => _hub.Clock.Timers.Count > 0, "the worker never parked on its fallback");
            Assert.Empty(_hub.Validators.Started);
            var timers = _hub.Clock.Timers.Count;

            (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
            await Eventually.TrueAsync(() => _hub.Validators.Started.Count == 1, "the transition never staffed a validator");
            Assert.Equal(id, _hub.Validators.Started.Single().TaskKey);

            // Whatever the worker parked on after the wake is a full fallback, never a shorter timer that would
            // mean the staffing came from a tick.
            var floor = TimeSpan.FromSeconds(MuthurOptions.MinimumConductorIntervalSeconds);
            Assert.All(_hub.Clock.Timers.Skip(timers), t => Assert.True(t >= floor, $"a timer of {t} was created between the transition and the staffing"));
        }
        finally
        {
            _hub.Validators.Finish();
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
