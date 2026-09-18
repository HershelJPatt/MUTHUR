using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The conductor staffs validation. What it refuses to staff matters more than what it staffs: a session it
/// starts by mistake spends the founder's subscription on nothing.
/// </summary>
public sealed class ConductorTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public ConductorTests() => _hub.Settings["Muthur:ConductorEnabled"] = "true";

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();

    private async Task<string> TaskInValidationAsync(string title = "Build the feature", string branch = "task/T-1-feature", string file = "feature.txt") =>
        (await OwnedTaskInValidationAsync(title, branch, file)).TaskId;

    private async Task<(HttpClient Owner, string TaskId)> OwnedTaskInValidationAsync(string title = "Build the feature", string branch = "task/T-1-feature", string file = "feature.txt")
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/T-1.md"))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(branch, file, "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    private Task SetUpAsync(params string[] validators) => _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators);

    private async Task DefineAsync(params string[] roles)
    {
        foreach (var role in roles)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(role, $"# {role}\nDrive it."))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_task_waiting_on_a_role_nobody_holds_gets_a_session()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var id = await TaskInValidationAsync();

        Assert.Equal(1, await Conductor.RunPassAsync());

        var started = Assert.Single(_hub.Validators.Started);
        Assert.Equal(id, started.TaskKey);
        Assert.Equal("win-validator", started.RoleKey);
    }

    [Fact]
    public async Task A_role_someone_already_holds_is_left_alone()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        var human = await _hub.RegisterAgentAsync("human");
        (await human.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);
    }

    [Fact]
    public async Task A_lapsed_hold_is_restaffed()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        var human = await _hub.RegisterAgentAsync("human");
        (await human.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        Assert.Equal(0, await Conductor.RunPassAsync());

        // The session went quiet; its hold lapses and the work is staffable again.
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(1, await Conductor.RunPassAsync());
    }

    [Fact]
    public async Task A_blocked_task_is_never_staffed()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Needs a decision");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("Monthly or annual?", task.Id, ["monthly", "annual"]))).EnsureSuccessStatusCode();

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);
    }

    [Fact]
    public async Task The_session_budget_is_a_ceiling()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "1";
        await SetUpAsync("win-validator", "web-validator");
        await DefineAsync("win-validator", "web-validator");
        _hub.Validators.Block = true;   // hold the one session open across the pass
        await TaskInValidationAsync();

        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Single(_hub.Validators.Started);

        // A second pass while the first session is still running adds nothing.
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Single(_hub.Validators.Started);

        _hub.Validators.Finish();
    }

    [Fact]
    public async Task The_harness_that_built_the_task_is_the_one_the_plan_says_to_avoid()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("builder", harness: "claude", model: "opus");
        var task = await owner.AddTaskAsync("Built by Claude");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/T-1.md"))).EnsureSuccessStatusCode();
        _repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var assignment = Assert.Single(await Conductor.PlanAsync());
        Assert.Equal("claude", assignment.AvoidHarness);
    }

    [Theory]
    // The harness that built it goes to the back of the queue, never off it: validating beats not validating.
    [InlineData("claude", new[] { "codex", "claude" })]
    [InlineData("codex", new[] { "claude", "codex" })]
    [InlineData(null, new[] { "claude", "codex" })]
    public void A_different_vendor_is_preferred_but_the_same_one_still_validates(string? avoid, string[] expected)
    {
        var available = new[]
        {
            new Muthur.Launch.HarnessCandidate("claude", "opus", "claude-subscription"),
            new Muthur.Launch.HarnessCandidate("codex", "", "chatgpt-subscription"),
        };

        Assert.Equal(expected, ValidatorSessionLauncher.Prefer(available, avoid).Select(c => c.Harness));
    }

    [Fact]
    public void When_the_other_vendor_is_out_of_quota_the_builder_validates_its_own_project()
    {
        // Not ideal and deliberately allowed: an unvalidated task helps nobody.
        var onlyClaude = new[] { new Muthur.Launch.HarnessCandidate("claude", "opus", "claude-subscription") };

        Assert.Equal(["claude"], ValidatorSessionLauncher.Prefer(onlyClaude, "claude").Select(c => c.Harness));
    }

    [Fact]
    public async Task After_three_failed_verdicts_it_stops_and_asks_the_founder()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await checker.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
            (await checker.PostActionAsync(id, "fail", new VerdictRequest("win-validator", $"broken, round {attempt}"))).EnsureSuccessStatusCode();
            (await checker.PostAsync(Routes.RoleAction("win-validator", "release"), null)).EnsureSuccessStatusCode();
            (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        }

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);

        var events = await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Contains(events!, e => e.Type == "conductor.exhausted");
        Assert.DoesNotContain(events!, e => e.Type == "conductor.staffing");
    }

    [Fact]
    public async Task The_founders_decision_outlives_the_process_that_heard_it()
    {
        // The hub is off for hours at a time. A founder who turned staffing on must not find it silently off.
        _hub.Settings["Muthur:ConductorEnabled"] = "false";
        (await _hub.Founder().PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true))).EnsureSuccessStatusCode();

        using var restarted = new HubFactory { DataDir = _hub.DataDir };
        var status = await restarted.CreateClient().GetFromJsonAsync(Routes.Conductor, MuthurJsonContext.Default.ConductorStatusDto);

        Assert.True(status!.Enabled, "the switch is in the database, not in the process that was told");
    }

    [Fact]
    public async Task Turned_off_it_staffs_nothing()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "false";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);
        Assert.False((await Conductor.StatusAsync()).Enabled);
    }

    [Fact]
    public async Task The_founder_turns_it_on_and_off_and_the_ledger_says_so()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "false";
        var founder = _hub.Founder();

        var on = await (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true)))
            .Content.ReadFromJsonAsync(MuthurJsonContext.Default.ConductorStatusDto);
        Assert.True(on!.Enabled);

        var off = await (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(false)))
            .Content.ReadFromJsonAsync(MuthurJsonContext.Default.ConductorStatusDto);
        Assert.False(off!.Enabled);

        var events = await founder.GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Contains(events!, e => e.Type == "conductor.on");
        Assert.Contains(events!, e => e.Type == "conductor.off");
    }

    [Fact]
    public async Task An_agent_may_not_turn_it_on()
    {
        var agent = await _hub.RegisterAgentAsync("nosy");
        var refused = await agent.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
    }
}
