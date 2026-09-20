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
            for (var i = 0; i < 3; i++)
            {
                m.Record("conductor.staffing", int.Parse(task.Id.AsSpan(2)), new { role = "#orchestrator" });
                m.Record("task.claimed", int.Parse(task.Id.AsSpan(2)), new { agent = "owner" });
            }
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
}
