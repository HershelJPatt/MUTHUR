using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Data;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class IncidentTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private HttpClient Founder => _hub.Founder();
    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();

    public IncidentTests()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        _hub.Settings["Muthur:ConductorOrchestrators"] = "true";
    }

    public void Dispose() { _hub.Dispose(); _repo.Dispose(); }

    private Task<ProjectDto> Setup() => _hub.AddProjectAsync(repoPath: _repo.Path);
    private async Task<IncidentDto> Add(string signature = "failure", string path = "worker", string configuration = "v1") =>
        await Read<IncidentDto>(await Founder.PostAsJsonAsync(Routes.Incidents, new AddIncidentRequest("Shared fault", signature, path, configuration, "Successful bounded probe", "demo")));
    private Task<HttpResponseMessage> Act<T>(string id, string action, T request, HttpClient? client = null) =>
        (client ?? Founder).PostAsJsonAsync(Routes.IncidentAction(id, action), request);
    private async Task<IncidentDto> Transition(string id, string state) =>
        await Read<IncidentDto>(await Act(id, "transition", new TransitionIncidentRequest(state, "Measured evidence")));
    private async Task<IncidentObservationDto> Observe(string id, string task, string signature = "failure", string evidence = "Independent retained evidence") =>
        await Read<IncidentObservationDto>(await Act(id, "observe", new ObserveIncidentRequest(task, evidence, signature, "worker", "v1")));
    private async Task Suppress(string id, string task, int observation, string assignment = "#orchestrator") =>
        (await Act(id, "suppress", new SuppressIncidentRequest(task, assignment, observation, "Exact observed failure"))).EnsureSuccessStatusCode();
    private async Task<IncidentDetailDto> Show(string id) => (await Founder.GetFromJsonAsync<IncidentDetailDto>(Routes.Incident(id)))!;
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task Error(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Independent_observations_exact_advisory_matches_and_separate_incidents()
    {
        await Setup();
        var a = await Founder.AddTaskAsync("first");
        var b = await Founder.AddTaskAsync("second");
        var i = await Add(" failure ");
        var same = await Add();
        await Add("Failure");
        await Add(path: "worker/other");
        await Add(configuration: "v10");
        var one = await Observe(i.Id, a.Id, evidence: "<script>first</script>");
        var two = await Observe(i.Id, b.Id, "different", "Second independent symptom");
        Assert.NotEqual(one.Id, two.Id);
        var matches = (await Founder.GetFromJsonAsync<IncidentMatchesDto>(Routes.IncidentMatches + "?project=demo&signature=%20failure%20&path=worker&configuration=v1"))!;
        Assert.True(matches.Advisory);
        Assert.Equal(new[] { i.Id, same.Id }, matches.Matches.Select(x => x.Id));
        var show = await Show(i.Id);
        Assert.Equal(2, show.Observations.Count);
        Assert.Equal("different", show.Observations[1].Signature);
        Assert.Empty(show.Suppressions);
        await Transition(i.Id, "confirmed");
        await Error(await Act(i.Id, "suppress", new SuppressIncidentRequest(b.Id, "#orchestrator", two.Id, "reason")), "incident_evidence");
        await Error(await Act(i.Id, "suppress", new SuppressIncidentRequest(b.Id, "#orchestrator", one.Id, "reason")), "incident_evidence");
        await Error(await Act(same.Id, "unlink", new UnlinkIncidentRequest(one.Id, "wrong incident")), "incident_evidence");
    }

    public static IEnumerable<object[]> Transitions()
    {
        string[] states = ["suspected", "confirmed", "mitigated", "resolved", "disproven"];
        var allowed = new HashSet<(string, string)>
        {
            ("suspected", "confirmed"), ("suspected", "disproven"), ("confirmed", "mitigated"), ("confirmed", "resolved"),
            ("confirmed", "disproven"), ("mitigated", "confirmed"), ("mitigated", "resolved"), ("mitigated", "disproven"),
            ("resolved", "suspected"), ("disproven", "suspected"),
        };
        foreach (var from in states)
            foreach (var to in states) yield return [from, to, allowed.Contains((from, to))];
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Every_lifecycle_edge_is_explicit(string from, string to, bool allowed)
    {
        await Setup();
        var i = await Add();
        if (from is "confirmed" or "mitigated" or "resolved") await Transition(i.Id, "confirmed");
        if (from is "mitigated" or "resolved" or "disproven") await Transition(i.Id, from);
        var response = await Act(i.Id, "transition", new TransitionIncidentRequest(to, "Evidence"));
        if (!allowed) await Error(response, "incident_transition");
        else
        {
            var updated = await Read<IncidentDto>(response);
            Assert.Equal(to, updated.State);
            Assert.Equal(from is "resolved" or "disproven" ? 2 : 1, updated.ConditionVersion);
        }
    }

    [Fact]
    public async Task Suppression_is_idempotent_unlink_is_attributable_and_reads_write_nothing()
    {
        await Setup();
        var task = await Founder.AddTaskAsync("blocked pair");
        var i = await Add();
        var o = await Observe(i.Id, task.Id);
        var other = await Observe(i.Id, task.Id, evidence: "Another independent observation");
        await Transition(i.Id, "confirmed");
        await Suppress(i.Id, task.Id, o.Id);
        await Suppress(i.Id, task.Id, o.Id);
        var before = await Show(i.Id);
        Assert.Single(before.Suppressions);
        Assert.True(before.Suppressions[0].Effective);
        Assert.Single(before.Events, e => e.Type == "incident.suppressed");
        await Act(i.Id, "unlink", new UnlinkIncidentRequest(other.Id, "different correction"));
        Assert.True((await Show(i.Id)).Suppressions[0].Effective);
        (await Act(i.Id, "unlink", new UnlinkIncidentRequest(o.Id, "false grouping corrected"))).EnsureSuccessStatusCode();
        (await Act(i.Id, "unlink", new UnlinkIncidentRequest(o.Id, "repeat"))).EnsureSuccessStatusCode();
        var after = await Show(i.Id);
        Assert.False(after.Suppressions[0].Effective);
        Assert.Equal(o.Evidence, after.Observations[0].Evidence);
        Assert.Equal("false grouping corrected", after.Observations[0].UnlinkReason);
        Assert.Single(after.Events, e => e.Type == "incident.released");
        var events = await Founder.GetFromJsonAsync<List<EventDto>>(Routes.Events);
        await Conductor.StatusAsync();
        await Conductor.PlanAsync();
        await Conductor.PlanOrchestratorsAsync();
        await Founder.GetTaskAsync(task.Id);
        await Founder.GetAsync(Routes.Incidents);
        var metrics = (await Founder.GetFromJsonAsync<IncidentMetricsDto>(Routes.IncidentAction(i.Id, "metrics")))!;
        Assert.Equal(1, metrics.GroupingCorrectionReleaseCount);
        Assert.Equal(2, metrics.UnlinkCorrections);
        Assert.Null(metrics.ActualProcessStarts);
        Assert.Null(metrics.DiagnosisSessions);
        Assert.Equal(events!.Select(e => e.Seq), (await Founder.GetFromJsonAsync<List<EventDto>>(Routes.Events))!.Select(e => e.Seq));
        Assert.All(after.Events, e => Assert.Equal(i.Id, e.Payload.GetProperty("incidentId").GetString()));
    }

    [Theory]
    [InlineData("unlink")]
    [InlineData("recover")]
    public async Task Release_text_cannot_make_an_inactive_suppression_effective(string action)
    {
        await Setup();
        await Conductor.SetOrchestratorsAsync(Caller.Founder, true);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("released pair");
        var incident = await Add();
        var observation = await Observe(incident.Id, task.Id);
        await Transition(incident.Id, "confirmed");
        await Suppress(incident.Id, task.Id, observation.Id);
        var response = action == "unlink"
            ? await Act(incident.Id, action, new UnlinkIncidentRequest(observation.Id, "effective"))
            : await Act(incident.Id, action, new RecoverIncidentRequest("probe", "effective"));
        response.EnsureSuccessStatusCode();

        var suppression = Assert.Single((await Show(incident.Id)).Suppressions);
        Assert.False(suppression.Active);
        Assert.False(suppression.Effective);
        Assert.Equal("effective", suppression.ReleaseReason);
        Assert.Empty((await Conductor.StatusAsync()).IncidentSuppressions!);
        Assert.False(Assert.Single(Assert.Single((await Founder.GetTaskAsync(task.Id)).Incidents!).Suppressions).Effective);
        Assert.Contains(await Conductor.PlanOrchestratorsAsync(), a => a.TaskKey == task.Id);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("probe", null, "mitigated")]
    [InlineData("configuration", "v2", "confirmed")]
    public async Task Recovery_and_reopening_require_fresh_evidence(string kind, string? configuration, string state)
    {
        await Setup();
        var task = await Founder.AddTaskAsync("recover");
        var i = await Add();
        var o = await Observe(i.Id, task.Id);
        await Transition(i.Id, "confirmed");
        await Suppress(i.Id, task.Id, o.Id);
        _hub.Clock.Advance(TimeSpan.FromSeconds(10));
        var recovered = await Read<IncidentDto>(await Act(i.Id, "recover", new RecoverIncidentRequest(kind, "Measured success", configuration)));
        Assert.Equal(2, recovered.ConditionVersion);
        Assert.Equal(state, recovered.State);
        Assert.False((await Show(i.Id)).Suppressions[0].Effective);
        await Error(await Act(i.Id, "suppress", new SuppressIncidentRequest(task.Id, "#orchestrator", o.Id, "old evidence")), "incident_evidence");
        var fresh = await Read<IncidentObservationDto>(await Act(i.Id, "observe", new ObserveIncidentRequest(task.Id, "Fresh measurement", "failure", "worker", configuration ?? "v1")));
        await Suppress(i.Id, task.Id, fresh.Id);
        await Transition(i.Id, "resolved");
        Assert.All((await Show(i.Id)).Suppressions, s => Assert.False(s.Effective));
        await Transition(i.Id, "suspected");
        await Transition(i.Id, "confirmed");
        await Error(await Act(i.Id, "suppress", new SuppressIncidentRequest(task.Id, "#orchestrator", fresh.Id, "old evidence")), "incident_evidence");
        Assert.Equal(3, (await Show(i.Id)).Incident.ConditionVersion);
        Assert.Equal(TaskState.Backlog, (await Founder.GetTaskAsync(task.Id)).Task.State);
    }

    [Fact]
    public async Task Ownership_project_terminal_and_malformed_inputs_are_rejected()
    {
        await Setup();
        var owner = await _hub.RegisterAgentAsync("owner");
        var stranger = await _hub.RegisterAgentAsync("stranger");
        var task = await owner.AddTaskAsync("owned");
        var i = await Add();
        var observationRequest = new ObserveIncidentRequest(task.Id, "evidence", "failure", "worker", "v1");
        await Error(await Act(i.Id, "observe", observationRequest, _hub.CreateClient()), "unauthorized", HttpStatusCode.Unauthorized);
        (await Act(i.Id, "observe", observationRequest, stranger)).EnsureSuccessStatusCode();
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        await Error(await Act(i.Id, "observe", observationRequest, stranger), "not_owner");
        var o = await Read<IncidentObservationDto>(await Act(i.Id, "observe", observationRequest, owner));
        await Transition(i.Id, "confirmed");
        await Error(await Act(i.Id, "unlink", new UnlinkIncidentRequest(o.Id, "reason"), stranger), "not_owner");
        await Error(await Act(i.Id, "suppress", new SuppressIncidentRequest(task.Id, "#orchestrator", o.Id, "reason"), stranger), "not_owner");
        await Suppress(i.Id, task.Id, o.Id);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        await _hub.AddProjectAsync("other", _repo.Path);
        var cross = await Founder.AddTaskAsync("cross", "other");
        await Error(await Act(i.Id, "observe", observationRequest with { Task = cross.Id }), "incident_scope");
        await Error(await Founder.GetAsync(Routes.Incident("bad")), "incident_input");
        await Error(await Founder.GetAsync(Routes.Incident("I-2147483648")), "incident_input");
        await Error(await Founder.GetAsync(Routes.Incident("I-99999")), "not_found", HttpStatusCode.NotFound);
        await Error(await Act(i.Id, "observe", observationRequest with { Task = null! }), "incident_input");
        await Error(await Act(i.Id, "observe", observationRequest with { Evidence = " " }), "incident_evidence");
        await Error(await Act(i.Id, "suppress", new SuppressIncidentRequest(task.Id, "absent-role", o.Id, "reason")), "not_found", HttpStatusCode.NotFound);
        await Error(await Act(i.Id, "recover", new RecoverIncidentRequest("probe", "success", "v2")), "incident_input");
        await Error(await Act(i.Id, "recover", new RecoverIncidentRequest("configuration", "success", " v1 ")), "incident_input");
        await Error(await Act(i.Id, "recover", new RecoverIncidentRequest("probe", " ")), "incident_evidence");
        foreach (var hours in new[] { "0", "721", "x" })
            await Error(await Founder.GetAsync(Routes.IncidentAction(i.Id, "metrics") + "?hours=" + hours), "incident_input");
        (await Founder.PostActionAsync(task.Id, "cancel", new CancelTaskRequest("terminal"))).EnsureSuccessStatusCode();
        await Error(await Act(i.Id, "observe", observationRequest), "incident_task_state");
        await Error(await Act(i.Id, "suppress", new SuppressIncidentRequest(task.Id, "#orchestrator", o.Id, "reason")), "incident_task_state");
        Assert.False((await Show(i.Id)).Suppressions[0].Effective);
        (await Act(i.Id, "recover", new RecoverIncidentRequest("probe", "success"))).EnsureSuccessStatusCode();
        Assert.Equal(TaskState.Cancelled, (await Founder.GetTaskAsync(task.Id)).Task.State);
    }

    [Fact]
    public async Task Diagnosis_preserves_omitted_annotations_and_validates_bounds()
    {
        await Setup();
        var i = await Add();
        await Error(await Act(i.Id, "update", new UpdateIncidentRequest("diagnosis", "workaround")), "incident_input");
        await Read<IncidentDto>(await Act(i.Id, "update", new UpdateIncidentRequest("diagnosis", "documented workaround", "approval-7")));
        var updated = await Read<IncidentDto>(await Act(i.Id, "update", new UpdateIncidentRequest("revised diagnosis")));
        Assert.Equal("documented workaround", updated.Workaround);
        Assert.Equal("approval-7", updated.AuthorizationReference);
        await Error(await Act(i.Id, "update", new UpdateIncidentRequest(new string('x', 16001))), "incident_input");
        await Error(await Founder.PostAsJsonAsync(Routes.Incidents, new AddIncidentRequest(new string('x', 301), "s", "p", "c", "r")), "incident_input");
        await Error(await Founder.PostAsJsonAsync(Routes.Incidents, new AddIncidentRequest("title", new string('x', 1001), "p", "c", "r")), "incident_input");
        var metrics = (await Founder.GetFromJsonAsync<IncidentMetricsDto>(Routes.IncidentAction(i.Id, "metrics")))!;
        Assert.Equal(2, metrics.RecordedDiagnosisUpdates);
    }

    [Fact]
    public async Task Malformed_JSON_and_oversized_optional_fields_return_stable_errors()
    {
        await Setup();
        var task = await Founder.AddTaskAsync("input checks");
        var i = await Add();
        foreach (var body in new[] { "null", "{", "[]", "{\"observation\":\"bad\",\"reason\":\"r\"}" })
            await Error(await Founder.PostAsync(Routes.IncidentAction(i.Id, "unlink"), new StringContent(body, System.Text.Encoding.UTF8, "application/json")), "incident_input");
        await Error(await Act(i.Id, "observe", new ObserveIncidentRequest(task.Id, "e", "s", "p", "c", new string('x', 2001))), "incident_input");
        await Error(await Act(i.Id, "update", new UpdateIncidentRequest("d", "w", new string('x', 2001))), "incident_input");
        await Error(await Act(i.Id, "update", new UpdateIncidentRequest("d", new string('x', 16001), "a")), "incident_input");
        await Error(await Act(i.Id, "observe", new ObserveIncidentRequest(task.Id, new string('x', 16001), "s", "p", "c")), "incident_evidence");
        await Error(await Act(i.Id, "transition", new TransitionIncidentRequest("confirmed", " ")), "incident_evidence");
        await Error(await Act(i.Id, "recover", new RecoverIncidentRequest("probe", "e")), "incident_transition");
        await Error(await Founder.PostAsJsonAsync(Routes.Incidents, new AddIncidentRequest("t", "s", "p", "c", new string('x', 16001))), "incident_input");
        Assert.Empty((await Show(i.Id)).Observations);
    }

    [Fact]
    public async Task Recovery_does_not_reset_daily_budget_or_answer_human_requests()
    {
        _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "1";
        await Setup();
        await Conductor.SetOrchestratorsAsync(Caller.Founder, true);
        var owner = await _hub.RegisterAgentAsync("owner");
        var budget = await owner.AddTaskAsync("budget remains");
        var human = await owner.AddTaskAsync("human remains");
        (await owner.ClaimAsync(human.Id)).EnsureSuccessStatusCode();
        var request = await Read<FounderRequestDto>(await owner.PostAsJsonAsync(Routes.Requests, new AskRequest("Human product choice", human.Id, Kind: "human")));
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.System, m =>
        {
            m.Record("conductor.staffing", int.Parse(budget.Id[2..], System.Globalization.CultureInfo.InvariantCulture), new { role = "#orchestrator" });
            return Task.CompletedTask;
        });
        var i = await Add();
        var a = await Observe(i.Id, budget.Id);
        var b = await Observe(i.Id, human.Id);
        await Transition(i.Id, "confirmed");
        await Suppress(i.Id, budget.Id, a.Id);
        await Suppress(i.Id, human.Id, b.Id);
        Assert.Equal(2, (await Conductor.StatusAsync()).IncidentSuppressions!.Count);
        for (var pass = 0; pass < 3; pass++) Assert.Equal(0, await Conductor.RunPassAsync());
        Assert.Empty(_hub.Orchestrators.Started);
        (await Act(i.Id, "recover", new RecoverIncidentRequest("probe", "successful bounded probe"))).EnsureSuccessStatusCode();
        Assert.Empty(await Conductor.PlanOrchestratorsAsync());
        Assert.Contains((await Conductor.StatusAsync()).Stalls, s => s.Task == budget.Id && s.Reason.Contains("budget", StringComparison.Ordinal));
        var requests = (await Founder.GetFromJsonAsync<List<FounderRequestDto>>(Routes.Requests))!;
        Assert.Contains(requests, r => r.Id == request.Id && r.Status == "open");
        Assert.Equal(TaskState.Blocked, (await Founder.GetTaskAsync(human.Id)).Task.State);
    }

    [Fact]
    public async Task Metrics_count_recorded_events_and_measure_first_subsequent_staffing_with_fake_time()
    {
        await Setup();
        var task = await Founder.AddTaskAsync("metric cohort");
        var i = await Add();
        var observation = await Observe(i.Id, task.Id);
        await Transition(i.Id, "confirmed");
        await Suppress(i.Id, task.Id, observation.Id);
        (await Act(i.Id, "recover", new RecoverIncidentRequest("probe", "success"))).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromSeconds(17));
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        await ledger.MutateAsync(Caller.System, m =>
        {
            var id = int.Parse(task.Id[2..], System.Globalization.CultureInfo.InvariantCulture);
            m.Record("conductor.staffing", id, new { role = "#orchestrator" });
            m.Record("worker.finished", id, new { success = true });
            m.Record("task.landed", id);
            return Task.CompletedTask;
        });
        _hub.Clock.Advance(TimeSpan.FromSeconds(10));
        var metric = (await Founder.GetFromJsonAsync<IncidentMetricsDto>(Routes.IncidentAction(i.Id, "metrics")))!;
        Assert.Equal(17, Assert.Single(metric.RecoveryToFirstStaffing).ElapsedSeconds);
        Assert.Equal(1, metric.ConductorStaffingAttempts);
        Assert.Equal(1, metric.WorkerRuns);
        Assert.Equal(1, metric.SuccessfulTaskOutcomes);
        Assert.Null(metric.ActualProcessStarts);
        Assert.Contains(metric.RecoveryToFirstStaffing[0].StaffingSeq!.Value, metric.SourceEventSequenceIds);
        _hub.Clock.Advance(TimeSpan.FromHours(25));
        var expired = (await Founder.GetFromJsonAsync<IncidentMetricsDto>(Routes.IncidentAction(i.Id, "metrics")))!;
        Assert.Equal(0, expired.ObservationCount);
        Assert.Equal(0, expired.ConductorStaffingAttempts);
        Assert.Equal(new[] { task.Id }, expired.LinkedTaskCohort);
    }

    [Fact]
    public async Task Recovering_one_incident_keeps_another_incident_gate_on_the_same_pair()
    {
        await Setup();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("two independent incidents");
        var first = await Add();
        var second = await Add();
        foreach (var i in new[] { first, second })
        {
            var observation = await Observe(i.Id, task.Id);
            await Transition(i.Id, "confirmed");
            await Suppress(i.Id, task.Id, observation.Id);
        }
        (await Act(first.Id, "recover", new RecoverIncidentRequest("probe", "success for first condition"))).EnsureSuccessStatusCode();
        var conflict = await owner.ClaimAsync(task.Id);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var error = await conflict.ReadErrorAsync();
        Assert.Equal("incident_wait", error.Code);
        Assert.Contains(second.Id, error.Message);
        Assert.True(Assert.Single((await Show(second.Id)).Suppressions).Effective);
        Assert.False(Assert.Single((await Show(first.Id)).Suppressions).Effective);
    }

    [Fact]
    public async Task Orchestration_gate_is_durable_and_does_not_consume_attempts_or_lift_other_gates()
    {
        await Setup();
        await Conductor.SetOrchestratorsAsync(Caller.Founder, true);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("suppressed");
        var unaffected = await owner.AddTaskAsync("unaffected");
        var dependent = await owner.AddTaskAsync("dependent");
        (await owner.PostActionAsync(dependent.Id, "dependencies", new DependenciesRequest([unaffected.Id]))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(dependent.Id, "attended", new AttendedRequest("human measurement"))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(dependent.Id, "hold", new HoldRequest("keep hold"))).EnsureSuccessStatusCode();
        var i = await Add();
        var o = await Observe(i.Id, task.Id);
        var d = await Observe(i.Id, dependent.Id);
        await Transition(i.Id, "confirmed");
        await Suppress(i.Id, task.Id, o.Id);
        await Suppress(i.Id, dependent.Id, d.Id);
        var before = (await Founder.GetTaskAsync(dependent.Id)).Task;
        for (var pass = 0; pass < 3; pass++)
        {
            var plan = await Conductor.PlanOrchestratorsAsync();
            Assert.DoesNotContain(plan, a => a.TaskKey == task.Id || a.TaskKey == dependent.Id);
            Assert.Contains(plan, a => a.TaskKey == unaffected.Id);
            _hub.Clock.Advance(TimeSpan.FromHours(1));
        }
        await Error(await owner.ClaimAsync(task.Id), "incident_wait", HttpStatusCode.Conflict);
        Assert.Equal(2, (await Conductor.StatusAsync()).IncidentSuppressions!.Count);
        Assert.Single((await Founder.GetTaskAsync(task.Id)).Incidents!);
        using var restart = new HubFactory { DataDir = _hub.DataDir };
        var restored = restart.Services.GetRequiredService<ConductorService>();
        Assert.Equal(2, (await restored.StatusAsync()).IncidentSuppressions!.Count);
        Assert.DoesNotContain(await restored.PlanOrchestratorsAsync(), a => a.TaskKey == task.Id);
        (await Act(i.Id, "recover", new RecoverIncidentRequest("probe", "bounded success"))).EnsureSuccessStatusCode();
        var after = (await Founder.GetTaskAsync(dependent.Id)).Task;
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.DependsOn, after.DependsOn);
        Assert.Equal(before.HoldReason, after.HoldReason);
        Assert.Equal(before.AttendedReason, after.AttendedReason);
        Assert.Contains(await Conductor.PlanOrchestratorsAsync(), a => a.TaskKey == task.Id);
        Assert.DoesNotContain(await Conductor.PlanOrchestratorsAsync(), a => a.TaskKey == dependent.Id);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await Founder.GetFromJsonAsync<List<EventDto>>(Routes.Events))!, e => e.Type == "conductor.staffing");
    }

    [Fact]
    public async Task Validator_suppression_only_removes_its_assignment_and_keeps_inflight_sessions()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["check-a", "check-b"]);
        foreach (var role in new[] { "check-a", "check-b" })
            (await Founder.PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(role, "Check it", IsValidator: true))).EnsureSuccessStatusCode();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("validation");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile("task/incident", "work.txt", "work");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/incident"))).EnsureSuccessStatusCode();
        var i = await Add();
        var o = await Observe(i.Id, task.Id);
        await Transition(i.Id, "confirmed");
        await Suppress(i.Id, task.Id, o.Id, "check-a");
        var plan = await Conductor.PlanAsync();
        Assert.DoesNotContain(plan, a => a.RoleKey == "check-a");
        Assert.Contains(plan, a => a.RoleKey == "check-b");
        _hub.Validators.Block = true;
        await Conductor.RunPassAsync();
        Assert.Single(_hub.Validators.Started);
        await Suppress(i.Id, task.Id, o.Id, "check-b");
        Assert.Equal(1, Conductor.RunningCount);
        Assert.Empty(await Conductor.PlanAsync());
        var subject = (await Founder.GetTaskAsync(task.Id)).Task.CurrentSubject;
        (await Act(i.Id, "recover", new RecoverIncidentRequest("probe", "bounded success"))).EnsureSuccessStatusCode();
        Assert.Equal(subject!.Id, (await Founder.GetTaskAsync(task.Id)).Task.CurrentSubject!.Id);
        Assert.Equal(1, Conductor.RunningCount);
        Assert.Contains(await Conductor.PlanAsync(), a => a.RoleKey == "check-a");
        await Conductor.StopSessionsAsync();
    }
}
