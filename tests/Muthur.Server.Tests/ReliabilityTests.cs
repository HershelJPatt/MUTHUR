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
    [InlineData("exited")]
    [InlineData("live")]
    [InlineData("reregistered")]
    public async Task Answered_work_recovers_on_next_pass_only_for_its_exited_session(string mode)
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var agents = _hub.Services.GetRequiredService<AgentService>();
        var registration = await agents.RegisterConductorSessionAsync(new("child", "codex", "test"));
        var owner = _hub.CreateClient(registration.Token);
        var task = await owner.AddTaskAsync("Answered work");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        async Task<int> Ask(string question)
        {
            var response = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest(question, task.Id));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<FounderRequestDto>())!.Id;
        }
        var first = await Ask("Scope?");
        var second = await Ask("Prerequisite?");
        if (mode != "live") await agents.ReleaseChildAsync(new("child", registration.Token), default);
        if (mode == "reregistered") await agents.RegisterConductorSessionAsync(new("child", "codex", "replacement"));
        var requests = _hub.Services.GetRequiredService<RequestService>();
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await requests.AnswerAsync(Caller.Founder, first, new("Documentation may ship now."));
        await conductor.RunPassAsync();
        Assert.Equal(TaskState.Blocked, (await _hub.Founder().GetTaskAsync(task.Id)).Task.State);
        await requests.AnswerAsync(Caller.Founder, second, new("Behavior still depends on T-67."));
        var expires = (await _hub.Founder().GetTaskAsync(task.Id)).Task.ClaimExpires;
        Assert.True(expires > _hub.Clock.GetUtcNow());
        await conductor.RunPassAsync(); // No clock advance or claim expiry.
        Assert.Equal(mode == "exited" ? TaskState.Backlog : TaskState.InProgress,
            (await _hub.Founder().GetTaskAsync(task.Id)).Task.State);
        if (mode == "exited")
        {
            await conductor.SetOrchestratorsAsync(Caller.Founder, true);
            var plan = Assert.Single(await conductor.PlanOrchestratorsAsync());
            Assert.Contains($"Request #{second}", plan.LatestDecisions);
            Assert.Contains("Behavior still depends on T-67.", plan.LatestDecisions);
            Assert.Contains("actor: founder", plan.LatestDecisions);
        }
    }

    [Fact]
    public async Task Dependency_wait_is_productive_and_records_the_return_to_backlog()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var dependency = await owner.AddTaskAsync("Prerequisite");
        var task = await owner.AddTaskAsync("Waiting work");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "dependencies", new DependenciesRequest([dependency.Id]))).EnsureSuccessStatusCode();
        var detail = await owner.GetTaskAsync(task.Id);
        Assert.Single(detail.Events, e => e.Type == "task.released");
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        Assert.False(await conductor.StillInBacklogAsync(int.Parse(task.Id.AsSpan(2))));
        (await owner.PostActionAsync(task.Id, "dependencies", new DependenciesRequest([]))).EnsureSuccessStatusCode();
        Assert.True(await conductor.StillInBacklogAsync(int.Parse(task.Id.AsSpan(2))));
    }

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
    public async Task Automatic_limits_back_off_within_a_day_and_never_shorten_a_founder_limit()
    {
        _ = _hub.Server;
        var harnesses = _hub.Services.GetRequiredService<HarnessService>();
        var launcher = ActivatorUtilities.CreateInstance<OrchestratorSessionLauncher>(_hub.Services);
        async Task<DateTimeOffset?> Until() =>
            (await harnesses.TiersAsync("mastermind")).Single().Candidates.First(c => c.Account == "a").LimitedUntil;
        File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile),
            """{ "tiers": { "mastermind": [ { "harness": "claude", "model": "opus", "account": "a" } ] } }""");

        var start = _hub.Clock.GetUtcNow();
        await launcher.MarkLimitedAsync("a", default);
        Assert.Equal(start.AddHours(1), await Until());
        _hub.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await launcher.MarkLimitedAsync("a", default);
        Assert.Equal(_hub.Clock.GetUtcNow().AddHours(2), await Until());
        _hub.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        await launcher.MarkLimitedAsync("a", default);
        Assert.Equal(_hub.Clock.GetUtcNow().AddHours(4), await Until());
        _hub.Clock.Advance(TimeSpan.FromHours(4) + TimeSpan.FromSeconds(1));
        await launcher.MarkLimitedAsync("a", default);
        Assert.Equal(_hub.Clock.GetUtcNow().AddHours(8), await Until());
        _hub.Clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1));
        await launcher.MarkLimitedAsync("a", default);
        Assert.Equal(_hub.Clock.GetUtcNow().AddHours(HarnessService.MaxBackoffHours), await Until());   // capped, not sixteen

        // A day later the count starts over, and a founder's longer limit is never shortened by the automatic one.
        _hub.Clock.Advance(TimeSpan.FromHours(30));
        var longer = _hub.Clock.GetUtcNow().AddDays(2);
        await harnesses.ReportLimitAsync(Caller.Founder, new AccountLimitRequest("a", longer));
        await launcher.MarkLimitedAsync("a", default);
        Assert.Equal(longer, await Until());
        var limited = (await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto))!
            .Where(e => e.Type == "account.limited").ToList();
        Assert.Equal(1, limited[0].Payload.GetProperty("attempt").GetInt32());
        Assert.Equal(2, limited[1].Payload.GetProperty("attempt").GetInt32());
        Assert.True(limited[0].Payload.GetProperty("automatic").GetBoolean());
    }

    private async Task<string> ValidatingTaskOnTwoTiersAsync()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "Check it", true))).EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile),
            """
            { "tiers": {
                "mastermind":  [ { "harness": "claude", "model": "opus", "account": "a" } ],
                "implementer": [ { "harness": "codex",  "model": "gpt",  "account": "b" } ] } }
            """);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Validate me");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        return task.Id;
    }

    private async Task<IReadOnlyList<EventDto>> SkippedAsync() =>
        (await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto))!
            .Where(e => e.Type == "conductor.skipped").ToList();

    [Fact]
    public async Task Validators_wait_only_when_their_own_tier_and_the_fallback_are_both_limited()
    {
        await ValidatingTaskOnTwoTiersAsync();
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        var launcher = ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services);

        Assert.Single(await conductor.PlanAsync());
        await launcher.MarkLimitedAsync("b", default);
        Assert.Single(await conductor.PlanAsync());          // mastermind is the fallback, so validation still goes ahead
        await launcher.MarkLimitedAsync("a", default);
        Assert.Empty(await conductor.PlanAsync());
        Assert.Empty(await conductor.PlanAsync());
        var skipped = Assert.Single(await SkippedAsync());    // once per window, not once per pass
        Assert.Equal("accounts_limited", skipped.Payload.GetProperty("reason").GetString());
        Assert.Equal("implementer+mastermind", skipped.Payload.GetProperty("tiers").GetString());
        _hub.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Single(await conductor.PlanAsync());
        Assert.Single(await SkippedAsync());
    }

    [Fact]
    public async Task A_session_that_runs_dry_is_not_restarted_every_pass()
    {
        await ValidatingTaskOnTwoTiersAsync();
        var launcher = ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services);
        // What the real launcher does when every candidate prints the quota banner: limits each account, then reports
        // a session that ran and produced nothing.
        _hub.Validators.OnStart = async _ =>
        {
            await launcher.MarkLimitedAsync("b", default);
            await launcher.MarkLimitedAsync("a", default);
        };
        _hub.Validators.Throw = new ValidatorSessionException("ERROR: You've hit your usage limit.");
        var conductor = _hub.Services.GetRequiredService<ConductorService>();

        var starts = new List<TimeSpan>();
        var begin = _hub.Clock.GetUtcNow();
        for (var minute = 0; minute < 200; minute++)
        {
            var before = _hub.Validators.Started.Count;
            await conductor.RunPassAsync();
            await Eventually.TrueAsync(async () => (await conductor.StatusAsync()).Sessions.Count == 0 ? "settled" : null, "a session never settled");
            if (_hub.Validators.Started.Count > before) starts.Add(_hub.Clock.GetUtcNow() - begin);
            _hub.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        // 1 h, then 2 h of backoff: three starts in 200 minutes, where one per pass would have been two hundred.
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180)], starts);
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
        var exitObserved = false;
        var launcher = new AgentLauncher(runner, _ => ("fake", []), async (identity, ct) =>
        {
            await agents.RenewChildAsync(identity, ct);
            renewed.TrySetResult();
        }, _hub.Clock, async (identity, ct) =>
        {
            Assert.True(runner.Finished.Task.IsCompleted);
            await agents.ReleaseChildAsync(identity, ct);
            exitObserved = true;
        });
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
        Assert.True(exitObserved);
        Assert.Contains((await _hub.Founder().GetTaskAsync(task.Id)).Events, e => e.Type == "conductor.orchestrator_exited");
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
