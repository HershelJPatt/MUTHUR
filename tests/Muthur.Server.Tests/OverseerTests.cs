using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class OverseerTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private OverseerService Service => _hub.Services.GetRequiredService<OverseerService>();
    private Ledger Ledger => _hub.Services.GetRequiredService<Ledger>();
    private static OverseerConfig Config => new(true, Account: "test-account", CooldownMinutes: 1, SessionMinutes: 2);
    public void Dispose() => _hub.Dispose();

    private async Task<(OverseerService.Assignment Assignment, Caller Caller, HttpClient Client)> Start()
    {
        await Service.ConfigureAsync(Caller.Founder, Config);
        var assignment = (await Service.PrepareAsync())!;
        Assert.NotNull(assignment);
        var agents = _hub.Services.GetRequiredService<AgentService>();
        var registration = await agents.RegisterConductorSessionAsync(new(OverseerService.Identity, "codex", "gpt-6-astra", "overseer"));
        var caller = (await agents.AuthenticateAsync(registration.Token))!;
        await Ledger.MutateAsync(Caller.System, async m =>
        {
            var row = await m.Db.Meta.SingleAsync(x => x.Key == OverseerService.StateKey);
            var state = JsonSerializer.Deserialize<OverseerService.State>(row.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await OverseerService.Store(m, OverseerService.StateKey, state with { Agent = caller.AgentId }, default);
        });
        return (assignment, caller, _hub.CreateClient(registration.Token));
    }

    [Fact]
    public async Task Disabled_by_default_and_founder_controls_configuration()
    {
        Assert.False((await Service.StatusAsync()).Config.Enabled);
        Assert.Null(await Service.PrepareAsync());
        await Assert.ThrowsAsync<MuthurException>(() => Service.ConfigureAsync(Caller.Anonymous, Config));
        await Assert.ThrowsAsync<MuthurException>(() => Service.ConfigureAsync(Caller.Founder, Config with { Account = null }));
        await Assert.ThrowsAsync<MuthurException>(() => Service.ConfigureAsync(Caller.Founder, Config with { MemoryChars = 13000 }));
        await Service.ConfigureAsync(Caller.Founder, Config with { Harness = "claude", Model = "fable", ReasoningEffort = "high" });
        var recreated = ActivatorUtilities.CreateInstance<OverseerService>(_hub.Services);
        Assert.Equal("claude", (await recreated.StatusAsync()).Config.Harness);
    }

    [Fact]
    public async Task Technical_decision_unblocks_task_with_attributed_reason_but_human_request_is_refused()
    {
        await _hub.AddProjectAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Clarify implementation");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("Which parser?", task.Id, Kind: "technical"));
        var request = (await asked.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        var (assignment, caller, client) = await Start();
        var decided = await client.PostAsJsonAsync(Routes.Api + "/overseer/decide", new OverseerDecision(request.Id, "Reuse parser", "Preserves contract", "spec:parser"));
        decided.EnsureSuccessStatusCode();
        Assert.Equal(TaskState.InProgress, (await owner.GetTaskAsync(task.Id)).Task.State);
        var result = (await decided.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        Assert.Contains("Reason: Preserves contract", result.Answer);
        var humanResponse = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("Should we build a new product?", Kind: "human"));
        var human = (await humanResponse.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        var denied = await client.PostAsJsonAsync(Routes.Api + "/overseer/decide", new OverseerDecision(human.Id, "Yes", "Preference", "none"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var events = await Ledger.ReadAsync((db, _) => db.Events.Where(x => x.Type == "request.answered").ToListAsync());
        Assert.Contains(events, x => x.Actor == OverseerService.Identity);
    }

    [Theory]
    [InlineData("Keep the shared dirty brief refusal?")]
    [InlineData("Preserve documented spec fallback compatibility?")]
    [InlineData("Normalize harness identities and reject collisions?")]
    public async Task Default_request_can_be_classified_then_answered_without_founder(string question)
    {
        await _hub.AddProjectAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Engineering decision");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest(question, task.Id));
        var request = (await asked.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        Assert.Equal("triage", request.Kind);
        var (_, _, client) = await Start();
        var premature = await client.PostAsJsonAsync(Routes.Api + "/overseer/decide", new OverseerDecision(request.Id, "Yes", "Contract", "spec"));
        Assert.Equal(HttpStatusCode.Unauthorized, premature.StatusCode);
        var triaged = await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", new OverseerTriage(request.Id, "technical", "Existing engineering contract", "spec and regression tests"));
        triaged.EnsureSuccessStatusCode();
        var routed = (await triaged.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        Assert.Equal("technical", routed.Kind);
        Assert.Contains("spec and regression tests", routed.RouteReason);
        Assert.Equal(TaskState.Blocked, (await owner.GetTaskAsync(task.Id)).Task.State);
        var listed = (await owner.GetFromJsonAsync<FounderRequestDto[]>(Routes.Requests))!;
        Assert.Equal(routed.RouteReason, Assert.Single(listed).RouteReason);
        var events = await Ledger.ReadAsync((db, _) => db.Events.Where(x => x.Type == "request.triaged").ToListAsync());
        Assert.Equal(OverseerService.Identity, Assert.Single(events).Actor);
        var decided = await client.PostAsJsonAsync(Routes.Api + "/overseer/decide", new OverseerDecision(request.Id, "Preserve the contract", "Standing technical authority", "spec and tests"));
        decided.EnsureSuccessStatusCode();
        Assert.Equal(TaskState.InProgress, (await owner.GetTaskAsync(task.Id)).Task.State);
        var closed = await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", new OverseerTriage(request.Id, "human", "Closed", "request"));
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
    }

    [Theory]
    [InlineData("Product preference")]
    [InlineData("Spending increase")]
    [InlineData("Permission expansion")]
    [InlineData("Account access")]
    [InlineData("Outbound message")]
    [InlineData("Secret scanner approval")]
    public async Task Human_classification_is_terminal_for_overseer(string reason)
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest(reason));
        var request = (await asked.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        var (assignment, caller, client) = await Start();
        (await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", new OverseerTriage(request.Id, "human", reason, "Standing human boundary"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", new OverseerTriage(request.Id, "technical", "Override", "none"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Routes.Api + "/overseer/decide", new OverseerDecision(request.Id, "Yes", "Override", "none"))).StatusCode);
        await Service.CheckpointAsync(caller, new(assignment.Run, "Human decision remains open", [], true));
        _hub.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(await Service.PrepareAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_and_legacy_human_routes_cannot_be_downgraded(bool legacy)
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("Founder preference", Kind: "human"));
        var request = (await asked.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        if (legacy) await Ledger.MutateAsync(Caller.System, async m =>
        {
            m.Db.Meta.Remove(await m.Db.Meta.SingleAsync(x => x.Key == $"request.kind.{request.Id}"));
        });
        var (_, _, client) = await Start();
        Assert.Equal("human", Assert.Single((await owner.GetFromJsonAsync<FounderRequestDto[]>(Routes.Requests))!).Kind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", new OverseerTriage(request.Id, "technical", "Override", "none"))).StatusCode);
    }

    [Fact]
    public async Task Triage_requires_active_authority_and_evidence_and_can_protect_mislabelled_technical_requests()
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("Account authorization", Kind: "technical"));
        var request = (await asked.Content.ReadFromJsonAsync<FounderRequestDto>())!;
        var body = new OverseerTriage(request.Id, "human", "Account access stays human", "Standing boundary");
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync(Routes.Api + "/overseer/triage", body)).StatusCode);
        var (_, _, client) = await Start();
        var invalid = await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", body with { Evidence = "" });
        Assert.False(invalid.IsSuccessStatusCode);
        Assert.Equal("technical", Assert.Single((await owner.GetFromJsonAsync<FounderRequestDto[]>(Routes.Requests))!).Kind);
        (await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", body)).EnsureSuccessStatusCode();
        await Service.RecoverAfterRestartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Routes.Api + "/overseer/triage", body)).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/overseer/config")]
    [InlineData("/api/v1/messages")]
    [InlineData("/api/v1/outbound")]
    [InlineData("/api/v1/conductor/sessions")]
    [InlineData("/api/v1/requests/1/answer")]
    public async Task Overseer_cannot_use_other_mutation_surfaces(string path)
    {
        var (_, _, client) = await Start();
        var result = await client.PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Disabling_revokes_active_decision_authority()
    {
        var (assignment, caller, _) = await Start();
        await Service.ConfigureAsync(Caller.Founder, Config with { Enabled = false });
        await Assert.ThrowsAsync<MuthurException>(() => Service.CheckpointAsync(caller, new(assignment.Run, "saved", [])));
    }

    [Fact]
    public async Task Checkpoint_is_bounded_and_pending_condition_groups_survive_compaction_and_recreation()
    {
        var (assignment, caller, _) = await Start();
        var at = _hub.Clock.GetUtcNow();
        var waits = new[] {
            new OverseerWait("time", at.AddMinutes(5).ToString("O"), "", "first", "both"),
            new OverseerWait("time", at.AddMinutes(10).ToString("O"), "", "second", "both") };
        await Service.CheckpointAsync(caller, new(assignment.Run, "Need both prerequisites; evidence retained", waits, true));
        await Service.CheckpointAsync(caller, new(assignment.Run, "Compact summary", [], true));
        Assert.Equal(2, (await Service.StatusAsync()).Waits.Count);
        await Assert.ThrowsAsync<MuthurException>(() => Service.CheckpointAsync(caller, new(assignment.Run, new string('a', 6001), [])));
        _hub.Clock.Advance(TimeSpan.FromMinutes(6));
        var recreated = ActivatorUtilities.CreateInstance<OverseerService>(_hub.Services);
        Assert.Null(await recreated.PrepareAsync());
        _hub.Clock.Advance(TimeSpan.FromMinutes(5));
        var next = await recreated.PrepareAsync();
        Assert.NotNull(next);
        Assert.Contains("Compact summary", next.Prompt);
        Assert.True(next.Prompt.Length <= Config.ContextChars);
    }

    [Fact]
    public async Task No_checkpoint_retries_are_bounded_by_persistent_daily_budget_and_cooldown()
    {
        await Service.ConfigureAsync(Caller.Founder, Config with { MaxStartsPerDay = 2 });
        Assert.NotNull(await Service.PrepareAsync());
        Assert.Null(await Service.PrepareAsync());
        _hub.Clock.Advance(TimeSpan.FromMinutes(4));
        var recreated = ActivatorUtilities.CreateInstance<OverseerService>(_hub.Services);
        Assert.NotNull(await recreated.PrepareAsync());
        _hub.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(await recreated.PrepareAsync());
        Assert.Equal(2, (await recreated.StatusAsync()).StartsToday);
    }

    [Fact]
    public async Task Unlimited_starts_survive_recreation_but_still_obey_cooldown()
    {
        Assert.Equal(20, new OverseerConfig().SessionMinutes);
        Assert.Equal(0, new OverseerConfig().MaxStartsPerDay);
        await Service.ConfigureAsync(Caller.Founder, Config with { SessionMinutes = 20, MaxStartsPerDay = 0 });
        await Ledger.MutateAsync(Caller.System, m =>
        {
            for (var i = 0; i < 120; i++) m.Record("overseer.started");
            return Task.CompletedTask;
        });
        var recreated = ActivatorUtilities.CreateInstance<OverseerService>(_hub.Services);
        var assignment = await recreated.PrepareAsync();
        Assert.NotNull(assignment);
        Assert.Equal(20, assignment.Config.SessionMinutes);
        Assert.Equal(121, (await recreated.StatusAsync()).StartsToday);
        Assert.Null(await recreated.PrepareAsync());
        await Assert.ThrowsAsync<MuthurException>(() => Service.ConfigureAsync(Caller.Founder, Config with { MaxStartsPerDay = -1 }));
    }

    [Fact]
    public async Task Saved_checkpoint_suppresses_unchanged_work_but_preserves_new_events()
    {
        var (assignment, caller, _) = await Start();
        await Service.CheckpointAsync(caller, new(assignment.Run, "Handled current issues", [], true));
        _hub.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(await Service.PrepareAsync());
        await Ledger.MutateAsync(Caller.System, m => { m.Record("validation.failed", payload: new { reason = "new issue" }); return Task.CompletedTask; });
        Assert.NotNull(await Service.PrepareAsync());
    }

    [Fact]
    public async Task Limited_account_does_not_start_overseer()
    {
        await Service.ConfigureAsync(Caller.Founder, Config);
        await Ledger.MutateAsync(Caller.System, m => HarnessService.ExhaustedAsync(m, "test-account", default));
        Assert.Null(await Service.PrepareAsync());
        Assert.Equal(0, (await Service.StatusAsync()).StartsToday);
    }

    [Fact]
    public async Task Restart_revokes_old_run_but_keeps_checkpoint_and_daily_spend()
    {
        var (assignment, caller, _) = await Start();
        await Service.CheckpointAsync(caller, new(assignment.Run, "Evidence and pending work", []));
        await Service.RecoverAfterRestartAsync();
        var status = await Service.StatusAsync();
        Assert.Null(status.Run);
        Assert.Equal("Evidence and pending work", status.Summary);
        Assert.Equal(1, status.StartsToday);
        await Assert.ThrowsAsync<MuthurException>(() => Service.CheckpointAsync(caller, new(assignment.Run, "stale overwrite", [])));
    }

    [Fact]
    public async Task Interim_checkpoint_does_not_consume_unfinished_work()
    {
        var (assignment, caller, _) = await Start();
        await Service.CheckpointAsync(caller, new(assignment.Run, "Still investigating; original evidence", []));
        await Service.RecoverAfterRestartAsync();
        _hub.Clock.Advance(TimeSpan.FromMinutes(4));
        var resumed = await Service.PrepareAsync();
        Assert.NotNull(resumed);
        Assert.Contains("Still investigating", resumed.Prompt);
    }

    [Fact]
    public async Task Quiet_aged_task_wakes_once_without_new_events()
    {
        await _hub.AddProjectAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Stranded work");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var (assignment, caller, _) = await Start();
        await Service.CheckpointAsync(caller, new(assignment.Run, "Nothing stale yet", [], true));
        _hub.Clock.Advance(TimeSpan.FromMinutes(91));
        Assert.NotNull(await Service.PrepareAsync());
    }

    [Fact]
    public async Task Overseer_waits_when_both_conductor_slots_are_occupied()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        _hub.Orchestrators.Block = true;
        using var repo = new TestRepo();
        await _hub.AddProjectAsync(repoPath: repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        await owner.AddTaskAsync("First");
        await owner.AddTaskAsync("Second");
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetOrchestratorsAsync(Caller.Founder, true);
        Assert.Equal(2, await conductor.RunPassAsync());
        await Service.ConfigureAsync(Caller.Founder, Config);
        Assert.Equal(0, await conductor.RunPassAsync());
        Assert.Equal(0, (await Service.StatusAsync()).StartsToday);
        Assert.Equal(2, conductor.RunningCount);
        await conductor.StopSessionsAsync();
    }
}
