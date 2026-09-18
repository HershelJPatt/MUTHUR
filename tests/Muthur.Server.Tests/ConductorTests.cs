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

    private Task DefineAsync(params string[] roles) => DefineAsync(1, roles);

    private async Task DefineAsync(int holders, params string[] roles)
    {
        foreach (var role in roles)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(role, $"# {role}\nDrive it.", Holders: holders))).EnsureSuccessStatusCode();
    }

    /// <summary>A task of this owner's, on a branch of its own, waiting in 'validating' at the given priority.</summary>
    private async Task<string> ValidatingTaskAsync(HttpClient owner, string title, int priority)
    {
        var task = await owner.AddTaskAsync(title, priority: priority);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest($"specs/{task.Id}.md"))).EnsureSuccessStatusCode();
        var branch = $"task/{task.Id}-work";
        _repo.BranchWithFile(branch, $"{task.Id}.txt", $"{task.Id}\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return task.Id;
    }

    private async Task<HttpClient> ValidatorAsync(string name, string role)
    {
        var client = await _hub.RegisterAgentAsync(name);
        (await client.PostAsync(Routes.RoleAction(role, "take"), null)).EnsureSuccessStatusCode();
        return client;
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
    public async Task A_role_with_room_for_two_plans_two_of_three_waiting_tasks_in_one_pass()
    {
        // The whole point of the capacity: N tasks no longer wait on one another. The counter has to come down as
        // the plan is built, or three waiting tasks become three sessions for a role with two slots.
        _hub.Settings["Muthur:ConductorMaxSessions"] = "5";   // so what limits the pass is the capacity, not the budget
        await SetUpAsync("win-validator");
        await DefineAsync(2, "win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var urgent = await ValidatingTaskAsync(owner, "Urgent", priority: 3);
        var next = await ValidatingTaskAsync(owner, "Next", priority: 2);
        await ValidatingTaskAsync(owner, "Can wait", priority: 1);

        Assert.Equal([urgent, next], (await Conductor.PlanAsync()).Select(a => a.TaskKey));
        Assert.Equal(2, await Conductor.RunPassAsync());
    }

    [Fact]
    public async Task A_role_with_room_for_one_plans_exactly_one_as_it_always_did()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var urgent = await ValidatingTaskAsync(owner, "Urgent", priority: 3);
        await ValidatingTaskAsync(owner, "Next", priority: 2);
        await ValidatingTaskAsync(owner, "Can wait", priority: 1);

        var assignment = Assert.Single(await Conductor.PlanAsync());
        Assert.Equal(urgent, assignment.TaskKey);
    }

    [Fact]
    public async Task A_pair_a_validator_has_already_claimed_is_not_staffed_again()
    {
        // A free slot is not permission to start a second session on work somebody is already doing.
        await SetUpAsync("win-validator");
        await DefineAsync(2, "win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var checker = await ValidatorAsync("checker", "win-validator");
        var taken = await ValidatingTaskAsync(owner, "Being checked", priority: 3);
        var open = await ValidatingTaskAsync(owner, "Nobody on it", priority: 2);
        (await checker.PostActionAsync(taken, "validate-claim", new ClaimValidationRequest("win-validator"))).EnsureSuccessStatusCode();

        var assignment = Assert.Single(await Conductor.PlanAsync());
        Assert.Equal(open, assignment.TaskKey);
    }

    [Fact]
    public async Task A_role_whose_slots_are_all_held_is_left_alone()
    {
        await SetUpAsync("win-validator");
        await DefineAsync(2, "win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        await ValidatorAsync("one", "win-validator");
        await ValidatorAsync("two", "win-validator");
        await ValidatingTaskAsync(owner, "Waiting", priority: 1);

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);
    }

    [Fact]
    public async Task Sessions_on_their_way_to_a_role_occupy_its_slots_on_the_following_pass()
    {
        // A session that has started but has not yet taken the role holds nothing the database can see. Counting
        // only the holds would plan straight over it on the next tick, and the extra session would start, fail to
        // take the role, and produce nothing.
        _hub.Settings["Muthur:ConductorMaxSessions"] = "5";   // so what says no on the second pass is the capacity
        await SetUpAsync("win-validator");
        await DefineAsync(2, "win-validator");
        _hub.Validators.Block = true;   // the two sessions are still on their way when the next pass runs
        var owner = await _hub.RegisterAgentAsync("owner");
        await ValidatingTaskAsync(owner, "Urgent", priority: 3);
        await ValidatingTaskAsync(owner, "Next", priority: 2);
        await ValidatingTaskAsync(owner, "Can wait", priority: 1);

        Assert.Equal(2, await Conductor.RunPassAsync());

        Assert.Empty(await Conductor.PlanAsync());
        Assert.Equal(0, await Conductor.RunPassAsync());

        _hub.Validators.Finish(2);
    }

    [Fact]
    public async Task The_session_budget_still_caps_a_role_with_slots_to_spare()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "1";
        await SetUpAsync("win-validator");
        await DefineAsync(3, "win-validator");
        _hub.Validators.Block = true;   // hold the one session open across the pass
        var owner = await _hub.RegisterAgentAsync("owner");
        await ValidatingTaskAsync(owner, "First", priority: 3);
        await ValidatingTaskAsync(owner, "Second", priority: 2);

        Assert.Equal(2, (await Conductor.PlanAsync()).Count);   // the role has room for both
        Assert.Equal(1, await Conductor.RunPassAsync());        // the budget is what says no, and it still does
        Assert.Single(_hub.Validators.Started);

        _hub.Validators.Finish();
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
    public async Task What_it_last_did_is_on_the_injected_clock()
    {
        // Assertable only because the timestamp comes from TimeProvider: HubFactory's clock is pinned to 2026-01-01.
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();

        Assert.Null((await Conductor.StatusAsync()).LastPass);
        await Conductor.RunPassAsync();

        var status = await Conductor.StatusAsync();
        Assert.Equal(_hub.Clock.GetUtcNow(), status.LastPass);
        Assert.Equal("staffed T-1 for win-validator", status.LastAction);
    }

    [Fact]
    public async Task A_session_that_cannot_start_is_not_retried_all_night()
    {
        // The neighbouring failure path to a failed verdict, and the one an unattended night hits first:
        // no candidate, no harness installed, no repository. Retrying every interval writes thousands of
        // events while `status` reads perfectly healthy.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        _hub.Validators.Throw = new ValidatorLaunchException("No available mastermind candidate to validate T-1.");

        for (var pass = 0; pass < 6; pass++) await Conductor.RunPassAsync();

        Assert.Equal(3, _hub.Validators.Started.Count);

        var events = await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Equal(3, events!.Count(e => e.Type == "conductor.failed"));
        Assert.Single(events!, e => e.Type == "conductor.stalled");
        Assert.Contains("gave up starting", (await Conductor.StatusAsync()).LastAction);
    }

    [Fact]
    public async Task A_stall_lets_one_probe_through_once_the_cooldown_has_passed()
    {
        // Whatever stopped the launch is usually fixed from outside the hub - a quota cleared, a harness
        // installed. The founder should not have to know an incantation to resume.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        _hub.Validators.Throw = new ValidatorLaunchException("No available mastermind candidate.");

        for (var pass = 0; pass < 5; pass++) await Conductor.RunPassAsync();
        Assert.Equal(3, _hub.Validators.Started.Count);

        // Still stalled a minute later.
        _hub.Clock.Advance(TimeSpan.FromMinutes(1));
        await Conductor.RunPassAsync();
        Assert.Equal(3, _hub.Validators.Started.Count);

        // The founder clears the quota; the conductor finds out by itself.
        _hub.Validators.Throw = null;
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        await Conductor.RunPassAsync();

        Assert.Equal(4, _hub.Validators.Started.Count);
    }

    [Fact]
    public async Task A_probe_that_fails_re_arms_the_cooldown_without_shouting_again()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        _hub.Validators.Throw = new ValidatorLaunchException("still broken");

        for (var pass = 0; pass < 5; pass++) await Conductor.RunPassAsync();
        for (var probe = 0; probe < 3; probe++)
        {
            _hub.Clock.Advance(TimeSpan.FromMinutes(31));
            await Conductor.RunPassAsync();
        }

        Assert.Equal(6, _hub.Validators.Started.Count);   // 3 before the stall, then one probe per cooldown

        var events = await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Single(events!, e => e.Type == "conductor.stalled");   // the founder is told once, not every cooldown
    }

    [Fact]
    public async Task Turning_it_on_again_is_the_founder_saying_try_now()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        _hub.Validators.Throw = new ValidatorLaunchException("No available mastermind candidate.");

        for (var pass = 0; pass < 5; pass++) await Conductor.RunPassAsync();
        Assert.Equal(3, _hub.Validators.Started.Count);

        _hub.Validators.Throw = null;
        var founder = _hub.Founder();
        (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(false))).EnsureSuccessStatusCode();
        (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true))).EnsureSuccessStatusCode();

        Assert.Equal(1, await Conductor.RunPassAsync());
    }

    [Fact]
    public async Task A_session_that_starts_again_clears_the_failures_behind_it()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();

        _hub.Validators.Throw = new ValidatorLaunchException("claude is not installed");
        await Conductor.RunPassAsync();
        await Conductor.RunPassAsync();

        _hub.Validators.Throw = null;   // the harness came back
        await Conductor.RunPassAsync();
        await Conductor.RunPassAsync();

        Assert.Equal(4, _hub.Validators.Started.Count);
        var events = await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.DoesNotContain(events!, e => e.Type == "conductor.stalled");
    }

    [Theory]
    [InlineData(@"{""validator"":""win-validator"",""by"":""checker"",""evidence"":""# Verdict: FAIL\n\nThe panel throws on an empty list.""}", "Verdict: FAIL")]
    [InlineData("""{"validator":"win-validator","by":"checker","evidence":"the export still 500s"}""", "the export still 500s")]
    [InlineData("""{"validator":"win-validator","by":"checker","evidence":null}""", "(no evidence)")]
    [InlineData("""{"validator":"win-validator","by":"checker"}""", "(no evidence)")]
    [InlineData("not json at all", "(no evidence)")]
    public void A_founder_notification_carries_a_line_of_evidence_not_a_report(string payload, string expected) =>
        Assert.Equal(expected, ConductorService.FirstLineOfEvidence(payload));

    [Fact]
    public void A_long_verdict_is_cut_short_rather_than_pasted_whole()
    {
        var evidence = new string('x', 4000);
        var payload = $$"""{"validator":"win-validator","by":"checker","evidence":"{{evidence}}"}""";

        var line = ConductorService.FirstLineOfEvidence(payload);

        Assert.Equal(140, line.Length);
        Assert.EndsWith("…", line);
    }

    [Theory]
    [InlineData("opus", "opus")]
    // A tier entry may leave the model blank to mean "the harness's own default". Registration refuses a blank
    // model - the ledger exists to record what did the work - so the blank needs a word of its own.
    [InlineData("", "default")]
    public async Task A_conductor_agent_registers_for_the_candidate_that_is_about_to_run(string model, string recorded)
    {
        // The factory swaps the real launcher for a fake, so build the real one over the hub's own services.
        var launcher = RealLauncher();

        var identity = await launcher.IdentityFor(Assignment("T-1", "win-validator"),
            new Muthur.Launch.HarnessCandidate("codex", model, "chatgpt-subscription"), default);

        Assert.Equal("conductor-win-validator-t-1", identity.Name);
        Assert.NotEmpty(identity.Token);

        var agents = await _hub.CreateClient().GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.IReadOnlyListAgentDto);
        var registered = Assert.Single(agents!, a => a.Name == "conductor-win-validator-t-1");
        Assert.Equal("codex", registered.Harness);
        Assert.Equal(recorded, registered.Model);
    }

    private static ConductorAssignment Assignment(string task, string role) =>
        new(1, task, $"Build {task}", "muthur", role, AvoidHarness: null);

    [Fact]
    public void Two_sessions_for_one_role_are_two_agents_because_the_pair_is_the_identity()
    {
        // Named per role, both sessions register as one agent: the second registration re-issues the token and the
        // first session runs on a revoked one - unauthorized on every call, while the conductor still counts it.
        Assert.Equal("conductor-win-validator-t-1", ValidatorSessionLauncher.IdentityName("T-1", "win-validator"));
        Assert.NotEqual(ValidatorSessionLauncher.IdentityName("T-1", "win-validator"),
            ValidatorSessionLauncher.IdentityName("T-2", "win-validator"));

        // The same pair keeps its name, so a retry reads as the same actor in the ledger.
        Assert.Equal(ValidatorSessionLauncher.IdentityName("T-1", "win-validator"),
            ValidatorSessionLauncher.IdentityName("T-1", "win-validator"));
    }

    [Fact]
    public void No_identity_is_ever_truncated_so_the_whole_role_key_reaches_the_name()
    {
        // The worst case the hub can produce: the widest role key RoleService accepts, and the widest task id an
        // int can hold. Two earlier rounds squeezed this into 48 characters and both failed validation - the head
        // was cut, and two role keys differing only past the cut became one name, one token, one working session
        // out of two. Nothing is cut now, and the whole of both parts is readable in the result.
        var role = new string('v', 48);
        var name = ValidatorSessionLauncher.IdentityName("T-2147483647", role);

        Assert.Equal($"conductor-{role}-t-2147483647", name);
        Assert.Equal(71, name.Length);
        Assert.True(name.Length <= 80, $"identity must fit the 1-80 agent-name rule, was {name.Length}");
        Assert.Contains(role, name, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_long_role_keys_that_differ_only_in_their_last_character_are_still_two_identities()
    {
        // Round 2's reproduction, kept because the regression is what matters and not the mechanism that failed
        // it: two legal 41-char validator roles were truncated to the same head and given one name, so the first
        // session's 'role take' and 'validate claim' both came back unauthorized while conductor status still said
        // two were running. Untruncated, the difference reaches the name by construction.
        var first = ValidatorSessionLauncher.IdentityName("T-4", new string('v', 40) + "a");
        var second = ValidatorSessionLauncher.IdentityName("T-4", new string('v', 40) + "b");

        Assert.NotEqual(first, second);
        Assert.EndsWith("a-t-4", first, StringComparison.Ordinal);
        Assert.EndsWith("b-t-4", second, StringComparison.Ordinal);

        // Still stable across retries of the same pair, and still keyed to the task.
        Assert.Equal(first, ValidatorSessionLauncher.IdentityName("T-4", new string('v', 40) + "a"));
        Assert.NotEqual(first, ValidatorSessionLauncher.IdentityName("T-5", new string('v', 40) + "a"));
    }

    [Fact]
    public void A_role_key_that_spells_out_another_pairs_task_suffix_does_not_take_its_identity()
    {
        // The only shape an attack on a concatenated name can take: hide another pair's "-t-<id>" inside a role
        // key. Round 3 was the same idea against the digest, and it worked. It cannot work here, and the reason is
        // worth pinning rather than trusting: for 'a-t-1' + T-2 to collide with 'a' + something, that something
        // would have to read "1-t-2", and a task key is "T-" followed by digits - no '-' and no 't' can appear in
        // the run. 'a-t-1' is a perfectly legal role key; it just cannot reach another pair's name.
        var hidden = ValidatorSessionLauncher.IdentityName("T-2", "a-t-1");

        Assert.Equal("conductor-a-t-1-t-2", hidden);
        Assert.NotEqual(hidden, ValidatorSessionLauncher.IdentityName("T-1", "a"));
        Assert.NotEqual(hidden, ValidatorSessionLauncher.IdentityName("T-12", "a"));
        Assert.NotEqual(hidden, ValidatorSessionLauncher.IdentityName("T-2", "a-t-1-t"));
    }

    [Fact]
    public void No_two_pairs_the_hub_accepts_share_an_identity()
    {
        // Round 3 was found by a validator running a hash loop, so this is that search done here and in advance.
        // Every role key over the alphabet that could possibly confuse the parse - 'a', 't', '-' and a digit, the
        // exact characters "-t-<id>" is made of - up to 5 long, crossed with task ids of several digit lengths.
        // A collision anywhere in here is a revoked token in production.
        string[] tasks = ["T-1", "T-2", "T-12", "T-21", "T-121", "T-1121", "T-11", "T-2147483647"];
        var roles = new List<string>();
        for (var length = 1; length <= 5; length++) Extend(roles, "", length);

        var names = new Dictionary<string, (string Task, string Role)>(StringComparer.Ordinal);
        foreach (var role in roles)
            foreach (var task in tasks)
            {
                var name = ValidatorSessionLauncher.IdentityName(task, role);
                Assert.Matches("^[a-z0-9][a-z0-9._-]{0,79}$", name);   // AgentService.NamePattern
                Assert.False(names.TryGetValue(name, out var owner),
                    $"'{name}' is the identity of both ({owner.Task}, {owner.Role}) and ({task}, {role}).");
                names[name] = (task, role);
            }

        Assert.Equal(roles.Count * tasks.Length, names.Count);
        Assert.Equal(1023, roles.Count);

        // Legal role keys only: RoleService.KeyPattern wants an alphanumeric first character.
        static void Extend(List<string> into, string prefix, int remaining)
        {
            if (remaining == 0)
            {
                into.Add(prefix);
                return;
            }
            foreach (var c in prefix.Length == 0 ? "at1" : "at1-") Extend(into, prefix + c, remaining - 1);
        }
    }

    [Fact]
    public async Task The_longest_identity_the_hub_can_produce_is_a_name_the_hub_accepts()
    {
        // Arithmetic in a comment is how the previous two rounds justified themselves, so this one goes through
        // AgentService.RegisterAsync for real. If the 1-80 rule and the worst case ever drift apart, the session
        // fails to register and the conductor stages a validator that cannot authenticate.
        var role = new string('v', 48);

        var identity = await RealLauncher().IdentityFor(Assignment("T-2147483647", role),
            new Muthur.Launch.HarnessCandidate("codex", "opus", "chatgpt-subscription"), default);

        Assert.Equal($"conductor-{role}-t-2147483647", identity.Name);
        Assert.NotEmpty(identity.Token);

        var agents = await _hub.CreateClient().GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.IReadOnlyListAgentDto);
        Assert.Single(agents!, a => a.Name == identity.Name);
    }

    [Fact]
    public async Task A_role_key_outside_what_the_identity_rule_assumes_is_refused_at_definition()
    {
        // IdentityName holds its guarantee over the keys the hub accepts, not over arbitrary strings, and the
        // bound is RoleService.KeyPattern's to hold - a cross-module invariant nothing in ValidatorSessionLauncher
        // can enforce for itself. Nothing tested KeyPattern before this task. 1-48 chars of [a-z0-9-] keeps the
        // worst-case identity at 71 of the 80 an agent name allows, and keeps every character in it legal there.
        // Case is not in the list: Normalize lowercases the key before the pattern ever sees it.
        foreach (var key in new[] { new string('v', 49), "win.validator", "win_validator" })
        {
            var refused = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(key, $"# {key}\nDrive it."));

            Assert.False(refused.IsSuccessStatusCode);
            Assert.Equal("invalid_key", (await refused.ReadErrorAsync()).Code);
        }
    }

    [Fact]
    public async Task Two_concurrent_sessions_for_one_role_do_not_invalidate_each_others_tokens()
    {
        await SetUpAsync("win-validator");
        await DefineAsync(2, "win-validator");
        var launcher = RealLauncher();
        var candidate = new Muthur.Launch.HarnessCandidate("codex", "opus", "chatgpt-subscription");

        var first = await launcher.IdentityFor(Assignment("T-1", "win-validator"), candidate, default);
        var second = await launcher.IdentityFor(Assignment("T-2", "win-validator"), candidate, default);

        // The reproduction, asserted before the names so this test fails on the behaviour and not on a convention:
        // the older session got 'unauthorized' on 'role take' while conductor status still said two were running.
        (await _hub.CreateClient(first.Token).PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        (await _hub.CreateClient(second.Token).PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        Assert.NotEqual(first.Name, second.Name);

        var roles = await _hub.Founder().GetFromJsonAsync(Routes.Roles, MuthurJsonContext.Default.IReadOnlyListRoleDto);
        var role = Assert.Single(roles!, r => r.Key == "win-validator");
        Assert.Equal(2, role.Holders.Count);
        Assert.Contains(role.Holders, h => h.Agent == first.Name);
        Assert.Contains(role.Holders, h => h.Agent == second.Name);
    }

    private static Muthur.Launch.WorkerAttempt Attempt(string harness, bool success, string report, bool started = true) =>
        new(new Muthur.Launch.HarnessCandidate(harness, "opus", "acct"),
            new Muthur.Launch.WorkerOutcome(success, report, RateLimited: false), TimeSpan.Zero, started);

    [Fact]
    public void A_harness_the_build_cannot_run_is_a_launch_failure_not_a_quiet_success()
    {
        // AgentLauncher records "unknown harness" and "not on PATH" as attempts and returns normally. Unexamined,
        // the caller takes the success path, the stall counter clears, and the conductor staffs the same pair
        // every interval forever - the round-2 defect, through the door the README says is closed.
        var attempts = new[]
        {
            Attempt("gemini", false, "Unknown harness 'gemini'.", started: false),
            Attempt("claude", false, "'claude' is not installed or not on PATH.", started: false),
        };

        var thrown = Assert.Throws<ValidatorLaunchException>(() => ValidatorSessionLauncher.EnsureSomethingRan("T-1", attempts));
        Assert.Equal("'claude' is not installed or not on PATH.", thrown.Message);
    }

    [Fact]
    public void A_session_reaped_at_its_timeout_is_not_reported_as_one_that_never_started()
    {
        // It held the role, spent the founder's quota, and hung. Sending them to look for a missing CLI is wrong.
        var attempts = new[] { Attempt("claude", false, "'cmd.exe' timed out after 00:01:00.") };

        var thrown = Assert.Throws<ValidatorSessionException>(() => ValidatorSessionLauncher.EnsureSomethingRan("T-1", attempts));
        Assert.Equal("'cmd.exe' timed out after 00:01:00.", thrown.Message);
    }

    [Fact]
    public void A_harness_that_could_not_start_is_not_blamed_for_the_one_that_ran_and_hung()
    {
        // Codex is not installed, so Claude ran and was reaped. The fault the founder hears is the reaping.
        var attempts = new[]
        {
            Attempt("codex", false, "'codex' is not installed or not on PATH.", started: false),
            Attempt("claude", false, "'cmd.exe' timed out after 00:45:00."),
        };

        var thrown = Assert.Throws<ValidatorSessionException>(() => ValidatorSessionLauncher.EnsureSomethingRan("T-1", attempts));
        Assert.Equal("'cmd.exe' timed out after 00:45:00.", thrown.Message);
    }

    [Fact]
    public void A_session_that_ran_is_not_a_launch_failure_whatever_it_decided()
    {
        // A validator that ran and voted no is the system working. Only "nothing ran" is a launch failure.
        var attempts = new[]
        {
            Attempt("claude", false, "rate limited"),
            Attempt("codex", true, "validated T-1: fail, evidence attached"),
        };

        ValidatorSessionLauncher.EnsureSomethingRan("T-1", attempts);
    }

    [Fact]
    public void No_candidate_reported_anything_at_all_is_still_a_failure() =>
        Assert.Throws<ValidatorLaunchException>(() => ValidatorSessionLauncher.EnsureSomethingRan("T-1", []));

    [Fact]
    public async Task A_run_that_hangs_is_recorded_as_a_session_that_failed_not_a_launch_that_did_not_happen()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        _hub.Validators.Throw = new ValidatorSessionException("'cmd.exe' timed out after 00:45:00.");

        for (var pass = 0; pass < 4; pass++) await Conductor.RunPassAsync();

        var events = await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Equal(2, events!.Count(e => e.Type == "conductor.session_failed"));
        Assert.DoesNotContain(events!, e => e.Type == "conductor.failed");
        Assert.Contains("gave up running", (await Conductor.StatusAsync()).LastAction);
    }

    /// <summary>
    /// The real launcher, not the fake. Every earlier test let itself choose which exception was thrown, so none of
    /// them could see a pre-flight failure being classified as a session that ran.
    /// </summary>
    private ValidatorSessionLauncher RealLauncher() => ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services);

    [Fact]
    public async Task No_candidate_left_is_a_launch_failure_not_a_session_that_ran()
    {
        // The likeliest way this path is hit overnight: the subscription is spent. Telling the founder that three
        // sessions ran and hung sends them to look for the wrong thing entirely.
        await SetUpAsync("win-validator");
        var founder = _hub.Founder();
        foreach (var account in new[] { "claude-subscription", "chatgpt-subscription" })
            (await founder.PostAsJsonAsync(Routes.AccountLimits, new AccountLimitRequest(account, _hub.Clock.GetUtcNow().AddHours(2)))).EnsureSuccessStatusCode();

        var thrown = await Assert.ThrowsAsync<ValidatorLaunchException>(() => RealLauncher().StartAsync(
            new ConductorAssignment(1, "T-1", "a task", "demo", "win-validator", null)));

        Assert.Contains("No available mastermind candidate", thrown.Message);
    }

    [Fact]
    public async Task A_project_whose_repository_is_gone_is_a_launch_failure_too()
    {
        await SetUpAsync("win-validator");

        var thrown = await Assert.ThrowsAsync<ValidatorLaunchException>(() => RealLauncher().StartAsync(
            new ConductorAssignment(1, "T-1", "a task", "no-such-project", "win-validator", null)));

        Assert.Contains("no repository on disk", thrown.Message);
    }

    [Fact]
    public async Task A_launch_failure_from_the_real_launcher_is_worded_as_one()
    {
        // End to end through the conductor, with the launcher the hub actually ships.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        var founder = _hub.Founder();
        foreach (var account in new[] { "claude-subscription", "chatgpt-subscription" })
            (await founder.PostAsJsonAsync(Routes.AccountLimits, new AccountLimitRequest(account, _hub.Clock.GetUtcNow().AddHours(2)))).EnsureSuccessStatusCode();
        _hub.Validators.Delegate = RealLauncher();

        for (var pass = 0; pass < 4; pass++) await Conductor.RunPassAsync();

        var events = await founder.GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Equal(2, events!.Count(e => e.Type == "conductor.failed"));
        Assert.DoesNotContain(events!, e => e.Type == "conductor.session_failed");
        Assert.Contains("gave up starting", (await Conductor.StatusAsync()).LastAction);
    }

    [Fact]
    public async Task An_unexpected_failure_is_called_a_launch_failure_rather_than_a_session()
    {
        // Classified on what is known: something nobody anticipated is likelier to mean the session never began.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "1";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        _hub.Validators.Throw = new InvalidOperationException("something nobody thought of");

        await Conductor.RunPassAsync();

        var events = await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Single(events!, e => e.Type == "conductor.failed");
        Assert.DoesNotContain(events!, e => e.Type == "conductor.session_failed");
    }

    [Fact]
    public async Task The_status_line_reports_the_interval_it_runs_at_not_the_one_that_was_asked_for()
    {
        // The worker floors it at 15s; a founder who sets 5 and is told 5 will misread every timestamp they see.
        _hub.Settings["Muthur:ConductorIntervalSeconds"] = "5";

        Assert.Equal(15, (await Conductor.StatusAsync()).IntervalSeconds);
    }

    [Fact]
    public async Task The_status_line_carries_the_number_a_founder_waiting_on_a_probe_needs()
    {
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "17";

        Assert.Equal(17, (await Conductor.StatusAsync()).StallProbeMinutes);
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
