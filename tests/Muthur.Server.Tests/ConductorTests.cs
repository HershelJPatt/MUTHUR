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

    /// <summary>
    /// A pass starts its sessions and returns; what a session records, it records afterwards. So wait on the thing a
    /// finished session is observable by - the conductor no longer counting it as running - never on a clock.
    /// </summary>
    private async Task SettledAsync()
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (Conductor.RunningCount > 0 && Environment.TickCount64 < deadline) await Task.Yield();
        Assert.Equal(0, Conductor.RunningCount);
    }

    private async Task<IReadOnlyList<EventDto>> EventsAsync() =>
        (await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto))!;

    private async Task<IReadOnlyList<MessageDto>> FounderMessagesAsync() =>
        (await _hub.Founder().GetFromJsonAsync($"{Routes.Messages}?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!;

    /// <summary>A session that does what the conductor hopes for: takes the role, records a verdict, releases it.</summary>
    private sealed class VerdictSession(HttpClient validator) : IValidatorSessionLauncher
    {
        public async Task StartAsync(ConductorAssignment assignment, CancellationToken ct = default)
        {
            (await validator.PostAsync(Routes.RoleAction(assignment.RoleKey, "take"), null, ct)).EnsureSuccessStatusCode();
            (await validator.PostActionAsync(assignment.TaskKey, "fail", new VerdictRequest(assignment.RoleKey, "the export still 500s"))).EnsureSuccessStatusCode();
            (await validator.PostAsync(Routes.RoleAction(assignment.RoleKey, "release"), null, ct)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task A_task_waiting_on_a_role_nobody_holds_gets_a_session()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var id = await TaskInValidationAsync();

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

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
        await SettledAsync();
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
        await SettledAsync();
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
        await SettledAsync();

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

        for (var pass = 0; pass < 6; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        Assert.Equal(3, _hub.Validators.Started.Count);

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.failed") == 3,
            "three launch failures were never recorded");
        Assert.Equal(3, events.Count(e => e.Type == "conductor.failed"));
        Assert.Single(events, e => e.Type == "conductor.stalled");
        Assert.Contains("gave up starting", (await Conductor.StatusAsync()).LastAction);

        // A third kind of nothing joined this one; what the founder is told about this kind did not change.
        Assert.StartsWith(
            "The conductor could not start a validator for T-1 (win-validator) 3 times and has stopped trying: " +
            "No available mastermind candidate to validate T-1.",
            Assert.Single(await FounderMessagesAsync()).Body);
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

        for (var pass = 0; pass < 5; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }
        Assert.Equal(3, _hub.Validators.Started.Count);

        // Still stalled a minute later.
        _hub.Clock.Advance(TimeSpan.FromMinutes(1));
        await Conductor.RunPassAsync();
        Assert.Equal(3, _hub.Validators.Started.Count);

        // The founder clears the quota; the conductor finds out by itself.
        _hub.Validators.Throw = null;
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        await Conductor.RunPassAsync();
        await SettledAsync();

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

        for (var pass = 0; pass < 5; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }
        for (var probe = 0; probe < 3; probe++)
        {
            _hub.Clock.Advance(TimeSpan.FromMinutes(31));
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        Assert.Equal(6, _hub.Validators.Started.Count);   // 3 before the stall, then one probe per cooldown

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.failed") == 6,
            "six launch failures were never recorded");
        Assert.Single(events, e => e.Type == "conductor.stalled");   // told once, not every cooldown
    }

    [Fact]
    public async Task Turning_it_on_again_is_the_founder_saying_try_now()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        _hub.Validators.Throw = new ValidatorLaunchException("No available mastermind candidate.");

        for (var pass = 0; pass < 5; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }
        Assert.Equal(3, _hub.Validators.Started.Count);

        _hub.Validators.Throw = null;
        var founder = _hub.Founder();
        (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(false))).EnsureSuccessStatusCode();
        (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true))).EnsureSuccessStatusCode();

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();
    }

    [Fact]
    public async Task A_session_that_reaches_a_verdict_clears_the_failures_behind_it()
    {
        // Reaching a verdict is the only thing that counts as the pair working, so it is the only thing that clears
        // the count. A pair that recovers must not be left one strike from a stall.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        _hub.Validators.Throw = new ValidatorLaunchException("claude is not installed");
        await Conductor.RunPassAsync();
        await SettledAsync();
        await Conductor.RunPassAsync();
        await SettledAsync();

        _hub.Validators.Throw = null;         // the harness came back, and this session votes
        _hub.Validators.Delegate = new VerdictSession(checker);
        await Conductor.RunPassAsync();
        await SettledAsync();
        _hub.Validators.Delegate = null;

        // The verdict sent it back to its owner; they resubmit, and the count starts from one rather than from three.
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        await Conductor.RunPassAsync();
        await SettledAsync();

        Assert.Equal(4, _hub.Validators.Started.Count);
        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.failed") == 2,
            "two launch failures were never recorded");
        Assert.DoesNotContain(events, e => e.Type == "conductor.stalled");
    }

    [Fact]
    public async Task A_session_that_ends_without_a_verdict_is_not_mistaken_for_progress()
    {
        // The incident this cap exists for: the session ran, refused correctly, released the role and exited 0.
        // Nothing was recorded, so nothing counted, so the conductor staffed it again. And again.
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var id = await TaskInValidationAsync();

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 1,
            "the session that reached no verdict was never recorded");
        var recorded = Assert.Single(events, e => e.Type == "conductor.no_verdict");
        Assert.Equal(id, recorded.TaskId);
        Assert.Equal("win-validator", recorded.Payload.GetProperty("role").GetString());
        Assert.Equal(1, recorded.Payload.GetProperty("attempt").GetInt32());
        Assert.DoesNotContain(events, e => e.Type is "conductor.failed" or "conductor.session_failed");
    }

    [Fact]
    public async Task After_three_sessions_that_reach_no_verdict_the_pair_stops_and_the_founder_is_told_once()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();

        for (var pass = 0; pass < 6; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        Assert.Equal(3, _hub.Validators.Started.Count);

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 3,
            "three sessions without a verdict were never recorded");
        Assert.Equal(3, events.Count(e => e.Type == "conductor.no_verdict"));
        Assert.Single(events, e => e.Type == "conductor.stalled");

        var told = Assert.Single(await FounderMessagesAsync(), m => m.Body.Contains("reached a verdict"));
        Assert.StartsWith(
            "The conductor started 3 validators for T-1 (win-validator) and none of them reached a verdict. " +
            "They ran and exited cleanly, so something is stopping them from validating at all rather than failing. " +
            "Look at the bus for what they said, then re-spec the task, validate it yourself, or raise Muthur:ConductorMaxAttempts.",
            told.Body);
        Assert.Contains("retries by itself within", told.Body);   // the half-open probe, on this message too
        Assert.Equal("gave up on win-validator for T-1: 3 sessions, no verdict", (await Conductor.StatusAsync()).LastAction);
    }

    [Fact]
    public async Task A_pair_that_reached_no_verdict_is_left_alone_until_the_cooldown_has_passed()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();

        for (var pass = 0; pass < 4; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }
        Assert.Equal(2, _hub.Validators.Started.Count);

        _hub.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(2, _hub.Validators.Started.Count);

        // Half-open, like the other two: the spec may have been rewritten while it waited.
        _hub.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.Equal(3, _hub.Validators.Started.Count);
        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 3,
            "three sessions without a verdict were never recorded");
        Assert.Single(events, e => e.Type == "conductor.stalled");   // still told once, not per probe
    }

    [Theory]
    [InlineData(@"{""validator"":""win-validator"",""by"":""checker"",""evidence"":""# Verdict: FAIL\n\nThe panel throws on an empty list.""}", "Verdict: FAIL")]
    [InlineData("""{"validator":"win-validator","by":"checker","evidence":"the export still 500s"}""", "the export still 500s")]
    [InlineData("""{"validator":"win-validator","by":"checker","evidence":null}""", "(no evidence)")]
    [InlineData("""{"validator":"win-validator","by":"checker"}""", "(no evidence)")]
    [InlineData("not json at all", "(no evidence)")]
    public void A_founder_notification_carries_a_line_of_evidence_not_a_report(string payload, string expected) =>
        Assert.Equal(expected, Evidence.FirstLineOfEvidence(payload));

    [Fact]
    public void A_long_verdict_is_cut_short_rather_than_pasted_whole()
    {
        var evidence = new string('x', 4000);
        var payload = $$"""{"validator":"win-validator","by":"checker","evidence":"{{evidence}}"}""";

        var line = Evidence.FirstLineOfEvidence(payload);

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
        var launcher = ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services);

        var identity = await launcher.IdentityFor("win-validator", new Muthur.Launch.HarnessCandidate("codex", model, "chatgpt-subscription"), default);

        Assert.Equal("conductor-win-validator", identity.Name);
        Assert.NotEmpty(identity.Token);

        var agents = await _hub.CreateClient().GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.IReadOnlyListAgentDto);
        var registered = Assert.Single(agents!, a => a.Name == "conductor-win-validator");
        Assert.Equal("codex", registered.Harness);
        Assert.Equal(recorded, registered.Model);
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

        for (var pass = 0; pass < 4; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.session_failed") == 2,
            "two session failures were never recorded");
        Assert.Equal(2, events.Count(e => e.Type == "conductor.session_failed"));
        Assert.DoesNotContain(events, e => e.Type == "conductor.failed");
        Assert.DoesNotContain(events, e => e.Type == "conductor.no_verdict");   // it threw; it is not the quiet kind
        Assert.Contains("gave up running", (await Conductor.StatusAsync()).LastAction);

        Assert.StartsWith(
            "The conductor started a validator for T-1 (win-validator) 2 times and none of them finished: " +
            "'cmd.exe' timed out after 00:45:00.",
            Assert.Single(await FounderMessagesAsync()).Body);
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

        for (var pass = 0; pass < 4; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.failed") == 2,
            "two launch failures were never recorded");
        Assert.Equal(2, events.Count(e => e.Type == "conductor.failed"));
        Assert.DoesNotContain(events, e => e.Type == "conductor.session_failed");
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
        await SettledAsync();

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.failed") == 1,
            "the launch failure was never recorded");
        Assert.Single(events, e => e.Type == "conductor.failed");
        Assert.DoesNotContain(events, e => e.Type == "conductor.session_failed");
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
