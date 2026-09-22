using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The conductor staffs validation. What it refuses to staff matters more than what it staffs: a session it
/// starts by mistake spends the founder's subscription on nothing.
/// </summary>
public sealed class ConductorTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new(integrationChecks: true);

    public ConductorTests()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        // These tests isolate the per-process retry/cooldown policy. Durable budgets have separate coverage.
        _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "0";
    }

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
        (await checker.PostActionAsync(id, "fail", new VerdictRequest("win-validator", evidence, SubjectId: (await checker.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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
            (await validator.PostActionAsync(assignment.TaskKey, "fail", new VerdictRequest(assignment.RoleKey, "the export still 500s", SubjectId: (await validator.GetTaskAsync(assignment.TaskKey)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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
                (await checker.PostActionAsync(assignment.TaskKey, "fail", new VerdictRequest(decider, "the export still 500s", SubjectId: (await checker.GetTaskAsync(assignment.TaskKey)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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
            (await checker.PostActionAsync(assignment.TaskKey, "pass", new VerdictRequest(assignment.RoleKey, "Ran the application: expected output observed; reproduce with dotnet test.", SubjectId: (await checker.GetTaskAsync(assignment.TaskKey)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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
            (await checker.PostActionAsync(id, "fail", new VerdictRequest("win-validator", $"broken, round {attempt}: reproduce with dotnet test", SubjectId: (await checker.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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
            await RejectAsync(checker, id, $"broken, round {round}: reproduce with dotnet test");
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
            await RejectAsync(checker, id, $"broken, round {round}: reproduce with dotnet test");
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
            await RejectAsync(checker, id, $"broken, round {round}: reproduce with dotnet test");
            (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        }

        await Conductor.RunPassAsync();
        await Conductor.RunPassAsync();   // the same round says it once, however often the conductor passes
        Assert.Single(await FounderMessagesAsync());

        // Rejected a fourth time, and resubmitted on that same commit again. That is a new round, and the
        // founder hearing about it a second time is the point: nothing has changed, again.
        await RejectAsync(checker, id, "broken, round 4: reproduce with dotnet test");
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
            await RejectAsync(checker, id, $"broken, round {round}: reproduce with dotnet test");
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

        // A staffed session is off the default roster by construction, so this read has to ask for everything.
        var roster = await _hub.CreateClient().GetFromJsonAsync(Routes.Agents + "?all=true", MuthurJsonContext.Default.AgentRosterDto);
        var registered = Assert.Single(roster!.Agents, a => a.Name == "conductor-win-validator-t-1");
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

        var roster = await _hub.CreateClient().GetFromJsonAsync(Routes.Agents + "?all=true", MuthurJsonContext.Default.AgentRosterDto);
        Assert.Single(roster!.Agents, a => a.Name == identity.Name);
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
        // An empty catalog is a configuration failure. Known exhausted accounts now stop before staffing.
        File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile), """{"tiers":{"mastermind":[]}}""");
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

    /// <summary>The founder moving the ceiling, as the CLI sends it.</summary>
    private Task<HttpResponseMessage> SetCeilingAsync(int? sessions = null, string? from = null, string? to = null, int? inWindow = null, bool clear = false) =>
        _hub.Founder().PostAsJsonAsync(Routes.ConductorSessions, new ConductorSessionsRequest(sessions, from, to, inWindow, clear));

    private static async Task<ConductorStatusDto> ReadStatusAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ConductorStatusDto))!;
    }

    [Fact]
    public async Task With_nothing_set_the_ceiling_is_the_number_in_the_configuration_file()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "3";

        var status = await Conductor.StatusAsync();

        Assert.Equal(3, status.Ceiling);
        Assert.Equal(3, status.MaxSessions);
        Assert.Equal("Muthur:ConductorMaxSessions.", status.CeilingReason);
    }

    [Fact]
    public async Task The_number_the_founder_sets_is_the_one_the_pass_obeys()
    {
        // Five pairs it could staff, a configuration file that would allow nine, and a founder who said four.
        // Nobody should have to edit a file and restart the hub to say that.
        _hub.Settings["Muthur:ConductorMaxSessions"] = "9";
        await SetUpAsync("win-validator");
        await DefineAsync(5, "win-validator");
        _hub.Validators.Block = true;   // the sessions are still running when the pass ends, so the ceiling is what counts
        var owner = await _hub.RegisterAgentAsync("owner");
        for (var i = 0; i < 5; i++) await ValidatingTaskAsync(owner, $"Waiting {i}", priority: 5 - i);

        var status = await ReadStatusAsync(await SetCeilingAsync(sessions: 4));

        Assert.Equal(4, status.Ceiling);
        Assert.Equal("Set by the founder.", status.CeilingReason);
        Assert.Equal(9, status.MaxSessions);   // what the file asks for, unchanged, so nothing reading it moves
        Assert.Equal(5, (await Conductor.PlanAsync()).Count);
        Assert.Equal(4, await Conductor.RunPassAsync());
        Assert.Equal(4, _hub.Validators.Started.Count);
        Assert.Equal(4, (await Conductor.StatusAsync()).Ceiling);

        _hub.Validators.Finish(4);
    }

    [Fact]
    public async Task The_ceiling_the_founder_set_outlives_the_process_that_heard_it()
    {
        // The hub is off for hours at a time, so this lives in the database like the on/off switch. A second hub
        // over the same data reads it back through a service instance that never heard the founder say it.
        (await SetCeilingAsync(sessions: 4)).EnsureSuccessStatusCode();

        using var restarted = new HubFactory { DataDir = _hub.DataDir };
        var status = await restarted.Services.GetRequiredService<ConductorService>().StatusAsync();

        Assert.Equal(4, status.Ceiling);
        Assert.Equal("Set by the founder.", status.CeilingReason);
        Assert.Equal(2, status.MaxSessions);   // the configuration default, so the 4 can only have come from Meta
    }

    [Fact]
    public async Task While_the_founder_is_asleep_the_window_is_the_ceiling()
    {
        // Pinned to UTC so "local" is a fact of this test and not of the machine running it, and driven entirely
        // by stepping the fake clock.
        _hub.Clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        (await SetCeilingAsync(sessions: 4)).EnsureSuccessStatusCode();

        var awake = await ReadStatusAsync(await SetCeilingAsync(from: "22:00", to: "07:00", inWindow: 1));
        Assert.Equal(4, awake.Ceiling);                          // noon: the window is shut
        Assert.Equal("Set by the founder.", awake.CeilingReason);

        // 23:00, then 02:00 the next morning: one window, either side of midnight.
        foreach (var step in new[] { TimeSpan.FromHours(11), TimeSpan.FromHours(3) })
        {
            _hub.Clock.Advance(step);
            var asleep = await Conductor.StatusAsync();
            Assert.Equal(1, asleep.Ceiling);
            Assert.Equal("Unattended 22:00-07:00 caps this at 1.", asleep.CeilingReason);
        }

        _hub.Clock.Advance(TimeSpan.FromHours(10));   // noon again, and the founder's own number is back
        var morning = await Conductor.StatusAsync();
        Assert.Equal(4, morning.Ceiling);
        Assert.Equal("Set by the founder.", morning.CeilingReason);
    }

    [Fact]
    public async Task An_open_window_is_a_ceiling_the_pass_keeps_to_and_not_only_a_line_on_a_card()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "9";
        _hub.Clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        await SetUpAsync("win-validator");
        await DefineAsync(3, "win-validator");
        _hub.Validators.Block = true;
        var owner = await _hub.RegisterAgentAsync("owner");
        for (var i = 0; i < 3; i++) await ValidatingTaskAsync(owner, $"Waiting {i}", priority: 3 - i);
        (await SetCeilingAsync(sessions: 4)).EnsureSuccessStatusCode();
        (await SetCeilingAsync(from: "22:00", to: "07:00", inWindow: 1)).EnsureSuccessStatusCode();

        _hub.Clock.Advance(TimeSpan.FromHours(11));   // 23:00

        Assert.Equal(3, (await Conductor.PlanAsync()).Count);
        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Single(_hub.Validators.Started);

        _hub.Validators.Finish();
    }

    [Fact]
    public async Task The_window_is_the_founders_own_night_and_not_UTC()
    {
        // Ten hours east: noon on the hub's clock is ten at night where the founder is asleep.
        _hub.Clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("muthur-test-east", TimeSpan.FromHours(10), "east", "east"));

        var status = await ReadStatusAsync(await SetCeilingAsync(from: "22:00", to: "07:00", inWindow: 1));

        Assert.Equal(1, status.Ceiling);
        Assert.Equal("Unattended 22:00-07:00 caps this at 1.", status.CeilingReason);
    }

    [Fact]
    public async Task Clear_takes_both_numbers_away_and_the_configuration_file_has_its_say_again()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "3";
        _hub.Clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        (await SetCeilingAsync(sessions: 4)).EnsureSuccessStatusCode();
        (await SetCeilingAsync(from: "22:00", to: "07:00", inWindow: 1)).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromHours(11));   // 23:00, so both keys are in force when the clear lands
        Assert.Equal(1, (await Conductor.StatusAsync()).Ceiling);

        var cleared = await ReadStatusAsync(await SetCeilingAsync(clear: true));

        Assert.Equal(3, cleared.Ceiling);
        Assert.Equal("Muthur:ConductorMaxSessions.", cleared.CeilingReason);
        await using var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync();
        Assert.Empty(await db.Meta.Where(e => e.Key == MetaEntry.ConductorSessions || e.Key == MetaEntry.ConductorUnattended).ToListAsync());
        Assert.Contains(await EventsAsync(), e => e.Type == "conductor.ceiling_cleared");
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -3)]
    public async Task A_conductor_allowed_no_sessions_at_all_is_refused(int? sessions, int? inWindow)
    {
        var refused = await SetCeilingAsync(sessions, inWindow is null ? null : "22:00", inWindow is null ? null : "07:00", inWindow);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = await refused.ReadErrorAsync();
        Assert.Equal("sessions_invalid", error.Code);
        Assert.Equal("The conductor needs at least one session to do anything.", error.Message);
    }

    [Theory]
    [InlineData("22:00", null, null)]
    [InlineData(null, "07:00", null)]
    [InlineData(null, null, 1)]
    [InlineData("22:00", "07:00", null)]
    [InlineData("22:00", null, 1)]
    [InlineData(null, "07:00", 1)]
    public async Task Half_an_unattended_window_is_refused_rather_than_guessed(string? from, string? to, int? inWindow)
    {
        var refused = await SetCeilingAsync(from: from, to: to, inWindow: inWindow);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = await refused.ReadErrorAsync();
        Assert.Equal("unattended_incomplete", error.Code);
        Assert.Equal("An unattended window needs --from, --to and --sessions.", error.Message);
    }

    [Theory]
    [InlineData("9am", "07:00")]
    [InlineData("7:00", "07:00")]
    [InlineData("24:00", "07:00")]
    [InlineData("22:60", "07:00")]
    [InlineData("22:00:00", "07:00")]
    [InlineData("22.00", "07:00")]
    [InlineData("22:00", "7am")]
    public async Task A_time_that_is_not_HH_mm_is_refused(string from, string to)
    {
        var refused = await SetCeilingAsync(from: from, to: to, inWindow: 1);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = await refused.ReadErrorAsync();
        Assert.Equal("time_invalid", error.Code);
        Assert.Equal("Times are HH:mm, 24-hour.", error.Message);
    }

    [Fact]
    public async Task A_window_with_no_width_is_no_window()
    {
        // Midnight to midnight would otherwise read as "always" or "never" depending on which way the comparison
        // was written. A founder who means always says so with `conductor sessions`.
        _hub.Clock.SetLocalTimeZone(TimeZoneInfo.Utc);

        var status = await ReadStatusAsync(await SetCeilingAsync(from: "12:00", to: "12:00", inWindow: 1));

        Assert.Equal(2, status.Ceiling);
        Assert.Equal("Muthur:ConductorMaxSessions.", status.CeilingReason);
    }

    [Fact]
    public async Task The_ledger_carries_the_numbers_the_founder_chose()
    {
        (await SetCeilingAsync(sessions: 4)).EnsureSuccessStatusCode();
        (await SetCeilingAsync(from: "22:00", to: "07:00", inWindow: 1)).EnsureSuccessStatusCode();

        var events = await EventsAsync();
        Assert.Equal(4, Assert.Single(events, e => e.Type == "conductor.sessions_set").Payload.GetProperty("sessions").GetInt32());
        var window = Assert.Single(events, e => e.Type == "conductor.unattended_set").Payload;
        Assert.Equal("22:00", window.GetProperty("from").GetString());
        Assert.Equal("07:00", window.GetProperty("to").GetString());
        Assert.Equal(1, window.GetProperty("sessions").GetInt32());
    }

    [Fact]
    public async Task An_agent_may_not_move_the_ceiling()
    {
        var agent = await _hub.RegisterAgentAsync("nosy");

        var refused = await agent.PostAsJsonAsync(Routes.ConductorSessions, new ConductorSessionsRequest(99, null, null, null, Clear: false));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    // ---- the conductor staffs orchestrators too -----------------------------------------------------------------

    /// <summary>The founder turning the half that begins new work on or off.</summary>
    private Task<HttpResponseMessage> OrchestratorsAsync(bool enabled) =>
        _hub.Founder().PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(enabled));

    /// <summary>Staffing backlog tasks, on, in a project whose repository is on disk.</summary>
    private async Task<HttpClient> OrchestratingAsync()
    {
        await SetUpAsync();
        (await OrchestratorsAsync(true)).EnsureSuccessStatusCode();
        return await _hub.RegisterAgentAsync("author");
    }

    [Fact]
    public async Task Orchestrators_are_off_until_the_founder_asks_and_validation_is_unaffected()
    {
        // The whole reason for a third switch. A hub that upgrades into this build has no row for the key, and no
        // row means no: landing this must not spend one session the build before it would not have spent.
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();                          // T-1, waiting on a validator
        var author = await _hub.RegisterAgentAsync("author");
        await author.AddTaskAsync("Nobody has started this");   // T-2, sitting in the backlog

        Assert.Empty(await Conductor.PlanOrchestratorsAsync());
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.Empty(_hub.Orchestrators.Started);
        Assert.Equal("T-1", Assert.Single(_hub.Validators.Started).TaskKey);
    }

    [Fact]
    public async Task Turned_on_it_staffs_one_orchestrator_for_an_unclaimed_backlog_task()
    {
        var author = await OrchestratingAsync();
        var id = (await author.AddTaskAsync("Ship the export")).Id;

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        var started = Assert.Single(_hub.Orchestrators.Started);
        Assert.Equal(id, started.TaskKey);
        Assert.Equal("Ship the export", started.TaskTitle);
        Assert.Equal("demo", started.Project);

        // Who the session acts as, and what it is told. Neither is observable anywhere else, and the prompt is the
        // whole of the brief: a task it cannot name is a task it cannot claim.
        Assert.Equal("orchestrator-t-1", OrchestratorSessionLauncher.IdentityName(id));
        var prompt = OrchestratorSessionLauncher.Prompt(started, new Muthur.Launch.HarnessCandidate("codex", "gpt", "acct"));
        Assert.Contains($"""Claim {id} ("Ship the export")""", prompt, StringComparison.Ordinal);
        Assert.Contains($"muthur task claim {id}", prompt, StringComparison.Ordinal);

        var staffed = Assert.Single(await EventsAsync(), e => e.Type == "conductor.staffing");
        Assert.Equal(id, staffed.TaskId);
        Assert.Equal("#orchestrator", RoleOf(staffed));
    }

    [Fact]
    public async Task An_orchestrator_session_is_off_the_roster_although_its_name_says_nothing_about_the_conductor()
    {
        // What marks a staffed row is the code that staffed it, not the name. This family carries no "conductor-"
        // prefix at all, so anything keyed on the name would leave the expensive half of the staffing on the rail
        // and count none of it. The near-duplicate of the validator test is the point.
        var launcher = ActivatorUtilities.CreateInstance<OrchestratorSessionLauncher>(_hub.Services);

        var identity = await launcher.IdentityFor(new OrchestratorAssignment(1, "T-1", "Ship the export", "demo"),
            new Muthur.Launch.HarnessCandidate("codex", "opus", "chatgpt-subscription"), default);

        Assert.Equal("orchestrator-t-1", identity.Name);
        Assert.DoesNotContain("conductor", identity.Name, StringComparison.Ordinal);

        var client = _hub.CreateClient();
        var standing = await client.GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.AgentRosterDto);
        Assert.DoesNotContain(standing!.Agents, a => a.Name == identity.Name);
        Assert.Equal(1, standing.ConductorHidden);

        var all = await client.GetFromJsonAsync(Routes.Agents + "?all=true", MuthurJsonContext.Default.AgentRosterDto);
        Assert.True(Assert.Single(all!.Agents, a => a.Name == identity.Name).ConductorStaffed);
    }

    [Fact]
    public async Task A_task_somebody_has_claimed_is_not_orchestrated()
    {
        // The expensive mistake: a second mastermind staged onto work another session is already doing. A claim
        // takes the task out of the backlog, and the backlog is the only place this plan looks.
        var author = await OrchestratingAsync();
        var task = await author.AddTaskAsync("Already being worked on");
        (await author.ClaimAsync(task.Id)).EnsureSuccessStatusCode();

        Assert.Empty(await Conductor.PlanOrchestratorsAsync());
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Orchestrators.Started);
    }

    [Fact]
    public async Task A_blocked_task_is_never_orchestrated()
    {
        // It is waiting on the founder. A session started for it would spend three quarters of an hour learning
        // what the task already says, and the conductor must never answer a founder request on their behalf.
        var author = await OrchestratingAsync();
        var task = await author.AddTaskAsync("Needs a decision");
        (await author.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await author.PostAsJsonAsync(Routes.Requests, new AskRequest("Monthly or annual?", task.Id, ["monthly", "annual"]))).EnsureSuccessStatusCode();

        Assert.Empty(await Conductor.PlanOrchestratorsAsync());
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Orchestrators.Started);
    }

    [Fact]
    public async Task A_project_with_no_repository_on_record_is_not_orchestrated()
    {
        // There is nowhere for the session to run, and starting one to find that out costs a session. The API
        // refuses an empty path, so the only way into this state is a hand-edited database - which is exactly the
        // state a guard is for.
        var author = await OrchestratingAsync();
        await author.AddTaskAsync("Nowhere to run it");
        await using (var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
        {
            (await db.Projects.SingleAsync()).RepoPath = "";
            await db.SaveChangesAsync();
        }

        Assert.Empty(await Conductor.PlanOrchestratorsAsync());
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Orchestrators.Started);
    }

    [Fact]
    public async Task Two_backlog_tasks_and_room_for_one_start_one()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "1";
        var author = await OrchestratingAsync();
        _hub.Orchestrators.Block = true;   // the first session is still running when the pass looks at the second
        var urgent = (await author.AddTaskAsync("Urgent", priority: 3)).Id;
        await author.AddTaskAsync("Can wait", priority: 1);

        Assert.Equal(2, (await Conductor.PlanOrchestratorsAsync()).Count);   // both are staffable
        Assert.Equal(1, await Conductor.RunPassAsync());                     // the ceiling is what says no
        Assert.Equal(urgent, Assert.Single(_hub.Orchestrators.Started).TaskKey);

        _hub.Orchestrators.Finish();
        await SettledAsync();
    }

    [Fact]
    public async Task Validation_drains_before_the_conductor_starts_anything_new()
    {
        // One ceiling for both halves, and validation has it first: a task in validating is closer to done than a
        // task in the backlog, and staffing the backlog instead only makes more work to validate later.
        _hub.Settings["Muthur:ConductorMaxSessions"] = "1";
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        (await OrchestratorsAsync(true)).EnsureSuccessStatusCode();
        _hub.Validators.Block = true;
        _hub.Orchestrators.Block = true;
        await TaskInValidationAsync();                       // T-1, waiting on a validator
        var author = await _hub.RegisterAgentAsync("author");
        await author.AddTaskAsync("Not started yet");        // T-2, sitting in the backlog

        Assert.Single(await Conductor.PlanAsync());
        Assert.Single(await Conductor.PlanOrchestratorsAsync());
        Assert.Equal(1, await Conductor.RunPassAsync());

        Assert.Equal("T-1", Assert.Single(_hub.Validators.Started).TaskKey);
        Assert.Empty(_hub.Orchestrators.Started);

        _hub.Validators.Finish();
        await SettledAsync();
    }

    [Fact]
    public async Task One_task_is_never_staffed_by_two_orchestrators_at_once()
    {
        // A session's first act is to claim, so between starting and claiming the task is still in the backlog and
        // the plan would find it again. The running set is what stops the second one.
        var author = await OrchestratingAsync();
        _hub.Orchestrators.Block = true;
        var id = (await author.AddTaskAsync("Only once")).Id;

        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Equal(1, Conductor.RunningCount);

        Assert.Empty(await Conductor.PlanOrchestratorsAsync());
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(id, Assert.Single(_hub.Orchestrators.Started).TaskKey);

        _hub.Orchestrators.Finish();
        await SettledAsync();
    }

    [Fact]
    public async Task An_orchestrator_that_cannot_start_is_retried_but_not_all_night()
    {
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        var author = await OrchestratingAsync();
        await author.AddTaskAsync("Nothing can start it");
        _hub.Orchestrators.Throw = new ValidatorLaunchException("No available mastermind candidate to orchestrate T-1.");

        // A throw must leave the running entry removed, or the task is never staffed again at all.
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();
        Assert.Equal(0, Conductor.RunningCount);

        for (var pass = 0; pass < 4; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }
        Assert.Equal(2, _hub.Orchestrators.Started.Count);   // two attempts, then the cooldown holds it

        // Half-open, like every other pair: whatever was wrong is usually fixed from outside the hub.
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        _hub.Orchestrators.Throw = null;
        _hub.Orchestrators.Claim = async a => (await author.ClaimAsync(a.TaskKey)).EnsureSuccessStatusCode();
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.Equal(3, _hub.Orchestrators.Started.Count);
        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.failed") == 2,
            "two orchestrator launch failures were never recorded");
        Assert.All(events.Where(e => e.Type == "conductor.failed"), e => Assert.Equal("#orchestrator", RoleOf(e)));
        Assert.Single(events, e => e.Type == "conductor.stalled");
    }

    [Fact]
    public async Task A_session_that_ran_and_claimed_nothing_is_not_mistaken_for_progress()
    {
        // The 45-minute nothing. A session that exits cleanly having never claimed leaves the task exactly where
        // the plan found it, so the next pass stages it again - three quarters of an hour at a time, all night.
        // Charged on the counter a validator that reaches no verdict is charged on, and stalled by the same cap.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        var author = await OrchestratingAsync();
        var id = (await author.AddTaskAsync("Nobody claims it")).Id;

        for (var pass = 0; pass < 4; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        Assert.Equal(2, _hub.Orchestrators.Started.Count);
        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 2,
            "two orchestrator sessions that claimed nothing were never recorded");
        Assert.All(events.Where(e => e.Type == "conductor.no_verdict"), e =>
        {
            Assert.Equal(id, e.TaskId);
            Assert.Equal("#orchestrator", RoleOf(e));
        });
        Assert.Single(events, e => e.Type == "conductor.stalled");
    }

    [Fact]
    public async Task A_session_that_claimed_its_task_leaves_no_strike_behind_it()
    {
        var author = await OrchestratingAsync();
        var id = (await author.AddTaskAsync("Claimed on the way in")).Id;
        _hub.Orchestrators.Claim = async a => (await author.ClaimAsync(a.TaskKey)).EnsureSuccessStatusCode();

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        // Claimed, so out of the backlog, so never staffed again and nothing charged against the pass.
        Assert.Equal(TaskState.InProgress, (await author.GetTaskAsync(id)).Task.State);
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Single(_hub.Orchestrators.Started);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type is "conductor.no_verdict" or "conductor.failed" or "conductor.stalled");
    }

    [Fact]
    public async Task An_orchestrators_running_key_is_never_counted_as_a_validators_slot()
    {
        // Both halves share one running set, keyed "T-n/role", and PlanAsync charges every entry in it to the role
        // it names. '#' is the whole of the guarantee that the two key spaces cannot meet: no role key may contain
        // one, so no orchestrator can ever be charged to a role that exists. The skip in PlanAsync makes that
        // local rather than something inferred two files away from RoleKey's pattern - so what this pins is the
        // property both rest on, and the behaviour a founder would lose if either drifted: a role with room for
        // one holder still staffs validation while an orchestrator is running.
        _hub.Settings["Muthur:ConductorMaxSessions"] = "5";
        Assert.False(RoleKey.IsValid("#orchestrator"));
        var refused = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("#orchestrator", "# no\nDrive it."));
        Assert.Equal("invalid_key", (await refused.ReadErrorAsync()).Code);

        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");              // room for exactly one holder
        (await OrchestratorsAsync(true)).EnsureSuccessStatusCode();
        _hub.Validators.Block = true;
        _hub.Orchestrators.Block = true;
        var author = await _hub.RegisterAgentAsync("author");
        var backlog = (await author.AddTaskAsync("Not started yet", priority: 5)).Id;

        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Equal(backlog, Assert.Single(_hub.Orchestrators.Started).TaskKey);
        Assert.Equal(1, Conductor.RunningCount);

        // Work arrives for the validator while that orchestrator is still running. Its slot is free, and stays free.
        var validating = await ValidatingTaskAsync(author, "Waiting on a validator", priority: 1);
        Assert.Equal(validating, Assert.Single(await Conductor.PlanAsync()).TaskKey);
        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Equal(validating, Assert.Single(_hub.Validators.Started).TaskKey);

        _hub.Validators.Finish();
        _hub.Orchestrators.Finish();
        await SettledAsync();
    }

    [Fact]
    public async Task The_decision_to_staff_orchestrators_outlives_the_process_that_heard_it()
    {
        // The hub is off for hours at a time. A founder who turned this on must not find it silently off - and,
        // far more importantly, one who turned it off must not find it silently on.
        await SetUpAsync();
        var author = await _hub.RegisterAgentAsync("author");
        await author.AddTaskAsync("Not started yet");
        var founder = _hub.Founder();
        (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true))).EnsureSuccessStatusCode();
        (await founder.PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true))).EnsureSuccessStatusCode();

        using var restarted = new HubFactory { DataDir = _hub.DataDir };
        var planned = await restarted.Services.GetRequiredService<ConductorService>().PlanOrchestratorsAsync();

        Assert.Equal("T-1", Assert.Single(planned).TaskKey);
    }

    [Fact]
    public async Task The_founder_turns_orchestrators_on_and_off_and_the_ledger_says_so()
    {
        var founder = _hub.Founder();
        (await founder.PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true))).EnsureSuccessStatusCode();
        (await founder.PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true))).EnsureSuccessStatusCode();
        (await founder.PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(false))).EnsureSuccessStatusCode();

        // Said twice, recorded once: the ledger carries decisions, not keystrokes.
        var events = await EventsAsync();
        Assert.Single(events, e => e.Type == "conductor.orchestrators_on");
        Assert.Single(events, e => e.Type == "conductor.orchestrators_off");
    }

    [Fact]
    public async Task An_agent_may_not_turn_orchestrators_on()
    {
        var agent = await _hub.RegisterAgentAsync("nosy");

        var refused = await agent.PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Status_says_which_way_the_most_expensive_switch_was_last_thrown()
    {
        // Without this a founder can only learn whether the hub may start orchestrators by reading the ledger for
        // the last time it changed - which is a question about history standing in for one about state.
        Assert.False((await Conductor.StatusAsync()).Orchestrators);

        var on = await ReadStatusAsync(await OrchestratorsAsync(true));
        Assert.True(on.Orchestrators);

        // And the rest of the card reads exactly as a founder saw it yesterday: this switch is a new line on it,
        // not a change to the numbers beside it.
        Assert.True(on.Enabled);
        Assert.Equal(2, on.MaxSessions);
        Assert.Equal(2, on.Ceiling);
        Assert.Equal("Muthur:ConductorMaxSessions.", on.CeilingReason);

        var off = await ReadStatusAsync(await OrchestratorsAsync(false));
        Assert.False(off.Orchestrators);
    }

    [Fact]
    public async Task The_answer_survives_the_process_that_heard_it_and_is_readable_over_HTTP()
    {
        (await OrchestratorsAsync(true)).EnsureSuccessStatusCode();

        using var restarted = new HubFactory { DataDir = _hub.DataDir };
        var status = await restarted.CreateClient().GetFromJsonAsync(Routes.Conductor, MuthurJsonContext.Default.ConductorStatusDto);

        Assert.True(status!.Orchestrators, "the switch is in the database, not in the process that was told");
    }

    [Fact]
    public async Task A_stalled_orchestrator_is_not_reported_to_the_founder_as_a_validator()
    {
        // The wording of a failure is the whole of what the founder has to go on at three in the morning. Told a
        // validator could not start - for a task no validator has been asked to look at, because nobody has even
        // claimed it - they go looking at validation, which is not where this broke.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        var author = await OrchestratingAsync();
        await author.AddTaskAsync("Nothing can start it");
        _hub.Orchestrators.Throw = new ValidatorLaunchException("No available mastermind candidate to orchestrate T-1.");

        for (var pass = 0; pass < 3; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        var told = Assert.Single(await FounderMessagesAsync());
        Assert.StartsWith(
            "The conductor could not start an orchestrator for T-1 (#orchestrator) 2 times and has stopped trying: " +
            "No available mastermind candidate to orchestrate T-1.",
            told.Body);
        Assert.DoesNotContain("validator", told.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_founder_told_nothing_claimed_the_task_is_not_sent_to_go_and_validate_it()
    {
        // The neighbouring wrong place. "None of them reached a verdict … validate it yourself" is sound advice
        // about validation and nonsense about a task sitting unclaimed in the backlog: what failed is that no
        // session ever started it.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        var author = await OrchestratingAsync();
        await author.AddTaskAsync("Nobody claims it");

        for (var pass = 0; pass < 3; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        var told = Assert.Single(await FounderMessagesAsync());
        Assert.StartsWith(
            "The conductor started 2 orchestrators for T-1 (#orchestrator) and none of them claimed it. They ran " +
            "and exited cleanly, so something is stopping them from starting the task at all rather than failing " +
            "at it. Look at the bus for what they said, then re-spec the task, take it yourself, or raise " +
            "Muthur:ConductorMaxAttempts.",
            told.Body);
        Assert.Contains("retries by itself within", told.Body, StringComparison.Ordinal);   // the half-open probe, on this message too
    }

    // ---- work that has been begun before is not work nobody has started ----------------------------------------

    /// <summary>
    /// A task somebody claimed and specced and then went quiet on. It is left in progress with a live claim:
    /// <see cref="SweptAsync"/> is what lapses it, so a test with two of these can lapse both together.
    /// </summary>
    private async Task<string> AbandonedAsync(HttpClient owner, string title, int priority = 0)
    {
        var task = await owner.AddTaskAsync(title, priority: priority);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        return task.Id;
    }

    /// <summary>
    /// Long enough for every claim to lapse, and then the sweep the hub runs every thirty seconds of its own
    /// accord. This is the only way abandoned work ever reaches the backlog, so it is how these tests get there.
    /// </summary>
    private async Task<int> SweptAsync()
    {
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        return await _hub.Services.GetRequiredService<TaskService>().SweepExpiredClaimsAsync();
    }

    private static string PromptFor(OrchestratorAssignment assignment) =>
        OrchestratorSessionLauncher.Prompt(assignment, new Muthur.Launch.HarnessCandidate("codex", "gpt", "acct"));

    [Fact]
    public async Task A_task_swept_back_to_the_backlog_is_staffed_as_a_resumption_and_told_whose()
    {
        // The whole of this task. `corner` claimed T-1, wrote its spec and died; thirty seconds after the claim
        // lapsed the sweep returned it to the backlog, keeping the spec and clearing the owner. It is now
        // indistinguishable from work nobody has begun - and the session staffed for it was being told to start
        // from nothing, which is how one frozen spec becomes two.
        await OrchestratingAsync();
        var corner = await _hub.RegisterAgentAsync("corner");
        var id = await AbandonedAsync(corner, "Ship the export");
        Assert.Equal(1, await SweptAsync());

        var swept = (await _hub.CreateClient().GetTaskAsync(id)).Task;
        Assert.Equal(TaskState.Backlog, swept.State);
        Assert.Equal($"specs/{id}.md", swept.SpecPath);
        Assert.Null(swept.Owner);   // so the name in the prompt below can only have come from the ledger

        var planned = Assert.Single(await Conductor.PlanOrchestratorsAsync());
        Assert.True(planned.Resuming);
        Assert.Equal("corner", planned.PreviousOwner);

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        var prompt = PromptFor(Assert.Single(_hub.Orchestrators.Started));
        Assert.Contains($"""You are taking over {id} ("Ship the export") from `corner`, whose session stopped.""",
            prompt, StringComparison.Ordinal);
        Assert.Contains("resumption, not a fresh start", prompt, StringComparison.Ordinal);
        Assert.Contains($"    muthur log --task {id}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("take it from claim to landing", prompt, StringComparison.Ordinal);

        // Only the opening changed. A takeover that quietly dropped a rule would be worse than no takeover at all.
        Assert.Contains($"    muthur task claim {id}", prompt, StringComparison.Ordinal);
        Assert.Contains("Exit code 3 on the claim means another session", prompt, StringComparison.Ordinal);
        Assert.Contains("Never push, never merge into the default branch.", prompt, StringComparison.Ordinal);
        Assert.Contains("You own this task and no other.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_nobody_has_ever_begun_is_still_told_to_begin_it()
    {
        // The other half, and the one that must not regress: most backlog tasks really are untouched, and telling
        // a session to continue work that does not exist sends it looking for a branch nobody wrote.
        var author = await OrchestratingAsync();
        var id = (await author.AddTaskAsync("Nobody has begun this")).Id;

        var planned = Assert.Single(await Conductor.PlanOrchestratorsAsync());
        Assert.False(planned.Resuming);
        Assert.Null(planned.PreviousOwner);

        var prompt = PromptFor(planned);
        Assert.Contains($"""Claim {id} ("Nobody has begun this") and take it from claim to landing""", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("taking over", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("muthur log --task", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_branch_with_no_spec_on_record_is_a_resumption_too_and_says_so_without_a_name()
    {
        // A spec and a branch are two independent pieces of evidence that a session has had this task, and the
        // plan needs either. `task implemented` requires a spec, so a branch without one takes a hand-edited
        // database to produce - which is exactly the state a second condition is for. Nothing here was ever
        // claimed, so the ledger has no holder to name, and a resumption nobody can attribute is still a
        // resumption: it must never fall back to being told to start from nothing.
        var author = await OrchestratingAsync();
        var id = (await author.AddTaskAsync("Half built, no spec on record")).Id;
        await using (var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
        {
            (await db.Tasks.SingleAsync()).Branch = $"task/{id}-work";
            await db.SaveChangesAsync();
        }

        var planned = Assert.Single(await Conductor.PlanOrchestratorsAsync());
        Assert.True(planned.Resuming);
        Assert.Null(planned.PreviousOwner);

        var prompt = PromptFor(planned);
        Assert.Contains($"""You are taking over {id} ("Half built, no spec on record") from an earlier session that stopped.""",
            prompt, StringComparison.Ordinal);
        Assert.Contains($"    muthur log --task {id}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("from ``", prompt, StringComparison.Ordinal);

        // This task has a branch and no spec; the one above has a spec and no branch. The opening promises
        // neither in particular, because a resumption told to go and read a branch that was never pushed goes
        // looking for work that does not exist.
        Assert.Contains("the task already has work on it: a frozen spec, a branch, or both.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_the_founder_released_by_hand_still_names_the_session_that_had_it()
    {
        // The 04:39 incident: the founder released `corner`'s tasks themselves rather than waiting for the sweep.
        // `task.released` records the caller, and the caller is the founder, so a plan that read the newest
        // release would tell the next session it was taking over from the founder - who never had it.
        await OrchestratingAsync();
        var corner = await _hub.RegisterAgentAsync("corner");
        var id = await AbandonedAsync(corner, "Handed over by hand");
        (await _hub.Founder().PostActionAsync(id, "release", new ReleaseTaskRequest("handing over"))).EnsureSuccessStatusCode();

        var planned = Assert.Single(await Conductor.PlanOrchestratorsAsync());

        Assert.True(planned.Resuming);
        Assert.Equal("corner", planned.PreviousOwner);
    }

    [Fact]
    public async Task The_founders_priority_outranks_work_already_begun_and_is_only_tied_by_it()
    {
        // The order is intended, and which way round it runs is the whole point. Priority is the only lever a
        // founder has for "this one matters most", so nothing may silently outrank it: the urgent untouched task
        // goes first even though half-built work is waiting. "Resuming beats starting" is real but it is a
        // tiebreaker - among the two tasks the founder ranked equally, the one already carrying work goes first.
        _hub.Settings["Muthur:ConductorMaxSessions"] = "5";
        var author = await OrchestratingAsync();
        var corner = await _hub.RegisterAgentAsync("corner");
        var urgent = (await author.AddTaskAsync("Urgent and untouched", priority: 9)).Id;
        var begun = await AbandonedAsync(corner, "Half done, and not urgent", priority: 1);
        var ordinary = (await author.AddTaskAsync("Ordinary and untouched", priority: 5)).Id;
        var tied = (await author.AddTaskAsync("Tied with it, and half built", priority: 5)).Id;
        await using (var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
        {
            (await db.Tasks.SingleAsync(t => t.Title == "Tied with it, and half built")).Branch = $"task/{tied}-work";
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await SweptAsync());

        var planned = await Conductor.PlanOrchestratorsAsync();

        // Added before `tied` and equal to it on priority, `ordinary` would win on id alone. It carries no work,
        // so it does not - and `begun`, which carries work, still comes last because the founder ranked it last.
        Assert.Equal([urgent, tied, ordinary, begun], planned.Select(a => a.TaskKey));
        Assert.Equal([false, true, false, true], planned.Select(a => a.Resuming));
    }

    [Fact]
    public async Task Three_resumed_sessions_that_claim_nothing_stall_exactly_as_three_fresh_ones_do()
    {
        // Resuming changes what a session is told and nothing else. It is charged on the same counter, stalls on
        // the same cap, and leaves the task in the backlog when it claims nothing - so a task that eats a session
        // every interval all night is stopped whether the sessions were starting it or continuing it.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        await OrchestratingAsync();
        var corner = await _hub.RegisterAgentAsync("corner");
        var id = await AbandonedAsync(corner, "Nobody picks it up again");
        Assert.Equal(1, await SweptAsync());

        for (var pass = 0; pass < 4; pass++)
        {
            await Conductor.RunPassAsync();
            await SettledAsync();
        }

        Assert.Equal(2, _hub.Orchestrators.Started.Count);
        Assert.All(_hub.Orchestrators.Started, a => Assert.True(a.Resuming, "a swept task is a resumption on every attempt"));
        var events = await _hub.EventsWhenAsync(e => e.Count(x => x.Type == "conductor.no_verdict") == 2,
            "two resuming sessions that claimed nothing were never recorded");
        Assert.All(events.Where(e => e.Type == "conductor.no_verdict"), e =>
        {
            Assert.Equal(id, e.TaskId);
            Assert.Equal("#orchestrator", RoleOf(e));
        });
        Assert.Single(events, e => e.Type == "conductor.stalled");
    }

    [Fact]
    public async Task A_resuming_session_that_claims_its_task_is_not_staffed_again()
    {
        // What success looks like: it claimed, so the task left the backlog, so no second session is ever staged
        // onto work somebody is now doing - the collision this half of the conductor exists to avoid.
        var author = await OrchestratingAsync();
        var corner = await _hub.RegisterAgentAsync("corner");
        var id = await AbandonedAsync(corner, "Picked up again");
        Assert.Equal(1, await SweptAsync());
        _hub.Orchestrators.Claim = async a => (await author.ClaimAsync(a.TaskKey)).EnsureSuccessStatusCode();

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.True(Assert.Single(_hub.Orchestrators.Started).Resuming);
        Assert.Equal(TaskState.InProgress, (await author.GetTaskAsync(id)).Task.State);
        Assert.Empty(await Conductor.PlanOrchestratorsAsync());
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.DoesNotContain(await EventsAsync(), e => e.Type is "conductor.no_verdict" or "conductor.stalled");
    }

    // ---- a session must not outlive the hub that started it ------------------------------------------------------

    /// <summary>
    /// A validator session held open, the way a real harness holds one open for its whole timeout. The fake blocks
    /// on the session's own cancellation token, so stopping the hub ends it exactly as killing the process tree does.
    /// </summary>
    private async Task<string> HeldValidatorSessionAsync()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        _hub.Validators.Block = true;
        var id = await TaskInValidationAsync();

        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Equal(1, Conductor.RunningCount);
        return id;
    }

    [Fact]
    public async Task Sessions_die_with_the_hub_that_started_them()
    {
        // The defect validation failed this task on, reproduced on the installed product: a hub staffs a session,
        // `down` then `up`, and the new hub staffs the same task again while the first child is still alive. The
        // child was launched with a token nobody ever cancelled, and _running lives in memory - so the next hub
        // began with an empty set and every reason to believe nothing was running.
        await HeldValidatorSessionAsync();

        await Conductor.StopSessionsAsync();

        // Nothing left running, and the ledger says work was cut off rather than leaving a founder to infer it.
        Assert.Equal(0, Conductor.RunningCount);
        var terminated = Assert.Single(await EventsAsync(), e => e.Type == "conductor.sessions_terminated");
        Assert.Equal(1, terminated.Payload.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task An_orchestrator_does_not_outlive_the_hub_that_started_it()
    {
        // The exact shape the validator hit: orchestrator-t-9 staffed by one hub and again by the next, both
        // children alive, both told to claim T-9. Two orchestrators on one task is two specs and two branches.
        var author = await OrchestratingAsync();
        _hub.Orchestrators.Block = true;
        await author.AddTaskAsync("Only one of these, ever");
        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Equal(1, Conductor.RunningCount);

        await Conductor.StopSessionsAsync();

        Assert.Equal(0, Conductor.RunningCount);
        Assert.Single(await EventsAsync(), e => e.Type == "conductor.sessions_terminated");
        Assert.Single(_hub.Orchestrators.Started);
    }

    [Fact]
    public async Task After_the_sessions_are_stopped_the_task_is_planned_exactly_once_again()
    {
        // The other half of the reproduction. With the child dead, an empty running set is *true*, so the next hub
        // plans the task once - not once more on top of a session that is still going.
        var id = await HeldValidatorSessionAsync();

        await Conductor.StopSessionsAsync();

        Assert.Equal(0, Conductor.RunningCount);
        Assert.Equal(id, Assert.Single(await Conductor.PlanAsync()).TaskKey);
        Assert.Single(await EventsAsync(), e => e.Type == "conductor.staffing");
    }

    [Fact]
    public async Task A_killed_session_is_not_charged_to_the_pair_that_did_nothing_wrong()
    {
        // Cancellation is the hub stopping, not the pair failing. Charged as a launch failure it would leave every
        // session in flight one strike worse off for a restart, and three restarts would stall work that was never
        // given the chance to fail - a hub that punishes you for turning it off and on again.
        _hub.Settings["Muthur:ConductorMaxAttempts"] = "2";
        await HeldValidatorSessionAsync();

        await Conductor.StopSessionsAsync();

        var events = await EventsAsync();
        Assert.DoesNotContain(events, e => e.Type is "conductor.failed" or "conductor.session_failed"
            or "conductor.no_verdict" or "conductor.stalled");
        Assert.Empty(await FounderMessagesAsync());
    }

    [Fact]
    public async Task Once_the_hub_is_stopping_no_further_session_is_started()
    {
        // A pass racing the stop would launch a child with nobody left to cancel it: the orphan this mechanism
        // exists to prevent, created by the mechanism's own shutdown.
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();

        await Conductor.StopSessionsAsync();

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Validators.Started);
    }

    [Fact]
    public async Task A_session_that_finishes_normally_records_nothing_about_being_stopped()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        await TaskInValidationAsync();
        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        Assert.DoesNotContain(await EventsAsync(), e => e.Type == "conductor.sessions_terminated");
    }

    [Fact]
    public async Task A_stop_with_nothing_running_says_nothing_and_a_second_stop_says_nothing_twice()
    {
        // The event means "work was cut off here". A line printed by every clean shutdown would mean nothing.
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");

        await Conductor.StopSessionsAsync();
        await Conductor.StopSessionsAsync();

        Assert.DoesNotContain(await EventsAsync(), e => e.Type == "conductor.sessions_terminated");
    }

    [Theory]
    [InlineData("git push*")]
    [InlineData("git merge*")]
    [InlineData("git rebase*")]
    [InlineData("git checkout main*")]
    [InlineData("git switch main*")]
    [InlineData("gh*")]
    public void No_session_the_conductor_starts_may_push_merge_or_take_the_default_branch(string forbidden)
    {
        // Asserted as a floor rather than as the list itself, so adding a guard passes and removing one fails.
        // Both launchers read this array: it is one boundary, and a second copy of a boundary is how one of them
        // quietly grows a hole while every test still passes.
        Assert.Contains(forbidden, SessionCommands.Denied);
        Assert.Contains("muthur*", SessionCommands.Allowed);   // and it can still talk to the hub
    }

    // ---- and it lands the work nobody is left to land -------------------------------------------------------------

    private static string Branch(string taskId) => $"task/{taskId}-work";

    /// <summary>
    /// A task its owner took all the way to validated. The project requires no validators, so <c>implemented</c>
    /// is the whole round and the state it ends in is the state a passed validation leaves a task in.
    /// </summary>
    /// <param name="integrate">
    /// Whether to supply the passing integration candidate a land needs. Only one candidate may be active, so a
    /// test that validates two tasks integrates the second once the first has been promoted.
    /// </param>
    private async Task<string> ValidatedTaskAsync(HttpClient owner, string title = "Ship the export", int priority = 0, bool integrate = true)
    {
        var task = await owner.AddTaskAsync(title, priority: priority);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(Branch(task.Id), $"{task.Id}.txt", $"{task.Id}\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch(task.Id)))).EnsureSuccessStatusCode();
        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(task.Id)).Task.State);
        if (integrate) await _hub.PassIntegrationAsync(_repo, task.Id);
        return task.Id;
    }

    /// <summary>
    /// Longer than Muthur:AgentStaleSeconds (180) with nobody speaking, on the fake clock. Every call after this
    /// one is the founder's on purpose: any authenticated request an agent makes is a sign of life and would undo it.
    /// </summary>
    private void OwnerGoesQuiet() => _hub.Clock.Advance(TimeSpan.FromMinutes(4));

    [Fact]
    public async Task A_finished_conductor_owner_does_not_wait_for_its_recent_heartbeat_to_expire_before_landing()
    {
        await SetUpAsync();
        var registration = await _hub.Services.GetRequiredService<AgentService>()
            .RegisterConductorSessionAsync(new RegisterAgentRequest("conductor-owner", "codex", "test"));
        var owner = _hub.CreateClient(registration.Token);
        var id = await ValidatedTaskAsync(owner);
        await _hub.Services.GetRequiredService<Ledger>().MutateAsync(Caller.Founder, m =>
        {
            m.Record("conductor.orchestrator_exited", int.Parse(id.AsSpan(2)), new { agent = "conductor-owner" });
            return Task.CompletedTask;
        });
        Assert.Contains((await Conductor.StatusAsync()).Landings!, r => r.Task == id && r.Reason.Contains("Ready"));
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(TaskState.Done, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.Empty((await Conductor.StatusAsync()).Landings!);
    }

    /// <summary>
    /// The default branch checked out, which is the refusal that fires most often on a machine that is also
    /// worked in: exact promotion moves the ref, and will not do so under somebody's working tree. Unlike a
    /// deleted branch it leaves the task branch alone, so a test can move its head while the cause persists.
    /// </summary>
    private void CheckOutMain() => _repo.Git("checkout", "-q", "main");

    private void DetachFromMain() => _repo.Git("checkout", "-q", "--detach");

    /// <summary>A real commit on the task branch: what "fix the cause and push" actually does to the head.</summary>
    private void CommitOnBranch(string branch)
    {
        var standing = _repo.Git("rev-parse", "--abbrev-ref", "HEAD");
        _repo.Git("checkout", "-q", branch);
        _repo.Write("fix.txt", $"fixed at {_repo.Git("rev-parse", "HEAD")}\n");
        _repo.Commit($"{branch}: another go");
        if (standing == "HEAD") _repo.Git("checkout", "-q", "--detach", "main");
        else _repo.Git("checkout", "-q", standing);
    }

    [Fact]
    public async Task A_validated_task_whose_owner_has_gone_is_landed_by_the_hub()
    {
        // T-34's own ending, and the ordinary one: claimed, specced, built, validated - and then the session that
        // would have landed it reached its forty-five minutes and exited. Nothing sweeps `validated` and nothing
        // plans it, so before this it sat there for ever.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        OwnerGoesQuiet();

        Assert.Equal(0, await Conductor.RunPassAsync());   // a land is not a session, so the pass started none

        Assert.Equal(TaskState.Done, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        var events = await EventsAsync();
        Assert.Single(events, e => e.Type == "task.landed" && e.TaskId == id);
        var landed = Assert.Single(events, e => e.Type == "conductor.landed");
        Assert.Equal(id, landed.TaskId);
        Assert.Equal(id, landed.Payload.GetProperty("task").GetString());
        Assert.Equal("owner", landed.Payload.GetProperty("owner").GetString());
        Assert.Equal($"landed {id}, which owner did not survive to land", (await Conductor.StatusAsync()).LastAction);

        // And the merge is really in the repository, not only in the ledger: main now carries the branch's file.
        Assert.Equal($"{id}", _repo.Git("show", $"main:{id}.txt"));
    }

    [Fact]
    public async Task A_validated_task_whose_owner_is_still_alive_is_left_for_its_owner_to_land()
    {
        // Auto-landing is not the policy. A live orchestrator lands its own work exactly as the procedure says,
        // and the hub must never take it out from under one that is about to.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);

        OwnerGoesQuiet();
        (await owner.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest())).EnsureSuccessStatusCode();

        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type is "conductor.landed" or "task.landed");
        Assert.Empty(await FounderMessagesAsync());
    }

    [Fact]
    public async Task A_task_the_conductor_has_a_session_on_is_not_landed_under_it_however_quiet_its_owner()
    {
        // The one that stops the hub racing a session about to land its own work. An orchestrator mid-build is
        // quiet for minutes at a time, and the heartbeat cannot tell that apart from a session that has exited -
        // so what the conductor is running is asked as well, and it is the stronger answer.
        await SetUpAsync();
        (await OrchestratorsAsync(true)).EnsureSuccessStatusCode();
        var session = await _hub.RegisterAgentAsync("orchestrator-t-1");
        var author = await _hub.RegisterAgentAsync("author");
        var id = (await author.AddTaskAsync("Only its own session lands this")).Id;

        // What a real session does: claim, spec, build, mark implemented - and still be running afterwards.
        _hub.Orchestrators.Block = true;
        _hub.Orchestrators.Claim = async assignment =>
        {
            var key = assignment.TaskKey;
            (await session.ClaimAsync(key)).EnsureSuccessStatusCode();
            (await session.PostActionAsync(key, "spec", new SetSpecRequest(_repo.WriteSpec(key)))).EnsureSuccessStatusCode();
            _repo.BranchWithFile(Branch(key), $"{key}.txt", $"{key}\n");
            (await session.PostActionAsync(key, "implemented", new ImplementedRequest(Branch(key)))).EnsureSuccessStatusCode();
            await _hub.PassIntegrationAsync(_repo, key);
        };

        Assert.Equal(1, await Conductor.RunPassAsync());
        await Eventually.TrueAsync(
            async () => (await _hub.Founder().GetTaskAsync(id)).Task.State == TaskState.Validated ? "validated" : null,
            $"{id} never reached validated, so no pass ever saw the state this test is about");

        // Its owner has now been silent for longer than a stale agent may be, and it is still not the hub's to land.
        OwnerGoesQuiet();
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type == "conductor.landed");

        // The session ending is the only thing that changes, and the very next pass lands it.
        _hub.Orchestrators.Finish();
        await SettledAsync();
        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Done, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.Single(await EventsAsync(), e => e.Type == "conductor.landed");
    }

    [Fact]
    public async Task A_validated_task_whose_project_has_no_repository_is_not_landed()
    {
        // There is nothing to merge into, and asking git about it would only produce a refusal to tell the
        // founder about. The API refuses an empty path, so a hand-edited database is the only way in.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        await using (var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
        {
            (await db.Projects.SingleAsync()).RepoPath = "";
            await db.SaveChangesAsync();
        }
        OwnerGoesQuiet();

        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type is "conductor.landed" or "conductor.land_refused");
        Assert.Empty(await FounderMessagesAsync());
    }

    [Fact]
    public async Task A_branch_that_no_longer_merges_goes_back_to_its_owner_and_the_founder_hears_once()
    {
        // The conflict is found where the merge is now made: by integration, not by the land. A candidate tested
        // against a main that has since moved is worth nothing, so the hub lands nothing and says nothing until
        // integration has looked again - and that look is what returns the work to its owner with the files named.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        // main grew a different version of the same file while the branch waited for its verdict.
        _repo.Git("checkout", "-q", "main");
        _repo.Write($"{id}.txt", "main wrote this instead\n");
        _repo.Commit("main moves on");
        _repo.Git("checkout", "-q", "--detach");
        OwnerGoesQuiet();

        Assert.Equal(0, await Conductor.RunPassAsync());

        var founder = _hub.Founder();
        Assert.Equal(TaskState.Validated, (await founder.GetTaskAsync(id)).Task.State);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type is "conductor.landed" or "conductor.land_conflict" or "conductor.land_refused");
        Assert.Empty(await FounderMessagesAsync());
        Assert.Equal($"{id} waits for current integration evidence", (await Conductor.StatusAsync()).LastAction);

        // A candidate invalidated by a moved target is retried on the same cooldown as any other integration retry.
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        var integration = await _hub.RunIntegrationAsync(id);

        Assert.False(integration.Passed);
        Assert.StartsWith("merge_conflict:", integration.Failure);
        var detail = await founder.GetTaskAsync(id);
        Assert.Equal(TaskState.InProgress, detail.Task.State);   // back with its owner, exactly as a bounced land was
        var failed = Assert.Single(detail.Events, e => e.Type == "task.land_failed").Payload;
        Assert.Equal(Branch(id), failed.GetProperty("branch").GetString());
        Assert.Contains($"{id}.txt", failed.GetProperty("files").EnumerateArray().Select(f => f.GetString()));

        // A further pass has nothing validated to land, and still nothing to shout about.
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.DoesNotContain(await EventsAsync(), e => e.Type is "conductor.landed" or "conductor.land_conflict" or "conductor.land_refused");
    }

    [Fact]
    public async Task A_land_the_environment_refuses_leaves_the_task_validated_and_tells_the_founder_once()
    {
        // A validated task whose default branch is checked out in somebody's working tree. Nothing the hub can do
        // about that, and a human has to look - so it stays validated and it is said once, however many passes go by.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        CheckOutMain();
        OwnerGoesQuiet();

        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        var refused = Assert.Single(await EventsAsync(), e => e.Type == "conductor.land_refused");
        Assert.Equal(id, refused.TaskId);
        Assert.Equal("integration_target_checked_out", refused.Payload.GetProperty("code").GetString());
        Assert.Equal(Branch(id), refused.Payload.GetProperty("branch").GetString());
        var told = Assert.Single(await FounderMessagesAsync());
        Assert.Contains("retries at once", told.Body, StringComparison.Ordinal);   // the incantation, as the stalls say it
        Assert.Equal($"could not land {id}: integration_target_checked_out", (await Conductor.StatusAsync()).LastAction);
    }

    [Fact]
    public async Task A_refusal_is_probed_on_a_cooldown_rather_than_retried_every_pass()
    {
        // A refusal leaves the task validated, so an unbounded retry is a pass a minute for ever: task.land_refused
        // and conductor.land_refused some fourteen hundred times a day for a fault only a human can clear. That is
        // the noise this mechanism exists to prevent, moved from the inbox into the ledger - and it is reachable
        // rather than exotic, because main checked out in a working tree refuses every land there is.
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        CheckOutMain();
        OwnerGoesQuiet();

        // Five passes, a minute apart: one attempt, and then silence in the ledger as well as the inbox.
        for (var pass = 0; pass < 5; pass++)
        {
            Assert.Equal(0, await Conductor.RunPassAsync());
            _hub.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var events = await EventsAsync();
        Assert.Equal(1, events.Count(e => e.Type == "conductor.land_refused"));
        Assert.Equal(1, events.Count(e => e.Type == "task.land_refused"));   // the land path was not asked again either
        Assert.Single(await FounderMessagesAsync());

        // Half-open: the cooldown passes, exactly one probe goes through, and failing again re-arms it without
        // shouting a second time.
        _hub.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(2, (await EventsAsync()).Count(e => e.Type == "conductor.land_refused"));
        Assert.Single(await FounderMessagesAsync());

        // The cause is fixed outside the hub, and no clock moves: the head has not, so the cooldown holds and
        // nothing is tried. The next probe finds the way clear and lands it.
        DetachFromMain();
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        _hub.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Done, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.Single(await EventsAsync(), e => e.Type == "conductor.landed");
        Assert.Equal(2, (await EventsAsync()).Count(e => e.Type == "conductor.land_refused"));
    }

    [Fact]
    public async Task A_commit_on_the_branch_invalidates_the_candidate_rather_than_waiting_out_the_cooldown()
    {
        // The defect validation failed this on. The cooldown used to be keyed on the head in the last
        // `task.implemented` event, which moves when somebody calls the CLI again - not when somebody makes a
        // commit. The key is now what git answers - and under the integration gate a moved head is not a fresh
        // land attempt at all: the tested candidate no longer describes the branch, so the task waits for
        // integration rather than for the cooldown, and a fresh round is what lands it.
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        CheckOutMain();   // the refusal that fires most often here, and it leaves the branch alone
        OwnerGoesQuiet();

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal("integration_target_checked_out",
            Assert.Single(await EventsAsync(), e => e.Type == "conductor.land_refused").Payload.GetProperty("code").GetString());

        // Nothing has changed, so nothing is tried: the cooldown holds on an unmoved head.
        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(1, (await EventsAsync()).Count(e => e.Type == "conductor.land_refused"));

        // A real commit on the branch - not a call to `task implemented`, which is what the old key needed.
        // The cause is deliberately still there. The very next pass, with no clock moved, looks again - and
        // finds a candidate that no longer describes the branch, which is a wait for integration, not a refusal.
        CommitOnBranch(Branch(id));

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(1, (await EventsAsync()).Count(e => e.Type == "conductor.land_refused"));
        Assert.Single(await FounderMessagesAsync());
        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.Equal($"{id} waits for current integration evidence", (await Conductor.StatusAsync()).LastAction);

        // A moved implementation needs a new validation subject and a new integration candidate. Freeing the
        // checkout alone cannot authorize the new commit; explicit recovery opens the fresh round.
        DetachFromMain();
        (await owner.PostActionAsync(id, "revalidate", new RevalidateRequest("Review the changed implementation."))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest(Branch(id)))).EnsureSuccessStatusCode();
        await _hub.PassIntegrationAsync(_repo, id);
        OwnerGoesQuiet();
        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Done, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.Single(await EventsAsync(), e => e.Type == "conductor.landed");
    }

    [Fact]
    public async Task Turning_the_conductor_on_again_retries_a_land_the_environment_refused()
    {
        // What the message arming the cooldown tells the founder to do, so it had better be true. The head does
        // not move here - the cause is the checkout, not the branch - so the cooldown is the only thing in the
        // way and the toggle is the only thing that clears it.
        _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        CheckOutMain();
        OwnerGoesQuiet();

        Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Equal(1, (await EventsAsync()).Count(e => e.Type == "conductor.land_refused"));

        DetachFromMain();
        Assert.Equal(0, await Conductor.RunPassAsync());   // fixed, but inside the cooldown: nothing tried
        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);

        var founder = _hub.Founder();
        (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(false))).EnsureSuccessStatusCode();
        (await founder.PostAsJsonAsync(Routes.Conductor, new ConductorSwitch(true))).EnsureSuccessStatusCode();

        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Done, (await founder.GetTaskAsync(id)).Task.State);
        Assert.Single(await EventsAsync(), e => e.Type == "conductor.landed");
    }

    [Fact]
    public async Task Every_orphaned_task_lands_in_one_pass_because_landing_takes_no_session()
    {
        _hub.Settings["Muthur:ConductorMaxSessions"] = "1";
        await SetUpAsync();
        (await OrchestratorsAsync(true)).EnsureSuccessStatusCode();
        var owner = await _hub.RegisterAgentAsync("owner");
        var first = await ValidatedTaskAsync(owner, "First", priority: 2);
        // Integration holds one candidate at a time, so the second is tested once the first has been promoted.
        var second = await ValidatedTaskAsync(owner, "Second", priority: 1, integrate: false);
        DetachFromMain();   // nobody's working tree sits on main, so exact promotion may move it
        var author = await _hub.RegisterAgentAsync("author");
        await author.AddTaskAsync("Something to start");

        // Fill the one slot there is - and while the owner is still alive, nothing lands.
        _hub.Orchestrators.Block = true;
        Assert.Equal(1, await Conductor.RunPassAsync());
        Assert.Equal(1, Conductor.RunningCount);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type == "conductor.landed");

        OwnerGoesQuiet();
        Assert.Equal(0, await Conductor.RunPassAsync());   // no slot left for a session, and a land needs none

        var founder = _hub.Founder();
        Assert.Equal(TaskState.Done, (await founder.GetTaskAsync(first)).Task.State);
        Assert.Equal(TaskState.Validated, (await founder.GetTaskAsync(second)).Task.State);
        Assert.Equal($"{second} waits for current integration evidence", (await Conductor.StatusAsync()).LastAction);

        await _hub.PassIntegrationAsync(_repo, second);
        Assert.Equal(0, await Conductor.RunPassAsync());   // the slot is still taken, and still not needed

        Assert.Equal(TaskState.Done, (await founder.GetTaskAsync(second)).Task.State);
        Assert.Equal([first, second],
            (await EventsAsync()).Where(e => e.Type == "conductor.landed").Select(e => e.TaskId));

        _hub.Orchestrators.Finish();
        await SettledAsync();
    }

    [Fact]
    public async Task With_the_conductor_off_nothing_is_landed()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "false";
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatedTaskAsync(owner);
        OwnerGoesQuiet();

        Assert.Equal(0, await Conductor.RunPassAsync());

        Assert.Equal(TaskState.Validated, (await _hub.Founder().GetTaskAsync(id)).Task.State);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type == "conductor.landed");
    }
}
