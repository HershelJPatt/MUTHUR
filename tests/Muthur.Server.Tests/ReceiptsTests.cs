using System.Net;
using System.Net.Http.Json;
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
    private readonly TestRepo _repo = new(integrationChecks: true);

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
        (await validator.PostActionAsync(taskId, "fail", new VerdictRequest("win-validator", "the export still 500s", SubjectId: (await validator.GetTaskAsync(taskId)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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

        (await validator.PostActionAsync(task.Id, "fail", new VerdictRequest("win-validator", "crashes on launch: reproduce by launching the application", SubjectId: (await validator.GetTaskAsync(task.Id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "failed validation");

        await ImplementedAsync(owner, task.Id);
        await AssertReplayMatchesAsync(task.Id, "implemented again");

        (await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", "Ran the application: expected output observed; reproduce with dotnet test.", SubjectId: (await validator.GetTaskAsync(task.Id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
        await AssertReplayMatchesAsync(task.Id, "passed validation");

        await _hub.PassIntegrationAsync(_repo, task.Id);
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
            (await validator.PostActionAsync(assignment.TaskKey, "pass", new VerdictRequest(assignment.RoleKey, "Ran the application: expected output observed; reproduce with dotnet test.", SubjectId: (await validator.GetTaskAsync(assignment.TaskKey)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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
        (await validator.PostActionAsync(task, "fail", new VerdictRequest("win-validator", "the export still 500s", SubjectId: (await validator.GetTaskAsync(task)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
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
        (await validator.PostActionAsync(task, "fail", new VerdictRequest("win-validator", "the export still 500s", SubjectId: (await validator.GetTaskAsync(task)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();

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
    /// A finished conductor session written straight to the ledger, in the shape <c>SessionReceipts</c> records.
    /// That the real conductor writes one per attempt is pinned in <see cref="SessionReceiptsTests"/>; this is
    /// about what receipts make of the row, so the row is written directly.
    /// </summary>
    private async Task SessionAsync(string taskId, string role, string harness, string model, int seconds,
        decimal? costUsd = null, int? inputTokens = null, int? outputTokens = null, int? cacheReadTokens = null, int? totalTokens = null,
        string? failureKind = null, bool started = true)
    {
        Assert.True(Wire.TryParseTaskId(taskId, out var id));
        await Ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("conductor.session_finished", id, new
            {
                role, harness, model, account = "work@example.com", seconds, costUsd, inputTokens, outputTokens, cacheReadTokens, totalTokens,
                failureKind, exitCode = failureKind is null ? 0 : (int?)null, started, runId = "abc123",
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Cost is summed, and the sum is never shown alone. The rows that report no cost are not a random sample —
    /// on the live hub they were every claude session and every conductor-started validator — so a total without
    /// its coverage is a confident answer to a different question. The pair beside it says how many rows are in
    /// the sum and how many are not, and the same rule prices a task: what its rows reported, or nothing at all.
    /// </summary>
    [Fact]
    public async Task Cost_is_summed_with_the_coverage_that_says_how_much_of_the_window_is_priced()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await RegisterAsync("owner", "claude", "opus");
        var task = await owner.AddTaskAsync("Build the feature");

        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "cheap", "codex", "gpt", "work@example.com", Branch(task.Id), "unit-a", true, 90, 0.42m,
            InputTokens: 1789, OutputTokens: 346, CacheReadTokens: 500))).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "deep", "claude", "opus", "founder@example.com", Branch(task.Id), "unit-b", false, 30, null))).EnsureSuccessStatusCode();
        // Two sessions and a launch that never started: an orchestrator that priced itself, a validator that hit
        // the wall clock and reported only one token figure, and a quota refusal that cost four seconds.
        await SessionAsync(task.Id, "#orchestrator", "claude", "opus", 34, 0.31m, 1200, 340, cacheReadTokens: 9000);
        await SessionAsync(task.Id, "win-validator", "codex", "gpt", 2700, totalTokens: 66882, failureKind: "timeout");
        await SessionAsync(task.Id, "win-validator", "codex", "gpt", 4, failureKind: "quota", started: false);

        var receipts = await ReceiptsAsync();

        // The total, and the pair that says two of five rows are in it.
        Assert.Equal(0.73m, receipts.CostUsd);
        Assert.Equal(2, receipts.CostReportedRuns);
        Assert.Equal(3, receipts.CostUnreportedRuns);
        // Tokens: input plus output where a row split them, the one total where it did not; cache reads never.
        Assert.Equal(1789 + 346 + 1200 + 340 + 66882, receipts.Tokens);

        // Session rows, newest first, each naming its harness so a blank cost reads as the harness and not a bug.
        var sessions = Assert.IsAssignableFrom<IReadOnlyList<SessionRunDto>>(receipts.SessionRuns);
        Assert.Equal(["quota", "timeout", null], sessions.Select(s => s.FailureKind));
        Assert.False(sessions[0].Started);
        Assert.Equal(66882, sessions[1].TotalTokens);
        Assert.Null(sessions[1].CostUsd);
        Assert.Equal("#orchestrator", sessions[2].Role);
        Assert.Equal(0.31m, sessions[2].CostUsd);
        Assert.Equal(9000, sessions[2].CacheReadTokens);
        Assert.Equal(task.Id, sessions[2].Task);
        Assert.Equal(500, receipts.Runs.Single(r => r.Unit == "unit-a").CacheReadTokens);

        // What the task cost to land, so far, by the same rule.
        var receipt = receipts.Tasks.Single();
        Assert.Equal(0.73m, receipt.CostUsd);
        Assert.Equal(1789 + 1200, receipt.InputTokens);
        Assert.Equal(346 + 340, receipt.OutputTokens);
        Assert.Equal(1789 + 346 + 1200 + 340 + 66882, receipt.Tokens);
        Assert.Equal(34 + 2700 + 4, receipt.SessionSeconds);
        Assert.Equal(0.5, receipt.FailedWorkerMinutes);
        Assert.Equal(0, receipt.BlockedWorkerRuns);
        Assert.Equal(1, receipt.TimeoutSessions);
        Assert.Equal(0, receipt.ConductorSessions);   // staffings, which this test never wrote: a session is not a staffing

        // By harness: the busier one first, and each one priced only as far as its rows are.
        var byHarness = Assert.IsAssignableFrom<IReadOnlyList<HarnessReceiptDto>>(receipts.ByHarness);
        Assert.Equal(["codex/gpt", "claude/opus"], byHarness.Select(h => $"{h.Harness}/{h.Model}"));
        Assert.Equal((2, 1, 0.42m, 1789 + 346 + 66882, 90 + 2700 + 4d), (byHarness[0].Sessions, byHarness[0].WorkerRuns, byHarness[0].CostUsd, byHarness[0].Tokens, byHarness[0].Seconds));
        Assert.Equal((1, 1, 0.31m, 1200 + 340, 34 + 30d), (byHarness[1].Sessions, byHarness[1].WorkerRuns, byHarness[1].CostUsd, byHarness[1].Tokens, byHarness[1].Seconds));
    }

    /// <summary>
    /// Harnesses are compared on fresh tokens, and a cache read is not one: it is context seen again at a fraction
    /// of the price. Two harnesses on one task — a pi orchestrator and a Codex worker, each reporting both — sum
    /// fresh and cache-read apart, for the task, for each harness, and for the window a <c>--task</c> read returns.
    /// </summary>
    [Fact]
    public async Task Fresh_tokens_and_cache_reads_are_summed_apart_per_task_and_per_harness()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await RegisterAsync("owner", "claude", "opus");
        var task = await owner.AddTaskAsync("Build the feature");
        var other = await owner.AddTaskAsync("Somebody else's");

        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "implementer", "codex", "gpt", "work@example.com", Branch(task.Id), "unit-a", true, 90, null,
            InputTokens: 5, OutputTokens: 36, CacheReadTokens: 16382, PromptBytes: 2048))).EnsureSuccessStatusCode();
        await SessionAsync(task.Id, "#orchestrator", "pi", "ollama/gemma4:26b", 300, inputTokens: 90000, outputTokens: 2000, cacheReadTokens: 700000);
        await SessionAsync(task.Id, "#orchestrator", "pi", "ollama/gemma4:26b", 200, inputTokens: 60000, outputTokens: 1000, cacheReadTokens: 500000);
        await SessionAsync(other.Id, "#orchestrator", "pi", "ollama/gemma4:26b", 10, inputTokens: 1, outputTokens: 1, cacheReadTokens: 99);

        var response = await _hub.CreateClient().GetAsync(Routes.Receipts + "?task=" + task.Id);
        response.EnsureSuccessStatusCode();
        var receipts = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto))!;

        var receipt = receipts.Tasks.Single();
        Assert.Equal(5 + 36 + 90000 + 2000 + 60000 + 1000, receipt.Tokens);
        Assert.Equal(16382 + 700000 + 500000, receipt.CacheReadTokens);
        Assert.Equal((receipt.Tokens, receipt.CacheReadTokens), (receipts.Tokens, receipts.CacheReadTokens));

        var byHarness = Assert.IsAssignableFrom<IReadOnlyList<HarnessReceiptDto>>(receipts.ByHarness);
        var pi = byHarness.Single(h => h.Harness == "pi");
        var codex = byHarness.Single(h => h.Harness == "codex");
        Assert.Equal((2, 90000 + 2000 + 60000 + 1000, 1200000), (pi.Sessions, pi.Tokens, pi.CacheReadTokens));
        Assert.Equal((1, 5 + 36, 16382), (codex.WorkerRuns, codex.Tokens, codex.CacheReadTokens));
    }

    /// <summary>
    /// The nulls are load-bearing. A window in which nothing reported a cost has no total, not a total of zero:
    /// zero would say the work was free. And a run that ended <c>blocked</c> is counted as the environment's
    /// failure, which is what the plan's second-largest waste is made of.
    /// </summary>
    [Fact]
    public async Task Nothing_reported_is_no_total_and_a_blocked_run_is_counted_as_one()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await RegisterAsync("owner", "claude", "opus");
        var task = await owner.AddTaskAsync("Build the feature");

        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "implementer", "codex", "gpt", "work@example.com", Branch(task.Id), "unit-a", false, 120, null,
            Status: "blocked", FailureKind: "environment", TotalTokens: 5000))).EnsureSuccessStatusCode();
        await SessionAsync(task.Id, "#orchestrator", "claude", "opus", 60);

        var receipts = await ReceiptsAsync();

        Assert.Null(receipts.CostUsd);
        Assert.Equal(0, receipts.CostReportedRuns);
        Assert.Equal(2, receipts.CostUnreportedRuns);
        Assert.Equal(5000, receipts.Tokens);   // the one total, since the run split nothing

        var receipt = receipts.Tasks.Single();
        Assert.Null(receipt.CostUsd);
        Assert.Null(receipt.InputTokens);
        Assert.Equal(5000, receipt.Tokens);
        Assert.Equal(1, receipt.BlockedWorkerRuns);
        Assert.Equal(1, receipt.WorkerRunsFailed);
        Assert.Equal(2d, receipt.FailedWorkerMinutes);
        Assert.Equal(60d, receipt.SessionSeconds);
        Assert.Null(Assert.IsAssignableFrom<IReadOnlyList<HarnessReceiptDto>>(receipts.ByHarness).Single(h => h.Harness == "claude").Tokens);
    }

    /// <summary>
    /// "What did T-n cost to land" is one task's question, so the endpoint answers it for one task: its rows, its
    /// receipt and totals over those alone. A task id the hub cannot read is a rule violation, the same as
    /// everywhere else a task id is typed; a task that spent nothing is an empty answer, not an error.
    /// </summary>
    [Fact]
    public async Task Asking_for_one_task_narrows_the_rows_and_the_totals_to_it()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await RegisterAsync("owner", "claude", "opus");
        var priced = await owner.AddTaskAsync("The priced one");
        var other = await owner.AddTaskAsync("The other one");
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            priced.Id, "cheap", "codex", "gpt", "work@example.com", Branch(priced.Id), "unit-a", true, 90, 0.42m))).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            other.Id, "cheap", "codex", "gpt", "work@example.com", Branch(other.Id), "unit-a", true, 60, 1.00m))).EnsureSuccessStatusCode();
        await SessionAsync(priced.Id, "#orchestrator", "claude", "opus", 34, 0.31m);
        await SessionAsync(other.Id, "#orchestrator", "claude", "opus", 20, 0.10m);

        var everything = await ReceiptsAsync();
        Assert.Equal(1.83m, everything.CostUsd);
        Assert.Equal(2, everything.Tasks.Count);

        var response = await _hub.CreateClient().GetAsync(Routes.Receipts + "?task=" + priced.Id);
        response.EnsureSuccessStatusCode();
        var one = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto))!;

        Assert.Equal(priced.Id, one.Tasks.Single().Task);
        Assert.Equal(0.73m, one.Tasks.Single().CostUsd);
        Assert.Equal(0.73m, one.CostUsd);
        Assert.Equal(2, one.CostReportedRuns);
        Assert.Equal(0, one.CostUnreportedRuns);
        Assert.Equal([priced.Id], one.Runs.Select(r => r.Task));
        Assert.Equal([priced.Id], Assert.IsAssignableFrom<IReadOnlyList<SessionRunDto>>(one.SessionRuns).Select(s => s.Task));
        // The registration is not the task's, so the filter leaves it out: sessions here are the task's, not the hub's.
        Assert.Equal(0, one.Sessions);

        var lower = await _hub.CreateClient().GetAsync(Routes.Receipts + "?task=" + priced.Id.ToLowerInvariant());
        lower.EnsureSuccessStatusCode();
        Assert.Equal(0.73m, (await lower.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto))!.CostUsd);

        var unknown = await _hub.CreateClient().GetAsync(Routes.Receipts + "?task=T-999");
        unknown.EnsureSuccessStatusCode();
        var empty = (await unknown.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto))!;
        Assert.Empty(empty.Tasks);
        Assert.Null(empty.CostUsd);

        var refused = await _hub.CreateClient().GetAsync(Routes.Receipts + "?task=eleven");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("invalid_task_id", await refused.Content.ReadAsStringAsync());
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

}
