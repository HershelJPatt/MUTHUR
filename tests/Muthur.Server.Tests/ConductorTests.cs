using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
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
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec()))).EnsureSuccessStatusCode();
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
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
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

    /// <summary>One rejection: the checker takes the role, votes no, and the task goes back to its owner.</summary>
    private static async Task RejectAsync(HttpClient checker, string id, string evidence)
    {
        (await checker.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        (await checker.PostActionAsync(id, "fail", new VerdictRequest("win-validator", evidence))).EnsureSuccessStatusCode();
        (await checker.PostAsync(Routes.RoleAction("win-validator", "release"), null)).EnsureSuccessStatusCode();
    }

    /// <summary>A real commit on the task branch, so the next `implemented` records a head the cap can tell apart.</summary>
    private void MoveHead(string branch = "task/T-1-feature")
    {
        _repo.Git("checkout", "-q", branch);
        _repo.Write("feature.txt", $"fixed at {_repo.Git("rev-parse", "HEAD")}\n");
        _repo.Commit($"{branch}: another fix");
        _repo.Git("checkout", "-q", "main");
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

    /// <summary>
    /// Both sibling sessions of one round, in the order that makes the defect happen: <paramref name="decider"/>'s
    /// fails the task, which takes it out of validating, and the other returns having recorded nothing only once that
    /// failure has landed. One instance per round — the gate opens once.
    /// </summary>
    private sealed class RoundClosedBy(HttpClient checker, string decider) : IValidatorSessionLauncher
    {
        private readonly TaskCompletionSource _decided = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(ConductorAssignment assignment, CancellationToken ct = default)
        {
            if (assignment.RoleKey != decider)
            {
                // The sibling: it did its work, found nothing left to post, and exited 0.
                await _decided.Task.WaitAsync(Eventually.Budget, ct);
                return;
            }
            try
            {
                (await checker.PostAsync(Routes.RoleAction(decider, "take"), null, ct)).EnsureSuccessStatusCode();
                (await checker.PostActionAsync(assignment.TaskKey, "fail", new VerdictRequest(decider, "the export still 500s"))).EnsureSuccessStatusCode();
                (await checker.PostAsync(Routes.RoleAction(decider, "release"), null, ct)).EnsureSuccessStatusCode();
            }
            finally { _decided.TrySetResult(); }   // never leave the sibling waiting out the budget
        }
    }

    /// <summary>
    /// A session that passes the task and does not return until every validator has, so both siblings classify
    /// themselves against a task that has left validating for <see cref="TaskState.Validated"/>.
    /// </summary>
    private sealed class PassingSession(Func<string, HttpClient> checkerFor) : IValidatorSessionLauncher
    {
        public async Task StartAsync(ConductorAssignment assignment, CancellationToken ct = default)
        {
            var checker = checkerFor(assignment.RoleKey);
            (await checker.PostAsync(Routes.RoleAction(assignment.RoleKey, "take"), null, ct)).EnsureSuccessStatusCode();
            (await checker.PostActionAsync(assignment.TaskKey, "pass", new VerdictRequest(assignment.RoleKey))).EnsureSuccessStatusCode();
            (await checker.PostAsync(Routes.RoleAction(assignment.RoleKey, "release"), null, ct)).EnsureSuccessStatusCode();
            await Eventually.TrueAsync(
                async () => (await checker.GetTaskAsync(assignment.TaskKey)).Task.State == TaskState.Validated ? "validated" : null,
                $"{assignment.TaskKey} never reached validated, so no session ever saw the state this test is about");
        }
    }

    private static string? RoleOf(EventDto recorded) => recorded.Payload.GetProperty("role").GetString();

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

    /// <summary>
    /// The other half of the rule above, and the one a validator failed this task on: a running session that
    /// has taken the role is a hold and a session at once, and counting it in both places charges one validator
    /// two slots. A capacity of two would then staff a second task only while the first session was still
    /// starting up, and serialize for the rest of its life — which is the very serialization this task exists
    /// to remove.
    /// </summary>
    [Fact]
    public async Task A_running_session_that_has_taken_its_role_occupies_one_slot_not_two()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "5";   // so what decides is the capacity, not the budget
        await SetUpAsync("win-validator");
        await DefineAsync(2, "win-validator");
        _hub.Validators.Block = true;   // the first session is still alive when the second task arrives
        var owner = await _hub.RegisterAgentAsync("owner");
        var first = await ValidatingTaskAsync(owner, "First in", priority: 3);

        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Equal(1, Conductor.RunningCount);

        // What the real session does next, under the name the launcher gives the (task, role) pair.
        await ValidatorAsync(ValidatorSessionLauncher.IdentityName(first, "win-validator"), "win-validator");

        // One holder, one running session, one validator: the second slot is free and the next task takes it.
        var second = await ValidatingTaskAsync(owner, "Arrived later", priority: 2);
        var planned = Assert.Single(await Conductor.PlanAsync());
        Assert.Equal(second, planned.TaskKey);
        Assert.Equal(1, await Conductor.RunPassAsync());

        // And the capacity is still a ceiling: with both slots occupied, a third task waits.
        await ValidatorAsync(ValidatorSessionLauncher.IdentityName(second, "win-validator"), "win-validator");
        await ValidatingTaskAsync(owner, "Waits its turn", priority: 1);
        Assert.Empty(await Conductor.PlanAsync());

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
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec()))).EnsureSuccessStatusCode();
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
    public async Task A_task_fixed_on_a_new_commit_is_staffed_again_however_often_it_failed_before()
    {
        // T-13's own history: three rejections on three commits, each finding a different real defect, each
        // fixed before the next attempt. The whole-history count excluded it from unattended validation for
        // good - the loop working exactly as designed, punished as though it were going nowhere.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        for (var round = 1; round <= 3; round++)
        {
            await RejectAsync(checker, id, $"broken, round {round}");
            MoveHead();
            (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        }

        Assert.Equal(3, (await EventsAsync()).Count(e => e.Type == "validation.failed"));
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        var events = await EventsAsync();
        Assert.DoesNotContain(events, e => e.Type == "conductor.exhausted");
        Assert.Single(events, e => e.Type == "conductor.staffing");
        Assert.Empty(await FounderMessagesAsync());
    }

    [Fact]
    public async Task A_task_that_comes_back_on_the_same_commit_is_stopped_and_the_founder_is_told_why()
    {
        // The ceiling the founder asked for: an owner resubmitting the identical commit is the pathology the
        // cap was built for, and it costs one `rev-parse` to tell apart from a task that is converging.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        for (var round = 1; round <= 3; round++)
        {
            await RejectAsync(checker, id, $"broken, round {round}");
            (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        }

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);

        var events = await EventsAsync();
        Assert.Single(events, e => e.Type == "conductor.exhausted");
        Assert.DoesNotContain(events, e => e.Type == "conductor.staffing");
        Assert.StartsWith(
            $"{id} \"Build the feature\" has failed validation 3 times and came back on the same commit, so the " +
            "conductor has stopped restaffing it. Change the branch and mark it implemented again, re-spec it, " +
            "cancel it, or raise Muthur:ConductorMaxAttempts.",
            Assert.Single(await FounderMessagesAsync()).Body);
    }

    [Fact]
    public async Task A_later_resubmission_on_the_same_commit_is_new_information_and_is_announced_again()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        for (var round = 1; round <= 3; round++)
        {
            await RejectAsync(checker, id, $"broken, round {round}");
            (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        }

        await Conductor.RunPassAsync();
        await Conductor.RunPassAsync();   // the same round says it once, however often the conductor passes
        Assert.Single(await FounderMessagesAsync());

        // Rejected a fourth time, and resubmitted on that same commit again. That is a new round, and the
        // founder hearing about it a second time is the point: nothing has changed, again.
        await RejectAsync(checker, id, "broken, round 4");
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        await Conductor.RunPassAsync();

        var messages = await FounderMessagesAsync();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Body.StartsWith($"{id} \"Build the feature\" has failed validation 4 times and came back on the same commit", StringComparison.Ordinal));
        Assert.Equal(2, (await EventsAsync()).Count(e => e.Type == "conductor.exhausted"));
        Assert.Empty(_hub.Validators.Started);
    }

    [Fact]
    public async Task A_round_recorded_before_heads_were_kept_is_staffed_rather_than_stalled_on_the_absence()
    {
        // Every `task.implemented` already in the ledger when this shipped carries no head. A task must never
        // be stopped for evidence nobody wrote down, and T-13 - the task this rule was written for - is
        // exactly that case.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        // Three real rejections on one commit, ending with the task back in progress with its owner.
        for (var round = 1; round <= 3; round++)
        {
            if (round > 1)
                (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
            await RejectAsync(checker, id, $"broken, round {round}");
        }

        // The round before the current one was recorded by the code this task replaces: same event, same
        // moment - the task is in progress, so this is a legal place for one - but no head in the payload.
        // Appending is the only way to produce it, because the ledger refuses to be rewritten and the new
        // code always records a head.
        await using (var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
        {
            db.Events.Add(new LedgerEvent
            {
                At = _hub.Clock.GetUtcNow(),
                Actor = "owner",
                Type = "task.implemented",
                TaskId = (await db.Tasks.SingleAsync()).Id,
                PayloadJson = """{"branch":"task/T-1-feature","spec":"specs/T-1.md","validators":["win-validator"]}""",
            });
            await db.SaveChangesAsync();
        }

        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.DoesNotContain(await EventsAsync(), e => e.Type == "conductor.exhausted");
        Assert.Empty(await FounderMessagesAsync());
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

    [Fact]
    public async Task A_round_another_validator_closed_is_recorded_as_that_and_never_charged_to_the_sibling()
    {
        // Two required validators means two sessions for one round. The first to fail the task takes it out of
        // validating and leaves the sibling's row pending with nothing left to post - a correct ending that the
        // pair's own row cannot tell apart from a session that recorded nothing. Counted as nothing three times,
        // the sibling stalls and the founder is sent to look for a validator that is not broken.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator", "web-validator");
        await DefineAsync("win-validator", "web-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        for (var round = 1; round <= 3; round++)
        {
            _hub.Validators.Delegate = new RoundClosedBy(checker, "win-validator");
            Assert.Equal(2, await Conductor.RunPassAsync());
            await SettledAsync();

            MoveHead();
            (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        }
        _hub.Validators.Delegate = null;

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "validation.failed") == 3,
            "win-validator never decided the three rounds this test is about");

        // The harm, asserted first and whole so a regression prints it: counted as three nothings, the sibling
        // stalls and the founder is told to go looking for a validator that was doing exactly the right thing.
        var stalls = events.Where(e => e.Type == "conductor.stalled").Select(RoleOf).ToList();
        var told = await FounderMessagesAsync();
        Assert.True(stalls.Count == 0 && told.Count == 0,
            $"Three rounds ended correctly under web-validator, and the conductor recorded {stalls.Count} " +
            $"conductor.stalled ({string.Join(", ", stalls)}) and sent the founder {told.Count} message(s): " +
            string.Join(" / ", told.Select(m => m.Body)));

        Assert.DoesNotContain(events, e => e.Type == "conductor.no_verdict");
        Assert.Equal(3, events.Count(e => e.Type == "conductor.round_closed"));
        Assert.All(events.Where(e => e.Type == "conductor.round_closed"), e =>
        {
            Assert.Equal(id, e.TaskId);
            Assert.Equal("web-validator", RoleOf(e));
        });
    }

    [Fact]
    public async Task A_round_that_is_still_open_still_charges_the_session_that_recorded_nothing()
    {
        // The other half of the same fork, and the reason the fix is a classifier rather than a licence: nobody
        // decided this task, so both sessions really did record nothing and both pairs are charged for it.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator", "web-validator");
        await DefineAsync("win-validator", "web-validator");
        await TaskInValidationAsync();

        for (var pass = 0; pass < 6; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        Assert.Equal(6, _hub.Validators.Started.Count);   // three per pair, then both are stalled half-open

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 6,
            "six sessions without a verdict were never recorded");
        Assert.DoesNotContain(events, e => e.Type == "conductor.round_closed");
        Assert.Equal(3, events.Count(e => e.Type == "conductor.no_verdict" && RoleOf(e) == "web-validator"));
        Assert.Equal(2, events.Count(e => e.Type == "conductor.stalled"));
        Assert.Contains(await FounderMessagesAsync(), m => m.Body.StartsWith(
            "The conductor started 3 validators for T-1 (web-validator) and none of them reached a verdict.",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_closed_round_leaves_the_strikes_behind_it_exactly_where_they_were()
    {
        // Neither charged nor credited. Clearing here is the tempting mistake: the session did nothing wrong. But a
        // pair that only ever runs in rounds somebody else closes would be laundered clean without ever reaching a
        // verdict, which is precisely what the no-verdict cap exists to catch. So two strikes, then a closed round,
        // then one more genuine nothing is three - and three is the cap.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator", "web-validator");
        await DefineAsync("win-validator", "web-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        for (var pass = 0; pass < 2; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }
        var charged = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 4,
            "the two rounds nobody decided were never charged to both pairs");
        Assert.Equal(2, charged.Count(e => e.Type == "conductor.no_verdict" && RoleOf(e) == "web-validator"));

        // A round win-validator closes under it. web-validator keeps the two strikes it already had.
        _hub.Validators.Delegate = new RoundClosedBy(checker, "win-validator");
        await Conductor.RunPassAsync();
        await SettledAsync();
        _hub.Validators.Delegate = null;
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        // One further round of genuine nothing, and those surviving strikes take the pair to the cap.
        await Conductor.RunPassAsync();
        await SettledAsync();

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 6,
            "the four rounds nobody decided were never charged to both pairs");
        Assert.Single(events, e => e.Type == "conductor.round_closed");
        Assert.Equal(3, events.Count(e => e.Type == "conductor.no_verdict" && RoleOf(e) == "web-validator"));
        Assert.True(events.Any(e => e.Type == "conductor.stalled" && RoleOf(e) == "web-validator"),
            "web-validator had two strikes, ran in a round win-validator closed under it, and then recorded nothing " +
            "a third time - which is the cap, so it should have stalled. It did not, so the closed round credited " +
            "the pair with a verdict it never reached and cleared strikes it had no evidence to clear.");
        Assert.Single(events, e => e.Type == "conductor.stalled");   // win-validator's verdict cleared its own
    }

    [Fact]
    public async Task A_verdict_still_clears_the_strikes_a_closed_round_would_have_left_alone()
    {
        // The other side of that decision: reaching a verdict is evidence the pair works, and it still clears.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var (owner, id) = await OwnedTaskInValidationAsync();
        var checker = await _hub.RegisterAgentAsync("checker");

        for (var pass = 0; pass < 2; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }
        await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 2,
            "two sessions without a verdict were never recorded");

        _hub.Validators.Delegate = new VerdictSession(checker);
        await Conductor.RunPassAsync();
        await SettledAsync();
        _hub.Validators.Delegate = null;
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        await Conductor.RunPassAsync();
        await SettledAsync();

        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 3,
            "the round after the verdict was never recorded");
        Assert.Equal(1, events.Where(e => e.Type == "conductor.no_verdict").MaxBy(e => e.Seq)!
            .Payload.GetProperty("attempt").GetInt32());
        Assert.DoesNotContain(events, e => e.Type is "conductor.stalled" or "conductor.round_closed");
        Assert.Empty(await FounderMessagesAsync());
    }

    [Fact]
    public async Task Every_validator_passing_reads_as_a_verdict_rather_than_a_round_somebody_closed()
    {
        // The trap in the order of the two checks. A task that reached validated because every validator passed is
        // not validating either, so a classifier that asked about the task's state first would read every passing
        // validator's session as a round closed under it - and the cap would stop seeing real nothings entirely.
        await SetUpAsync("win-validator", "web-validator");
        await DefineAsync("win-validator", "web-validator");
        var id = await TaskInValidationAsync();
        var checkers = new Dictionary<string, HttpClient>
        {
            ["win-validator"] = await _hub.RegisterAgentAsync("win-checker"),
            ["web-validator"] = await _hub.RegisterAgentAsync("web-checker"),
        };
        _hub.Validators.Delegate = new PassingSession(role => checkers[role]);

        Assert.Equal(2, await Conductor.RunPassAsync());
        await SettledAsync();

        var events = await _hub.EventsWhenAsync(e => e.Any(x => x.Type == "task.validated"),
            "the task never passed both validators");
        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.DoesNotContain(events, e => e.Type == "conductor.round_closed");
        Assert.DoesNotContain(events, e => e.Type == "conductor.no_verdict");
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

    /// <summary>
    /// A role key and an agent name are one relationship, not two constants: the conductor's identity is built
    /// out of a key, so the key limit has to leave room for the form. Both limits are read here rather than
    /// written down — a test that restates their numbers passes happily on the day someone changes one of them,
    /// which is the drift it exists to catch.
    /// </summary>
    [Fact]
    public async Task The_longest_role_key_there_can_be_still_makes_an_agent_name_the_hub_accepts()
    {
        // The key limit, asked of the rule instead of quoted from it.
        var length = 1;
        while (length < 1000 && RoleKey.IsValid(new string('a', length + 1))) length++;
        var longest = new string('a', length);
        Assert.Equal(RoleKey.MaxLength, length);    // the number callers budget against is the pattern's own

        // IdentityFor builds the name the tree actually uses and registers it, so the name limit is asked of
        // AgentService the same way: one character too long comes back as invalid_name and fails this test.
        // The sibling test above spells the worst case out at 48; this one derives it, so raising RoleKey's
        // limit without raising the agent-name limit fails here rather than in a staged validator session.
        var identity = await RealLauncher().IdentityFor(Assignment("T-2147483647", longest),
            new Muthur.Launch.HarnessCandidate("codex", "opus", "chatgpt-subscription"), default);

        Assert.Equal($"conductor-{longest}-t-2147483647", identity.Name);
        Assert.NotEmpty(identity.Token);
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
