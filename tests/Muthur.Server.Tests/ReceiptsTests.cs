using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// Receipts counts what the ledger says. The failure worth testing for is not a crash but a plausible wrong
/// number: a session counted twice because two events describe it, or a state whose time quietly stopped being
/// measured because a new event type enters it and the mapping never heard.
/// </summary>
public sealed class ReceiptsTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public ReceiptsTests() => _hub.Settings["Muthur:ConductorEnabled"] = "true";

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();

    private Ledger Ledger => _hub.Services.GetRequiredService<Ledger>();

    private async Task<ReceiptsDto> ReceiptsAsync(int? hours = null)
    {
        var response = await _hub.CreateClient().GetAsync(Routes.Receipts + (hours is { } h ? $"?hours={h}" : ""));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto))!;
    }

    /// <summary>Registers with an account and a tier, which <see cref="HubTestExtensions"/> does not carry.</summary>
    private async Task<HttpClient> RegisterAsync(
        string name, string harness = "claude", string model = "opus", string? tier = null, string? account = null, HttpClient? asAgent = null)
    {
        var response = await (asAgent ?? _hub.CreateClient())
            .PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest(name, harness, model, tier, account));
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse))!;
        return _hub.CreateClient(registered.Token);
    }

    private async Task DefineRoleAsync(string key) =>
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(key, $"# {key}\nDrive it."))).EnsureSuccessStatusCode();

    /// <summary>An owner and a validator who holds the one role the project requires.</summary>
    private async Task<(HttpClient Owner, HttpClient Validator)> CastAsync()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        await DefineRoleAsync("win-validator");
        var owner = await RegisterAsync("owner", "claude", "opus", "deep", "founder@example.com");
        var validator = await RegisterAsync("checker", "codex", "gpt", "cheap", "work@example.com");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        return (owner, validator);
    }

    /// <summary>A claimed task with a spec attached and a branch that exists, ready to be marked implemented.</summary>
    private async Task<string> ImplementableAsync(HttpClient owner, string title = "Build the feature")
    {
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(Branch(task.Id), $"{task.Id}.txt", "work\n");
        return task.Id;
    }

    private static string Branch(string taskId) => $"task/{taskId}-work";

    private static async Task ImplementedAsync(HttpClient owner, string taskId) =>
        (await owner.PostActionAsync(taskId, "implemented", new ImplementedRequest(Branch(taskId)))).EnsureSuccessStatusCode();

    /// <summary>One round trip through validation: the owner submits, the validator says no.</summary>
    private async Task FailOnceAsync(HttpClient owner, HttpClient validator, string taskId)
    {
        (await owner.PostActionAsync(taskId, "implemented", new ImplementedRequest(Branch(taskId)))).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(taskId, "fail", new VerdictRequest("win-validator", "the export still 500s"))).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Staffings written straight to the ledger. That the real conductor's staffings are counted is pinned by
    /// <see cref="A_conductor_session_is_counted_once_and_not_twice"/>; this is about the order rows sort in, and
    /// writing them directly keeps that free of the conductor's own scheduling rules.
    /// </summary>
    private async Task StaffAsync(string taskId, int times)
    {
        Assert.True(Wire.TryParseTaskId(taskId, out var id));
        for (var i = 0; i < times; i++)
            await Ledger.MutateAsync(Caller.Founder, m =>
            {
                m.Record("conductor.staffing", id, new { role = "win-validator" });
                return Task.CompletedTask;
            });
    }

    /// <summary>A pass starts its sessions and returns; what a session records, it records afterwards.</summary>
    private async Task SettledAsync()
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (Conductor.RunningCount > 0 && Environment.TickCount64 < deadline) await Task.Yield();
        Assert.Equal(0, Conductor.RunningCount);
    }

    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The drift guard. <see cref="TaskStateTimeline.Enters"/> is a table of fifteen event types, and a table
    /// drifts: someone adds a sixteenth state-changing event and nobody remembers this file. Replaying the real
    /// ledger after every real transition catches that the first time any test path exercises it — the state the
    /// row is in and the state the replay ends in are the same claim, made two ways.
    /// </summary>
    [Fact]
    public async Task Replaying_a_task_ledger_lands_on_the_state_the_row_is_actually_in()
    {
        var (owner, validator) = await CastAsync();

        var task = await owner.AddTaskAsync("Build the feature");
        await AssertReplayMatchesAsync(task.Id, "added");

        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "claimed");

        var asked = await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("Which way round?", task.Id));
        asked.EnsureSuccessStatusCode();
        var request = (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;
        await AssertReplayMatchesAsync(task.Id, "asked");

        (await _hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("that way"))).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "answered");

        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "spec attached");

        _repo.BranchWithFile(Branch(task.Id), "feature.txt", "feature\n");
        await ImplementedAsync(owner, task.Id);
        await AssertReplayMatchesAsync(task.Id, "implemented");

        (await validator.PostActionAsync(task.Id, "fail", new VerdictRequest("win-validator", "crashes on launch"))).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "failed validation");

        await ImplementedAsync(owner, task.Id);
        await AssertReplayMatchesAsync(task.Id, "implemented again");

        (await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator"))).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "passed validation");

        (await owner.PostAsync(Routes.TaskAction(task.Id, "land"), null)).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "landed");

        // And the same ledger read as receipts: the spend this window paid for ended in a landing.
        var receipt = Assert.Single((await ReceiptsAsync()).Tasks);
        Assert.True(receipt.Landed);
        Assert.Equal(TaskState.Done, receipt.State);
        Assert.Equal(1, receipt.ValidationFailures);
    }

    private async Task AssertReplayMatchesAsync(string taskId, string step)
    {
        var detail = await _hub.Founder().GetTaskAsync(taskId);
        // By Seq, never by At: events written in one mutation share an instant and differ only in order.
        var intervals = TaskStateTimeline.Replay(detail.Events.OrderBy(e => e.Seq).Select(e => (e.Type, e.At)));

        Assert.True(intervals.Count > 0, $"after '{step}' the ledger of {taskId} replayed to no interval at all");
        Assert.True(detail.Task.State == intervals[^1].State,
            $"after '{step}' {taskId} is '{detail.Task.State}' but replaying its ledger ends in '{intervals[^1].State}'. " +
            $"TaskStateTimeline.Enters has no row entering '{detail.Task.State}' for the event that just happened, " +
            "so every second receipts attributes to that state is being charged to the previous one.");
    }

    /// <summary>
    /// Registrations are the sessions. The launcher takes an identity only once the harness is found and its
    /// executable resolved, so one registration means one process really started on that account — and taking the
    /// name again re-issues the token, which is a second session, not a correction of the first.
    /// </summary>
    [Fact]
    public async Task Registrations_are_the_sessions_and_they_group_by_the_identity_they_took()
    {
        var one = await RegisterAsync("one", "claude", "opus", "deep", "founder@example.com");
        await RegisterAsync("two", "codex", "gpt", "cheap", "work@example.com");
        await RegisterAsync("one", "claude", "opus", "deep", "founder@example.com", asAgent: one);

        var receipts = await ReceiptsAsync();

        Assert.Equal(3, receipts.Sessions);
        Assert.Equal(2, receipts.ByAccount.Count);
        var busiest = receipts.ByAccount[0];
        Assert.Equal(2, busiest.Count);   // registered, then re-registered: two identities taken
        Assert.Equal("founder@example.com", busiest.Account);
        Assert.Equal("claude", busiest.Harness);
        Assert.Equal("opus", busiest.Model);
        Assert.Equal("deep", busiest.Tier);
        Assert.Equal(1, receipts.ByAccount[1].Count);
        Assert.Equal("work@example.com", receipts.ByAccount[1].Account);
    }

    /// <summary>
    /// The number this whole unit exists to keep honest. A conductor-started validator session is in the ledger
    /// twice — the <c>conductor.staffing</c> that asked for it, and the <c>agent.registered</c> the session wrote
    /// when it took an identity. They are two views of one session, so receipts reports them side by side and
    /// never sums them. Summing would inflate "sessions" by exactly the amount the conductor is used, which is
    /// the one number a founder deciding whether to let it run overnight is actually looking at.
    /// </summary>
    [Fact]
    public async Task A_conductor_session_is_counted_once_and_not_twice()
    {
        var (owner, validator) = await CastAsync();
        _hub.Validators.Delegate = new RegisteringSession(this);
        var task = await ImplementableAsync(owner);
        await ImplementedAsync(owner, task);
        // The conductor staffs only a role nobody holds, so the validator this test parked on it stands aside.
        (await validator.PostAsync(Routes.RoleAction("win-validator", "release"), null)).EnsureSuccessStatusCode();

        Assert.Equal(1, await Conductor.RunPassAsync());
        await SettledAsync();

        var receipts = await ReceiptsAsync();

        // Three identities were taken: the owner, the validator this test parked on the role, and the one the
        // conductor's session registered for itself. One staffing asked for the last of them.
        Assert.Equal(3, receipts.Sessions);
        Assert.Equal(1, receipts.ConductorSessions);
        // Read them as a founder would: three sessions ran, one of which the conductor started. Not four.
        Assert.Equal(1, receipts.Tasks.Single().ConductorSessions);
    }

    /// <summary>A session that takes an identity the way a real one does: it registers, then votes.</summary>
    private sealed class RegisteringSession(ReceiptsTests test) : IValidatorSessionLauncher
    {
        public async Task StartAsync(ConductorAssignment assignment, CancellationToken ct = default)
        {
            var validator = await test.RegisterAsync("staffed", "codex", "gpt", "cheap", "work@example.com");
            (await validator.PostAsync(Routes.RoleAction(assignment.RoleKey, "take"), null, ct)).EnsureSuccessStatusCode();
            (await validator.PostActionAsync(assignment.TaskKey, "pass", new VerdictRequest(assignment.RoleKey))).EnsureSuccessStatusCode();
        }
    }

    /// <summary>The task that took eleven sessions and failed three times is the row you can see, at the top.</summary>
    [Fact]
    public async Task The_task_that_consumed_the_most_sorts_first()
    {
        var (owner, validator) = await CastAsync();
        var busy = await ImplementableAsync(owner, "The one that took eleven sessions");
        var quiet = await ImplementableAsync(owner, "The one that went through first time");

        await FailOnceAsync(owner, validator, busy);
        await FailOnceAsync(owner, validator, busy);
        await FailOnceAsync(owner, validator, quiet);
        await StaffAsync(busy, 3);
        await StaffAsync(quiet, 1);

        var receipts = await ReceiptsAsync();

        Assert.Equal([busy, quiet], receipts.Tasks.Select(t => t.Task));
        Assert.Equal(3, receipts.Tasks[0].ConductorSessions);
        Assert.Equal(2, receipts.Tasks[0].ValidationFailures);
        Assert.Equal("The one that took eleven sessions", receipts.Tasks[0].Title);
        Assert.Equal(1, receipts.Tasks[1].ConductorSessions);
        Assert.Equal(1, receipts.Tasks[1].ValidationFailures);
        Assert.Equal(4, receipts.ConductorSessions);
        Assert.Equal(2, receipts.Sessions);   // the two agents this test registered; staffings are not sessions
    }

    /// <summary>
    /// The claim in docs/PLAN.md §0 is that validation, not implementation, is the bottleneck. This is the number
    /// that settles it: an hour waiting on a verdict against ten minutes of work, both measured from the same
    /// ledger. Nothing here sleeps — the fake clock is stepped, so the hour is exactly an hour.
    /// </summary>
    [Fact]
    public async Task Time_spent_validating_is_measured_against_time_spent_being_worked()
    {
        var (owner, validator) = await CastAsync();
        var task = await ImplementableAsync(owner);
        // Nothing above moved the clock, so everything before this instant cost zero seconds.
        await ImplementedAsync(owner, task);

        _hub.Clock.Advance(TimeSpan.FromHours(1));
        // An hour is longer than a role lease, which is the point: the validator turns up after the wait.
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(task, "fail", new VerdictRequest("win-validator", "the export still 500s"))).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromMinutes(10));

        var receipts = await ReceiptsAsync();

        // Imposed order, not a dictionary's: in_progress, validating, blocked, backlog, with blocked omitted
        // because no task was ever there. Backlog is present at zero seconds - added and claimed in one instant
        // is a stay of no length, which is a different fact from never having been in the backlog at all.
        Assert.Equal([TaskState.InProgress, TaskState.Validating, TaskState.Backlog], receipts.StateTime.Select(s => s.State));
        var validating = receipts.StateTime.Single(s => s.State == TaskState.Validating);
        Assert.Equal(3600d, validating.Seconds);
        Assert.Equal(3600d, validating.Longest);
        Assert.Equal(task, validating.LongestTask);
        Assert.Equal(1, validating.Tasks);
        Assert.Equal(600d, receipts.StateTime.Single(s => s.State == TaskState.InProgress).Seconds);
        Assert.Equal(0d, receipts.StateTime.Single(s => s.State == TaskState.Backlog).Seconds);

        var row = Assert.Single(receipts.Tasks);
        Assert.Equal(3600d, row.ValidatingSeconds);
        Assert.Equal(600d, row.InProgressSeconds);
        Assert.Equal(TaskState.InProgress, row.State);
        Assert.False(row.Landed);
    }

    /// <summary>The window is the question: what was spent since, not what was ever spent.</summary>
    [Fact]
    public async Task An_hour_window_read_two_hours_later_reports_nothing()
    {
        var (owner, validator) = await CastAsync();
        var task = await ImplementableAsync(owner);
        await ImplementedAsync(owner, task);
        (await validator.PostActionAsync(task, "fail", new VerdictRequest("win-validator", "the export still 500s"))).EnsureSuccessStatusCode();

        _hub.Clock.Advance(TimeSpan.FromHours(2));
        var receipts = await ReceiptsAsync(hours: 1);

        Assert.Empty(receipts.Tasks);
        Assert.Empty(receipts.StateTime);
        Assert.Empty(receipts.ByAccount);
        Assert.Empty(receipts.Runs);
        Assert.Equal(0, receipts.Sessions);
        Assert.Equal(0, receipts.ConductorSessions);
        Assert.Equal(0, receipts.WorkerRuns);
        Assert.Equal(_hub.Clock.GetUtcNow(), receipts.At);
        Assert.Equal(receipts.At - TimeSpan.FromHours(1), receipts.Since);
    }

    /// <summary>A founder typing --hours 100000 wants everything, not an error.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 720)]
    public async Task Hours_outside_the_bounds_is_clamped_rather_than_refused(int asked, int bounded)
    {
        var response = await _hub.CreateClient().GetAsync($"{Routes.Receipts}?hours={asked}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipts = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto))!;
        Assert.Equal(receipts.At - TimeSpan.FromHours(bounded), receipts.Since);
    }

    /// <summary>
    /// Receipts must never be the thing that fails. A payload no serializer produced — a hand-edited row, an older
    /// shape, a bug upstream — costs the dimensions it was carrying and nothing else: the event still happened,
    /// so it is still counted.
    /// </summary>
    [Fact]
    public async Task A_payload_that_does_not_parse_costs_its_own_dimensions_and_nothing_else()
    {
        await Ledger.MutateAsync(Caller.Founder, m =>
        {
            // Not through Record, which would serialize it: the point is JSON that never was.
            m.Db.Events.Add(new LedgerEvent { At = m.Now, Actor = "founder", Type = "agent.registered", PayloadJson = "not json" });
            return Task.CompletedTask;
        });

        var response = await _hub.CreateClient().GetAsync(Routes.Receipts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipts = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto))!;
        Assert.Equal(1, receipts.Sessions);
        var group = Assert.Single(receipts.ByAccount);
        Assert.Equal(1, group.Count);
        Assert.Null(group.Tier);
        Assert.Null(group.Account);
        Assert.Equal("", group.Harness);
        Assert.Equal("", group.Model);
    }

    /// <summary>Which accounts hit their ceiling, how often, and whether one of them still has.</summary>
    [Fact]
    public async Task An_account_that_hit_its_ceiling_is_named_with_how_often_and_until_when()
    {
        var agent = await RegisterAsync("worker", "codex", "gpt", "cheap", "work@example.com");
        var now = _hub.Clock.GetUtcNow();
        (await agent.PostAsJsonAsync(Routes.AccountLimits,
            new AccountLimitRequest("work@example.com", now + TimeSpan.FromMinutes(30)))).EnsureSuccessStatusCode();
        (await agent.PostAsJsonAsync(Routes.AccountLimits,
            new AccountLimitRequest("work@example.com", now + TimeSpan.FromHours(3)))).EnsureSuccessStatusCode();

        var receipts = await ReceiptsAsync();

        var account = Assert.Single(receipts.Accounts);
        Assert.Equal("work@example.com", account.Account);
        Assert.Equal(2, account.Times);
        Assert.Equal(now + TimeSpan.FromHours(3), account.LimitedUntil);
    }

    /// <summary>
    /// Unauthenticated like doctor, and read back with the source-generated context: the AOT CLI has nothing else,
    /// so a type missing from <see cref="MuthurJsonContext"/> must fail here and not in the shipped binary.
    /// </summary>
    [Fact]
    public async Task Receipts_answers_without_a_token_and_round_trips_through_the_generated_context()
    {
        var response = await _hub.CreateClient().GetAsync(Routes.Receipts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipts = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), MuthurJsonContext.Default.ReceiptsDto);
        Assert.NotNull(receipts);
        Assert.Equal(_hub.Clock.GetUtcNow(), receipts.At);
        Assert.Equal(receipts.At - TimeSpan.FromHours(24), receipts.Since);
        Assert.Empty(receipts.Tasks);
        Assert.Empty(receipts.Runs);
    }

    /// <summary>
    /// Founder request #13, settled: <c>costUsd</c> goes on the row that reported it and nowhere else. The second
    /// half of this test is the one that matters a year from now. A total would be incomplete in a biased
    /// direction nobody can correct for — the runs that report no cost are not a random sample, they are every
    /// claude session and every conductor-started validator, which is the heaviest spend the organization has.
    /// A figure that omits the largest category is not a partial answer to "what did this cost"; it is a
    /// confident answer to a different question. So: exactly one cost field in the whole receipts contract, and
    /// the hand that adds a second one fails here rather than in review.
    /// </summary>
    [Fact]
    public async Task Cost_is_on_the_run_that_reported_it_and_on_nothing_else()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await RegisterAsync("owner", "claude", "opus");
        var task = await owner.AddTaskAsync("Build the feature");

        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "cheap", "codex", "gpt", "work@example.com", Branch(task.Id), "unit-a", true, 90, 0.42m,
            InputTokens: 1789, OutputTokens: 346))).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "deep", "claude", "opus", "founder@example.com", Branch(task.Id), "unit-b", false, 30, null))).EnsureSuccessStatusCode();

        var receipts = await ReceiptsAsync();

        // Newest first, and the clock has not moved, so the two runs share an instant: only Seq tells them apart.
        Assert.Equal(["claude/opus", "codex/gpt"], receipts.Runs.Select(r => r.Worker));
        Assert.Null(receipts.Runs[0].CostUsd);   // reports none, and the row names the harness that reports none
        Assert.False(receipts.Runs[0].Success);
        Assert.Equal("unit-b", receipts.Runs[0].Unit);
        Assert.Equal(0.42m, receipts.Runs[1].CostUsd);
        Assert.Equal(90, receipts.Runs[1].Seconds);
        Assert.Null(receipts.Runs[0].InputTokens);
        Assert.Null(receipts.Runs[0].OutputTokens);
        Assert.Equal(1789, receipts.Runs[1].InputTokens);
        Assert.Equal(346, receipts.Runs[1].OutputTokens);
        Assert.Equal("cheap", receipts.Runs[1].Tier);
        Assert.Equal(task.Id, receipts.Runs[1].Task);
        Assert.Equal(2, receipts.WorkerRuns);
        Assert.Equal(120d, receipts.WorkerSeconds);
        Assert.Equal(1, receipts.Tasks.Single().WorkerRunsFailed);

        var costs = CostsIn(typeof(ReceiptsDto)).Order(StringComparer.Ordinal).ToList();
        Assert.True(costs.Count == 1 && costs[0] == $"{nameof(WorkerRunDto)}.{nameof(WorkerRunDto.CostUsd)}",
            "the receipts contract may carry cost on the worker-run row and nowhere else, but it carries it on: " +
            $"{string.Join(", ", costs)}. Founder request #13 settled this: costUsd is exact on the run that " +
            "reported it and biased anywhere it is aggregated, because the runs that report nothing are every " +
            "claude session and every conductor-started validator.");
    }

    /// <summary>
    /// The field the founder refused to let this task skip. A run staffed from another run's plan records which
    /// run that was, so a two-level fan-out is a tree in the ledger and the receipts row can attribute what the
    /// fan-out cost to the work that caused it. The second half is the half that rots quietly: a run an
    /// orchestrator started itself records <em>no</em> <c>parent</c> key, not an explicit null. A key present on
    /// every row would say every run came from somewhere, and then the tree is unreadable again — every run
    /// would have a parent edge, most of them to nothing.
    /// </summary>
    [Fact]
    public async Task A_planned_run_names_the_run_that_planned_it_and_an_unplanned_one_carries_no_parent_key()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await RegisterAsync("owner", "claude", "opus");
        var task = await owner.AddTaskAsync("Build the feature");
        var specialist = $"worker/{task.Id.ToLowerInvariant()}-all-9f1c2e";

        // The specialist an orchestrator staffed directly, and then one of the units it came back planning.
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "mastermind", "claude", "opus", "founder@example.com", specialist, null, true, 300, 1.10m))).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "implementer", "codex", "gpt", "work@example.com", Branch(task.Id), "unit-a", true, 90, 0.42m, specialist))).EnsureSuccessStatusCode();

        var receipts = await ReceiptsAsync();

        // Newest first by Seq, so the planned unit is the row above the run that planned it.
        Assert.Equal(["unit-a", null], receipts.Runs.Select(r => r.Unit));
        Assert.Equal(specialist, receipts.Runs[0].Parent);
        Assert.Null(receipts.Runs[1].Parent);
        // And the fan-out's cost is attributable, which is the whole reason the field exists.
        Assert.Equal(0.42m, receipts.Runs[0].CostUsd);

        var payloads = await PayloadsAsync("worker.finished");
        var planned = JsonDocument.Parse(payloads[1]).RootElement;
        Assert.True(planned.TryGetProperty("parent", out var parent), $"the planned run recorded no parent: {payloads[1]}");
        Assert.Equal(specialist, parent.GetString());

        var unplanned = JsonDocument.Parse(payloads[0]).RootElement;
        Assert.False(unplanned.TryGetProperty("parent", out _),
            "a run an orchestrator started directly recorded a parent key anyway: " + payloads[0] +
            ". Omit the field rather than writing parent: null - every row claiming a parent edge is the same " +
            "as no row having one, and reading the tree back is the point of recording it.");
    }

    /// <summary>The payloads of one event type exactly as the ledger stored them, oldest first.</summary>
    private Task<List<string>> PayloadsAsync(string type) =>
        Ledger.ReadAsync((db, _) => db.Events
            .Where(e => e.Type == type)
            .OrderBy(e => e.Seq)
            .Select(e => e.PayloadJson)
            .ToListAsync());

    /// <summary>Every property of the receipts contract that carries money, named by the record it sits on.</summary>
    private static IEnumerable<string> CostsIn(Type contract)
    {
        foreach (var property in contract.GetProperties())
        {
            if (ElementOf(property.PropertyType) is { } element)
            {
                foreach (var nested in CostsIn(element)) yield return nested;
            }
            else if (LooksLikeMoney(property))
            {
                yield return $"{contract.Name}.{property.Name}";
            }
        }
    }

    private static Type? ElementOf(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? type.GetGenericArguments()[0] : null;

    /// <summary>By type and by name, so neither a decimal called Spent nor a double called TotalCostUsd slips past.</summary>
    private static bool LooksLikeMoney(PropertyInfo property) =>
        property.PropertyType == typeof(decimal) || property.PropertyType == typeof(decimal?) ||
        new[] { "cost", "usd", "price", "money", "spend", "spent", "dollar", "billed" }
            .Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
}
