using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The conductor on the board. Everything here is read off the prerendered HTML: "enabled, 0 running" reads
/// the same whether the conductor is idle or wedged, and a stall is the one state a founder has to act on,
/// so both have to reach the page rather than only the bus.
/// </summary>
public sealed class DashboardConductorTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public DashboardConductorTests() => _hub.Settings["Muthur:ConductorEnabled"] = "true";

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();

    private async Task SetUpAsync(params string[] validators)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators);
        foreach (var role in validators)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(role, $"# {role}\nDrive it."))).EnsureSuccessStatusCode();
    }

    private async Task<string> ValidatingTaskAsync(string title = "Build the feature", string branch = "task/T-1-feature")
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(branch, "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return task.Id;
    }

    [Fact]
    public async Task A_running_session_says_which_task_and_role_it_is_for()
    {
        await SetUpAsync("win-validator");
        _hub.Validators.Block = true;   // the session is still alive while we look at it
        var id = await ValidatingTaskAsync();

        Assert.Equal(1, await Conductor.RunPassAsync());

        var status = await Conductor.StatusAsync();
        Assert.Equal(status.Running, status.Sessions.Count);   // the count and the list can never disagree
        var session = Assert.Single(status.Sessions);
        Assert.Equal(id, session.Task);
        Assert.Equal("win-validator", session.Role);

        var page = await _hub.CreateClient().GetStringAsync("/");
        Assert.Contains("Conductor", page);
        Assert.Contains("1 of 2 running", page);
        Assert.Contains($"href=\"/tasks/{id}\"", page);
        Assert.Contains(">win-validator<", page);

        _hub.Validators.Finish();
    }

    [Fact]
    public async Task With_nothing_running_the_page_says_when_the_last_pass_was_and_what_it_did()
    {
        // The whole reason lastAction exists: idle and wedged both read "0 running".
        await SetUpAsync("win-validator");
        Assert.Contains("no pass yet", await _hub.CreateClient().GetStringAsync("/"));

        await Conductor.RunPassAsync();

        var page = await _hub.CreateClient().GetStringAsync("/");
        Assert.Contains("no sessions running", page);
        Assert.Contains("last pass", page);
        Assert.DoesNotContain("no pass yet", page);
    }

    [Fact]
    public async Task The_switch_reads_the_state_it_is_about_to_change()
    {
        await SetUpAsync();

        var on = await _hub.CreateClient().GetStringAsync("/");
        Assert.Contains(">Turn off<", on);
        Assert.Contains("0 of 2 running", on);

        await Conductor.SetEnabledAsync(Caller.Founder, false);

        var off = await _hub.CreateClient().GetStringAsync("/");
        Assert.Contains(">Turn on<", off);
        Assert.Contains("class=\"panel-sub\">off<", off);
    }

    [Fact]
    public async Task A_pair_with_attempts_left_is_not_a_stall_and_one_that_has_run_out_is()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        await SetUpAsync("win-validator");
        var id = await ValidatingTaskAsync();
        _hub.Validators.Throw = new ValidatorLaunchException("No available mastermind candidate.");

        // Two failures: the pair is in trouble and is not yet the founder's problem.
        for (var pass = 0; pass < 2; pass++) { await Conductor.RunPassAsync(); await SettledAsync(); }
        Assert.Empty((await Conductor.StatusAsync()).Stalls);
        Assert.DoesNotContain("stalled", await _hub.CreateClient().GetStringAsync("/"));

        var stalledAt = _hub.Clock.GetUtcNow();
        await Conductor.RunPassAsync();
        await SettledAsync();

        var stall = Assert.Single((await Conductor.StatusAsync()).Stalls);
        Assert.Equal(id, stall.Task);
        Assert.Equal("win-validator", stall.Role);
        Assert.Equal("never started", stall.Reason);
        Assert.Equal(3, stall.Failures);
        Assert.Equal(stalledAt + TimeSpan.FromMinutes(30), stall.NextProbeAt);

        var page = await _hub.CreateClient().GetStringAsync("/");
        Assert.Contains("pill-blocked", page);
        Assert.Contains("never started", page);
        Assert.Contains("3 failures", page);
        Assert.Contains("next probe", page);
    }

    [Fact]
    public async Task Turning_staffing_on_clears_the_stalls_the_page_was_showing()
    {
        // Existing behaviour — the founder saying "try again" is not answered by a stall from before it —
        // asserted here because the new list must not outlive the state it reports.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "1";
        await SetUpAsync("win-validator");
        await ValidatingTaskAsync();
        _hub.Validators.Throw = new ValidatorLaunchException("No available mastermind candidate.");
        await Conductor.RunPassAsync();
        await SettledAsync();
        Assert.Single((await Conductor.StatusAsync()).Stalls);

        await Conductor.SetEnabledAsync(Caller.Founder, false);
        await Conductor.SetEnabledAsync(Caller.Founder, true);

        Assert.Empty((await Conductor.StatusAsync()).Stalls);
        Assert.DoesNotContain("pill-blocked", await _hub.CreateClient().GetStringAsync("/"));
    }

    /// <summary>A pass starts its sessions and returns; what a session records, it records afterwards.</summary>
    private async Task SettledAsync()
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (Conductor.RunningCount > 0 && Environment.TickCount64 < deadline) await Task.Yield();
        Assert.Equal(0, Conductor.RunningCount);
    }

    /// <summary>
    /// What the panel reloads for. A live circuit is the one thing an unattended validator cannot make, so
    /// the decision lives in a static the test calls directly rather than only in a running component: every
    /// number on this panel is written by the conductor, and nothing else the hub records can change one.
    /// </summary>
    [Theory]
    [InlineData("conductor.on", true)]
    [InlineData("conductor.off", true)]
    [InlineData("conductor.staffing", true)]
    [InlineData("conductor.stalled", true)]
    [InlineData("conductor.failed", true)]
    [InlineData("conductor.no_verdict", true)]
    [InlineData("task.landed", false)]
    [InlineData("validation.passed", false)]
    [InlineData("role.taken", false)]
    [InlineData("message.sent", false)]
    public void The_panel_reloads_for_conductor_events_and_nothing_else(string eventType, bool watched) =>
        Assert.Equal(watched, Muthur.Server.Components.Panels.ConductorPanel.Watches(eventType));
}
