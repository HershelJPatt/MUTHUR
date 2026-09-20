using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Launch;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    public void Dispose() { _hub.Dispose(); _repo.Dispose(); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_exhaustion_stops_staffing_across_tasks_and_preserves_longer_limits(bool validator)
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        await owner.AddTaskAsync("First");
        await owner.AddTaskAsync("Second");
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetOrchestratorsAsync(Caller.Founder, true);
        var harnesses = _hub.Services.GetRequiredService<HarnessService>();
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        Task Exhaust(string account) => validator
            ? ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services).MarkLimitedAsync(account, default)
            : ActivatorUtilities.CreateInstance<OrchestratorSessionLauncher>(_hub.Services).MarkLimitedAsync(account, default);
        var candidates = (await harnesses.TiersAsync("mastermind")).Single().Candidates;
        foreach (var c in candidates)
            await Exhaust(c.Account!);
        Assert.Empty(await conductor.PlanOrchestratorsAsync());
        Assert.Empty(await conductor.PlanAsync());
        Assert.NotNull((await conductor.StatusAsync()).StaffingWait);
        Assert.Equal(0, await conductor.RunPassAsync());
        var account = candidates[0].Account!;
        var longer = _hub.Clock.GetUtcNow().AddDays(2);
        await harnesses.ReportLimitAsync(Caller.Founder, new AccountLimitRequest(account, longer));
        await Exhaust(account);
        Assert.Equal(longer, (await harnesses.TiersAsync("mastermind")).Single().Candidates.Single(c => c.Account == account).LimitedUntil);
        _hub.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await conductor.PlanOrchestratorsAsync()).Count);
    }

    [Fact]
    public async Task Dependencies_survive_restart_prevent_claims_and_wake_only_after_landing()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var prerequisite = await owner.AddTaskAsync("Shared prerequisite");
        var task = await owner.AddTaskAsync("Dependent work");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "dependencies", new DependenciesRequest([prerequisite.Id], "Needs the shared fix"))).EnsureSuccessStatusCode();
        var waiting = (await owner.GetTaskAsync(task.Id)).Task;
        Assert.Equal(TaskState.Backlog, waiting.State);
        Assert.NotNull(waiting.SpecPath);
        Assert.Null(waiting.Owner);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.ClaimAsync(task.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostActionAsync(prerequisite.Id, "dependencies", new DependenciesRequest([task.Id]))).StatusCode);
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetOrchestratorsAsync(Caller.Founder, true);
        using var restarted = new HubFactory { DataDir = _hub.DataDir };
        Assert.DoesNotContain(await restarted.Services.GetRequiredService<ConductorService>().PlanOrchestratorsAsync(), a => a.TaskKey == task.Id);
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.Founder, async m =>
        {
            (await TaskService.LoadAsync(m.Db, prerequisite.Id, default)).State = TaskState.Cancelled;
            m.Record("test.cancelled", int.Parse(prerequisite.Id.AsSpan(2)));
        });
        await TaskDependencies.ResolveAsync(ledger, default);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.ClaimAsync(task.Id)).StatusCode);
        await ledger.MutateAsync(Caller.Founder, async m =>
        {
            (await TaskService.LoadAsync(m.Db, prerequisite.Id, default)).State = TaskState.Done;
            m.Record("task.landed", int.Parse(prerequisite.Id.AsSpan(2)));
        });
        await TaskDependencies.ResolveAsync(ledger, default);
        Assert.Equal(task.Id, Assert.Single(await conductor.PlanOrchestratorsAsync()).TaskKey);
        var ready = await owner.GetTaskAsync(task.Id);
        Assert.Empty(ready.Task.DependsOn!);
        Assert.Single(ready.Events, e => e.Type == "task.dependencies_ready");
    }

    [Fact]
    public async Task Launcher_renews_a_live_child_and_stops_renewing_after_exit()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var agents = _hub.Services.GetRequiredService<AgentService>();
        var registration = await agents.RegisterConductorSessionAsync(new("child", "codex", "test"));
        var owner = _hub.CreateClient(registration.Token);
        var task = await owner.AddTaskAsync("Long build");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var before = (await _hub.Founder().GetTaskAsync(task.Id)).Task.ClaimExpires;
        var runner = new HeldProcess();
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new AgentLauncher(runner, _ => ("fake", []), async (identity, ct) =>
        {
            await agents.RenewChildAsync(identity, ct);
            renewed.TrySetResult();
        }, _hub.Clock);
        var running = launcher.RunAsync([new("codex", "test", "local")],
            _ => new(_repo.Path, "prompt", "test", null, [], [], _repo.Path),
            _ => Task.FromResult(new AgentIdentity("child", registration.Token)), TimeSpan.FromMinutes(45), _ => Task.CompletedTask);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // No scheduler sleeps: advance the launcher's own timer.
        _hub.Clock.Advance(TimeSpan.FromMinutes(1));
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var after = (await _hub.Founder().GetTaskAsync(task.Id)).Task.ClaimExpires;
        Assert.True(after > before);
        runner.Finished.SetResult(new ProcessResult(0, "", ""));
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        _hub.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(after, (await _hub.Founder().GetTaskAsync(task.Id)).Task.ClaimExpires);
    }

    [Fact]
    public async Task Exited_owner_is_released_without_waiting_for_lease_but_a_new_claim_is_not_released()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var next = await _hub.RegisterAgentAsync("next");
        var task = await owner.AddTaskAsync("Preserved work");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("conductor.orchestrator_exited", int.Parse(task.Id.AsSpan(2)), new { agent = "owner" });
            return Task.CompletedTask;
        });
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.RunPassAsync();
        Assert.Equal(TaskState.Backlog, (await _hub.Founder().GetTaskAsync(task.Id)).Task.State);
        (await next.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        await conductor.RunPassAsync();
        Assert.Equal("next", (await _hub.Founder().GetTaskAsync(task.Id)).Task.Owner);
    }

    [Fact]
    public async Task Heartbeat_failure_cancels_and_observes_the_child_before_returning()
    {
        var runner = new HeldProcess();
        var launcher = new AgentLauncher(runner, _ => ("fake", []),
            (_, _) => throw new InvalidOperationException("revoked"), _hub.Clock);
        var running = launcher.RunAsync([new("codex", "test", "local")],
            _ => new(_repo.Path, "prompt", "test", null, [], [], _repo.Path),
            _ => Task.FromResult(new AgentIdentity("child", "test")), TimeSpan.FromMinutes(45), _ => Task.CompletedTask);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _hub.Clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(runner.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_exit_releases_roles_but_cannot_release_a_reregistered_session(bool reregister)
    {
        var agents = _hub.Services.GetRequiredService<AgentService>();
        var registration = await agents.RegisterConductorSessionAsync(new("reviewer", "codex", "test"));
        var child = _hub.CreateClient(registration.Token);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("review", "Review", true))).EnsureSuccessStatusCode();
        (await child.PostAsync(Routes.RoleAction("review", "take"), null)).EnsureSuccessStatusCode();
        if (reregister) await agents.RegisterConductorSessionAsync(new("reviewer", "codex", "test"));
        await agents.ReleaseChildAsync(new("reviewer", registration.Token), default);
        var roles = await _hub.Services.GetRequiredService<RoleService>().ListAsync();
        Assert.Equal(reregister ? 1 : 0, Assert.Single(roles).Holders.Count);
    }

    private sealed class HeldProcess : IProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProcessResult> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Token = ct;
            Started.TrySetResult();
            return Finished.Task.WaitAsync(ct);
        }
    }
}
