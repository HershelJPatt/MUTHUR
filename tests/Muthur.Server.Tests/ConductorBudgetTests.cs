using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class ConductorBudgetTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose() { _hub.Dispose(); _repo.Dispose(); }

    [Fact]
    public async Task Recreated_conductor_cannot_spend_again_on_claim_release_churn_but_new_work_resets_budget()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var client = await _hub.RegisterAgentAsync("owner");
        var task = await client.AddTaskAsync("Small change");
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetOrchestratorsAsync(Caller.Founder, true);
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("task.implemented", int.Parse(task.Id.AsSpan(2)), new { head = "old-head" });
            return Task.CompletedTask;
        });
        _hub.Clock.Advance(TimeSpan.FromDays(2));
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            for (var i = 0; i < 3; i++)
            {
                m.Record("conductor.staffing", int.Parse(task.Id.AsSpan(2)), new { role = "#orchestrator" });
                m.Record("task.claimed", int.Parse(task.Id.AsSpan(2)), new { agent = "owner" });
            }
            // Re-announcing an unchanged head from before the rolling window is not new work.
            m.Record("task.implemented", int.Parse(task.Id.AsSpan(2)), new { head = "old-head" });
            return Task.CompletedTask;
        });
        Assert.Empty(await conductor.PlanOrchestratorsAsync());
        var recreated = ActivatorUtilities.CreateInstance<ConductorService>(_hub.Services);
        Assert.Empty(await recreated.PlanOrchestratorsAsync());
        Assert.Contains((await recreated.StatusAsync()).Stalls, s => s.Task == task.Id && s.Reason.Contains("budget"));
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("task.implemented", int.Parse(task.Id.AsSpan(2)), new { head = "new-head" });
            return Task.CompletedTask;
        });
        Assert.Single(await recreated.PlanOrchestratorsAsync());
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            for (var i = 0; i < 3; i++) m.Record("conductor.staffing", int.Parse(task.Id.AsSpan(2)), new { role = "#orchestrator" });
            m.Record("task.implemented", int.Parse(task.Id.AsSpan(2)), new { head = "new-head" });
            m.Record("task.spec_set", int.Parse(task.Id.AsSpan(2)), new { path = "same-spec.md" });
            return Task.CompletedTask;
        });
        Assert.Empty(await recreated.PlanOrchestratorsAsync());
        _hub.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.Single(await recreated.PlanOrchestratorsAsync());
    }

    [Fact]
    public async Task A_running_orchestrators_own_workers_do_not_spend_the_tasks_daily_budget()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var client = await _hub.RegisterAgentAsync("owner");
        var task = await client.AddTaskAsync("Small change");
        var id = int.Parse(task.Id.AsSpan(2));
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetOrchestratorsAsync(Caller.Founder, true);
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("conductor.staffing", id, new { role = "#orchestrator" });
            // Three units dispatched by the session the conductor started: one attempt, however many workers.
            for (var i = 0; i < 3; i++)
                m.Record("worker.admitted", id, new { reservationId = $"r{i}", role = "#orchestrator", ownerName = OrchestratorSessionLauncher.IdentityName(task.Id), task = task.Id, tier = "implementer", harness = "sim", model = "scripted", account = "sim", runId = $"run{i}", inputHash = new string('a', 64) });
            return Task.CompletedTask;
        });
        Assert.Single(await conductor.PlanOrchestratorsAsync());

        await ledger.MutateAsync(Caller.Founder, m =>
        {
            // A standing mastermind's workers are its own spending and count as before.
            for (var i = 3; i < 5; i++)
                m.Record("worker.admitted", id, new { reservationId = $"r{i}", role = "#orchestrator", ownerName = "owner", task = task.Id, tier = "implementer", harness = "sim", model = "scripted", account = "sim", runId = $"run{i}", inputHash = new string('a', 64) });
            return Task.CompletedTask;
        });
        Assert.Empty(await conductor.PlanOrchestratorsAsync());
    }

    [Fact]
    public async Task An_unset_cost_cap_changes_nothing()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "0";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var client = await _hub.RegisterAgentAsync("owner");
        var task = await client.AddTaskAsync("Costly task");
        var id = int.Parse(task.Id.AsSpan(2));
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("worker.finished", id, new { costUsd = 100m });
            return Task.CompletedTask;
        });
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetOrchestratorsAsync(Caller.Founder, true);

        Assert.Single(await conductor.PlanOrchestratorsAsync());
        Assert.Equal(0, await ledger.ReadAsync((db, _) => db.Events.CountAsync(e => e.Type == "conductor.budget_blocked")));
    }

    [Fact]
    public async Task A_task_over_its_daily_cost_cap_is_not_staffed_and_the_block_is_recorded_once()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "0";
        _hub.Settings["Muthur:ConductorCostPerTaskDay"] = "1.00";
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var client = await _hub.RegisterAgentAsync("owner");
        var task = await client.AddTaskAsync("Costly task");
        var id = int.Parse(task.Id.AsSpan(2));
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("worker.finished", id, new { costUsd = 0.70m });
            m.Record("worker.failed", id, new { costUsd = 0.50m });
            return Task.CompletedTask;
        });
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        await conductor.SetOrchestratorsAsync(Caller.Founder, true);

        for (var i = 0; i < 200; i++) Assert.Equal(0, await conductor.RunPassAsync());
        Assert.Empty(await conductor.PlanOrchestratorsAsync());
        Assert.Equal(1, await ledger.ReadAsync((db, _) => db.Events.CountAsync(e => e.Type == "conductor.budget_blocked")));

        // A new UTC day leaves yesterday's spending behind: nothing to sum, nothing to block.
        _hub.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Single(await conductor.PlanOrchestratorsAsync());
    }

    [Theory]
    [InlineData("#orchestrator")]
    [InlineData("validator")]
    public async Task Status_reads_durable_budget_without_a_planning_pass_and_answered_decisions_reset_it(string role)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var client = await _hub.RegisterAgentAsync("owner");
        var task = await client.AddTaskAsync("Waiting on a decision");
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            for (var i = 0; i < 3; i++) m.Record("conductor.staffing", int.Parse(task.Id.AsSpan(2)), new { role });
            return Task.CompletedTask;
        });
        var conductor = _hub.Services.GetRequiredService<ConductorService>();
        Assert.Contains((await conductor.StatusAsync()).Stalls, s => s.Task == task.Id && s.Role == role && s.Reason.Contains("budget"));
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("request.answered", int.Parse(task.Id.AsSpan(2)), new { answer = "resolved" });
            return Task.CompletedTask;
        });
        Assert.DoesNotContain((await conductor.StatusAsync()).Stalls, s => s.Task == task.Id && s.Reason.Contains("budget"));
    }
}
