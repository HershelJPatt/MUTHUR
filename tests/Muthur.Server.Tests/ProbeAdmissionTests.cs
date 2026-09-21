using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Launch;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

[CollectionDefinition("Probe process environment", DisableParallelization = true)]
public sealed class ProbeProcessEnvironment;

[Collection("Probe process environment")]
public sealed class ProbeAdmissionTests : IDisposable
{
    private readonly HubFactory _hub = new() { ExpectsLoggedErrors = true };
    private readonly TestRepo _repo = new();
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();
    private Ledger Ledger => _hub.Services.GetRequiredService<Ledger>();
    public ProbeAdmissionTests() => _hub.Settings["Muthur:ConductorEnabled"] = "true";
    public void Dispose() { Environment.SetEnvironmentVariable("PATH", _path); _hub.Dispose(); _repo.Dispose(); }

    private async Task<(HttpClient Client, ProbeAdmissionRequest Request)> Setup(string[]? validators = null)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators);
        var client = await _hub.RegisterAgentAsync("owner", tier: "mastermind");
        var task = await client.AddTaskAsync("Probe fixture");
        (await client.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile),
            """{"tiers":{"worker":[{"harness":"fixture","model":"model","account":"account"}],"mastermind":[{"harness":"fixture","model":"model","account":"account"}]}}""");
        return (client, new(task.Id, "worker", "fixture", "model", "account", Guid.NewGuid().ToString("N")));
    }

    private static Task<HttpResponseMessage> Admit(HttpClient client, ProbeAdmissionRequest request) =>
        client.PostAsJsonAsync(Routes.ProbeAdmit, request, MuthurJsonContext.Default.ProbeAdmissionRequest);
    private static async Task<ProbeAdmissionDto> Granted(HttpClient client, ProbeAdmissionRequest request)
    {
        var response = await Admit(client, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ProbeAdmissionDto))!;
    }
    private static Task<HttpResponseMessage> Release(HttpClient client, string id) =>
        client.PostAsJsonAsync(Routes.ProbeRelease, new ProbeReleaseRequest(id), MuthurJsonContext.Default.ProbeReleaseRequest);
    private Task<int> Count(string type) => Ledger.ReadAsync((db, _) => db.Events.CountAsync(e => e.Type == type));
    private static async Task Code(HttpResponseMessage response, string code) => Assert.Equal(code, (await response.ReadErrorAsync()).Code);

    private sealed class HeldProcess : IProcessRunner
    {
        private int _starts;
        public int Starts => Volatile.Read(ref _starts);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Interlocked.Increment(ref _starts);
            Started.TrySetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
            return new(0, "", "");
        }
    }

    private async Task<ProbeAdmissionRequest> SetupGateAsync(string kind, ConductorService conductor, OverseerService overseer)
    {
        var (client, request) = await Setup(kind == "validator" ? ["fixture-validator"] : null);
        await conductor.SetCeilingAsync(Caller.Founder, new(1, null, null, null, false));
        _hub.Validators.Block = true;
        _hub.Orchestrators.Block = true;
        if (kind == "overseer")
        {
            // Resolution needs a file, but HeldProcess intercepts execution: no harness is actually launched.
            File.WriteAllText(Path.Combine(_hub.DataDir, OperatingSystem.IsWindows() ? "codex.cmd" : "codex"), "fixture");
            Environment.SetEnvironmentVariable("PATH", _hub.DataDir + Path.PathSeparator + _path);
            await overseer.ConfigureAsync(Caller.Founder, new(true, Harness: "codex", Model: "fixture", Account: "account"));
        }
        else
        {
            var task = await client.AddTaskAsync("Ready for staffing");
            if (kind == "orchestrator") await conductor.SetOrchestratorsAsync(Caller.Founder, true);
            else
            {
                (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("fixture-validator", "Validate", true))).EnsureSuccessStatusCode();
                (await client.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
                (await client.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
                var branch = $"task/{task.Id}-work";
                _repo.BranchWithFile(branch, $"{task.Id}.txt", $"{task.Id}\n");
                (await client.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
                Assert.NotNull((await client.GetTaskAsync(task.Id)).Task.CurrentSubject);
                Assert.Equal(task.Id, Assert.Single(await conductor.PlanAsync()).TaskKey);
            }
        }
        return request;
    }

    [Theory]
    [InlineData("validator", true)]
    [InlineData("validator", false)]
    [InlineData("orchestrator", true)]
    [InlineData("orchestrator", false)]
    [InlineData("overseer", true)]
    [InlineData("overseer", false)]
    public Task Admission_and_each_launch_class_share_the_gate(string kind, bool admissionFirst) =>
        SharedGateAsync(kind, admissionFirst, contention: false);

    [Theory]
    [InlineData("validator", true)]
    [InlineData("validator", false)]
    [InlineData("orchestrator", true)]
    [InlineData("orchestrator", false)]
    [InlineData("overseer", true)]
    [InlineData("overseer", false)]
    public Task Admission_and_each_launch_class_contend_for_the_gate(string kind, bool admissionFirst) =>
        SharedGateAsync(kind, admissionFirst, contention: true);

    private async Task SharedGateAsync(string kind, bool admissionFirst, bool contention)
    {
        using var cancel = new CancellationTokenSource();
        var process = new HeldProcess();
        var overseer = ActivatorUtilities.CreateInstance<OverseerService>(_hub.Services, process);
        var conductor = ActivatorUtilities.CreateInstance<ConductorService>(_hub.Services, overseer);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<Task>();
        Task<ProbeAdmissionDto>? admission = null;
        var timeout = TimeSpan.FromSeconds(30);
        try
        {
            var request = await SetupGateAsync(kind, conductor, overseer);
            Task? writer = null;
            if (contention)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                writer = Ledger.MutateAsync(Caller.Founder, async _ => { entered.SetResult(); await unblock.Task; }, cancel.Token);
                operations.Add(writer);
                await entered.Task.WaitAsync(timeout);
            }

            Task<int> pass;
            // Invocation order is not gate acquisition order; only completion orders the non-racing case.
            if (admissionFirst)
            {
                admission = conductor.AdmitProbeAsync(Caller.Founder, request, cancel.Token);
                operations.Add(admission);
                if (!contention)
                {
                    Assert.True((await admission.WaitAsync(timeout)).MayExecute);
                    await AssertOneSessionAsync();
                }
                pass = conductor.RunPassAsync(cancel.Token);
                operations.Add(pass);
            }
            else
            {
                pass = conductor.RunPassAsync(cancel.Token);
                operations.Add(pass);
                if (!contention)
                {
                    Assert.Equal(1, await pass.WaitAsync(timeout));
                    await AssertOneSessionAsync();
                }
                admission = conductor.AdmitProbeAsync(Caller.Founder, request, cancel.Token);
                operations.Add(admission);
            }

            unblock.TrySetResult();
            if (writer is not null) await writer.WaitAsync(timeout);
            var admissionError = await Record.ExceptionAsync(async () => await admission.WaitAsync(timeout));
            var launches = await pass.WaitAsync(timeout);
            if (admissionError is null)
            {
                Assert.True((await admission).MayExecute);
                Assert.Equal(0, launches);
            }
            else
            {
                Assert.Equal("probe_capacity_exhausted", Assert.IsType<MuthurException>(admissionError).Code);
                Assert.Equal(1, launches);
            }
            if (!contention) Assert.Equal(admissionFirst ? 0 : 1, launches);
            var secondPass = conductor.RunPassAsync(cancel.Token);
            operations.Add(secondPass);
            Assert.Equal(0, await secondPass.WaitAsync(timeout));
            if (kind == "overseer" && launches == 1) await process.Started.Task.WaitAsync(timeout);
            await AssertOneSessionAsync();
            var validators = _hub.Validators.Started.Count;
            var orchestrators = _hub.Orchestrators.Started.Count;
            Assert.Equal(kind == "validator" ? launches : 0, validators);
            Assert.Equal(kind == "orchestrator" ? launches : 0, orchestrators);
            Assert.Equal(kind == "overseer" ? launches : 0, process.Starts);
            var grants = await Count("worker.probe_admitted");
            Assert.Equal(admissionError is null ? 1 : 0, grants);
            Assert.Equal(1, grants + validators + orchestrators + process.Starts);
        }
        finally
        {
            unblock.TrySetResult();
            cancel.Cancel();
            try
            {
                await Task.WhenAll(operations.Select(operation => Record.ExceptionAsync(() => operation)));
                if (admission is { IsCompletedSuccessfully: true })
                    await conductor.ReleaseProbeAsync(Caller.Founder, new(admission.Result.ReservationId));
            }
            finally { await conductor.StopSessionsAsync(); }
        }

        async Task AssertOneSessionAsync()
        {
            var status = await conductor.StatusAsync(cancel.Token);
            Assert.Equal(1, status.Running);
            Assert.Single(status.Sessions);
        }
    }

    [Fact]
    public async Task Two_running_conductor_sessions_refuse_an_additional_probe()
    {
        var (client, request) = await Setup();
        _hub.Orchestrators.Block = true;
        await client.AddTaskAsync("First");
        await client.AddTaskAsync("Second");
        await Conductor.SetOrchestratorsAsync(Caller.Founder, true);
        try
        {
            Assert.Equal(2, await Conductor.RunPassAsync());
            await Code(await Admit(client, request), "probe_capacity_exhausted");
            Assert.Equal(2, (await Conductor.StatusAsync()).Running);
        }
        finally { await Conductor.StopSessionsAsync(); }
    }

    [Fact]
    public async Task Simultaneous_admissions_share_capacity_and_release_restores_exactly_one_slot()
    {
        var (client, request) = await Setup();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 8).Select(async _ => { await start.Task; return await Admit(client, request with { RunId = Guid.NewGuid().ToString("N") }); }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(calls);
        Assert.Equal(2, responses.Count(r => r.IsSuccessStatusCode));
        foreach (var response in responses.Where(r => !r.IsSuccessStatusCode)) await Code(response, "probe_capacity_exhausted");
        var first = (await responses.First(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync(MuthurJsonContext.Default.ProbeAdmissionDto))!;
        var status = await Conductor.StatusAsync();
        Assert.Equal(2, status.Running);
        Assert.Equal(status.Running, status.Sessions.Count);
        Assert.All(status.Sessions, s => Assert.StartsWith("#capability-probe:", s.Role));
        (await Release(client, first.ReservationId)).EnsureSuccessStatusCode();
        (await Release(client, first.ReservationId)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await Conductor.StatusAsync()).Running);
        Assert.True((await Granted(client, request)).MayExecute);
        Assert.Equal(2, (await Conductor.StatusAsync()).Running);
        Assert.Equal(1, await Count("worker.probe_released"));
    }

    [Fact]
    public async Task Concurrent_identical_requests_grant_once_and_never_reactivate_released_reservations()
    {
        var (client, request) = await Setup();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 8).Select(async _ => { await start.Task; return await Granted(client, request); }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(calls);
        Assert.Single(results, r => r.MayExecute);
        Assert.Single(results.Select(r => r.ReservationId).Distinct());
        var id = results[0].ReservationId;
        Assert.False((await Granted(client, request)).MayExecute);
        (await Release(client, id)).EnsureSuccessStatusCode();
        var replay = await Granted(client, request);
        Assert.Equal(id, replay.ReservationId);
        Assert.False(replay.MayExecute);
        Assert.Equal(0, (await Conductor.StatusAsync()).Running);
        Assert.Equal(1, await Count("worker.probe_admitted"));
        await Code(await Admit(client, request with { Model = "different" }), "probe_run_conflict");
        await Code(await Release(client, "unknown"), "not_found");
    }

    [Fact]
    public async Task Current_owner_and_original_release_identity_are_enforced()
    {
        var (client, request) = await Setup();
        await Code(await Admit(_hub.CreateClient(), request), "unauthorized");
        var other = await _hub.RegisterAgentAsync("other", tier: "mastermind");
        await Code(await Admit(other, request), "unauthorized");
        var admission = await Granted(client, request);
        var otherCaller = (await _hub.Services.GetRequiredService<AgentService>().AuthenticateAsync(other.DefaultRequestHeaders.Authorization!.Parameter!))!;
        await Ledger.MutateAsync(Caller.Founder, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, request.Task, default);
            task.OwnerAgentId = otherCaller.AgentId;
            m.Record("task.claimed", task.Id);
        });
        await Code(await Admit(other, request), "unauthorized");
        await Code(await Release(other, admission.ReservationId), "unauthorized");
        (await Release(client, admission.ReservationId)).EnsureSuccessStatusCode();
        (await Release(_hub.Founder(), admission.ReservationId)).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("candidate", "probe_candidate_unavailable")]
    [InlineData("limited", "probe_account_limited")]
    [InlineData("disabled", "probe_admission_disabled")]
    [InlineData("malformed", "probe_request_invalid")]
    [InlineData("nonmastermind", "unauthorized")]
    [InlineData("noowner", "unauthorized")]
    public async Task Refusals_have_no_reservation_side_effects(string kind, string code)
    {
        var (client, request) = await Setup();
        if (kind == "candidate") request = request with { Model = "missing" };
        if (kind == "malformed") request = request with { RunId = Guid.NewGuid().ToString("D") };
        if (kind == "disabled") await Conductor.SetEnabledAsync(Caller.Founder, false);
        if (kind == "limited") await _hub.Services.GetRequiredService<HarnessService>().ReportLimitAsync(Caller.Founder, new("account", _hub.Clock.GetUtcNow().AddHours(1)));
        if (kind is "nonmastermind" or "noowner") await Ledger.MutateAsync(Caller.Founder, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, request.Task, default);
            if (kind == "noowner") task.OwnerAgentId = null;
            else (await m.Db.Agents.SingleAsync(a => a.Id == task.OwnerAgentId)).Tier = "worker";
            m.Record("fixture.changed", task.Id);
        });
        await Code(await Admit(client, request), code);
        Assert.Equal(0, await Count("worker.probe_admitted"));
    }

    [Fact]
    public async Task Restart_replay_retains_capacity_and_spending_without_ttl_reclamation()
    {
        var (client, request) = await Setup();
        var first = await Granted(client, request);
        var second = await Granted(client, request with { RunId = Guid.NewGuid().ToString("N") });
        _hub.Clock.Advance(TimeSpan.FromDays(2));
        var restarted = ActivatorUtilities.CreateInstance<ConductorService>(_hub.Services);
        Assert.Equal(2, (await restarted.StatusAsync()).Running);
        var error = await Assert.ThrowsAsync<MuthurException>(() => restarted.AdmitProbeAsync(Caller.Founder, request with { RunId = Guid.NewGuid().ToString("N") }));
        Assert.Equal("probe_capacity_exhausted", error.Code);
        await restarted.ReleaseProbeAsync(Caller.Founder, new(first.ReservationId));
        Assert.Equal(1, (await restarted.StatusAsync()).Running);
        await restarted.ReleaseProbeAsync(Caller.Founder, new(second.ReservationId));
    }

    [Fact]
    public async Task Shared_daily_bucket_counts_both_directions_and_keeps_reset_semantics()
    {
        var (client, request) = await Setup();
        var taskId = int.Parse(request.Task.AsSpan(2));
        await Ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("task.implemented", taskId, new { head = "old" });
            for (var i = 0; i < 2; i++) m.Record("conductor.staffing", taskId, new { role = "#orchestrator" });
            return Task.CompletedTask;
        });
        var first = await Granted(client, request);
        (await Release(client, first.ReservationId)).EnsureSuccessStatusCode();
        Assert.False((await Granted(client, request)).MayExecute);
        await Code(await Admit(client, request with { RunId = Guid.NewGuid().ToString("N") }), "probe_budget_exhausted");
        var recreated = ActivatorUtilities.CreateInstance<ConductorService>(_hub.Services);
        Assert.Contains((await recreated.StatusAsync()).Stalls, s => s.Task == request.Task && s.Role == "#orchestrator" && s.Failures == 3);
        await Conductor.SetOrchestratorsAsync(Caller.Founder, true);
        await Ledger.MutateAsync(Caller.Founder, async m =>
        {
            var task = await TaskService.LoadAsync(m.Db, request.Task, default);
            task.State = TaskState.Backlog;
            task.OwnerAgentId = null;
            m.Record("task.implemented", taskId, new { head = "old" });
        });
        Assert.Empty(await recreated.PlanOrchestratorsAsync());
        foreach (var reset in new[] { "request.answered", "task.dependencies_ready", "task.implemented", "conductor.on" })
        {
            await Ledger.MutateAsync(Caller.Founder, m =>
            {
                for (var i = 0; i < 3; i++) m.Record("conductor.staffing", taskId, new { role = "#orchestrator" });
                m.Record(reset, taskId, new { head = "new" });
                return Task.CompletedTask;
            });
            Assert.Single(await recreated.PlanOrchestratorsAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_stop_while_admission_waits_leaves_no_reservation(bool stop)
    {
        var (_, request) = await Setup();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Ledger.MutateAsync(Caller.Founder, async _ => { entered.SetResult(); await unblock.Task; });
        await entered.Task;
        var admission = Conductor.AdmitProbeAsync(Caller.Founder, request, cancel.Token);
        if (stop) await Conductor.StopSessionsAsync();
        else cancel.Cancel();
        unblock.SetResult();
        await holding;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission);
        Assert.Equal(0, await Count("worker.probe_admitted"));
        if (stop) Assert.Equal("probe_admission_disabled", (await Assert.ThrowsAsync<MuthurException>(() => Conductor.AdmitProbeAsync(Caller.Founder, request))).Code);
    }

    [Fact]
    public async Task Failed_commit_does_not_leave_a_phantom_slot()
    {
        var (_, request) = await Setup();
        await Ledger.MutateAsync(Caller.Founder, async m =>
            await m.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_probe BEFORE INSERT ON Events WHEN NEW.Type = 'worker.probe_admitted' BEGIN SELECT RAISE(ABORT, 'fixture commit failure'); END"));
        await Assert.ThrowsAsync<DbUpdateException>(() => Conductor.AdmitProbeAsync(Caller.Founder, request));
        Assert.Equal(0, await Count("worker.probe_admitted"));
        Assert.Equal(0, (await Conductor.StatusAsync()).Running);
        await Ledger.MutateAsync(Caller.Founder, async m => await m.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_probe"));
        Assert.True((await Conductor.AdmitProbeAsync(Caller.Founder, request)).MayExecute);
    }

    [Fact]
    public async Task In_flight_release_cannot_grant_more_than_one_replacement()
    {
        var (_, request) = await Setup();
        var first = await Conductor.AdmitProbeAsync(Caller.Founder, request);
        await Conductor.AdmitProbeAsync(Caller.Founder, request with { RunId = Guid.NewGuid().ToString("N") });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Ledger.MutateAsync(Caller.Founder, async _ => { entered.SetResult(); await unblock.Task; });
        await entered.Task;
        var release = Conductor.ReleaseProbeAsync(Caller.Founder, new(first.ReservationId));
        var replacement = Conductor.AdmitProbeAsync(Caller.Founder, request with { RunId = Guid.NewGuid().ToString("N") });
        var excess = Conductor.AdmitProbeAsync(Caller.Founder, request with { RunId = Guid.NewGuid().ToString("N") });
        Assert.False(replacement.IsCompleted);
        unblock.SetResult();
        await writer;
        await release;
        Assert.True((await replacement).MayExecute);
        Assert.Equal("probe_capacity_exhausted", (await Assert.ThrowsAsync<MuthurException>(() => excess)).Code);
        Assert.Equal(2, (await Conductor.StatusAsync()).Running);
    }
}
