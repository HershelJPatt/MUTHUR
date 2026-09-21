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

    private async Task<(HttpClient Client, ProbeAdmissionRequest Request)> Setup()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
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
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Started.TrySetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
            return new(0, "", "");
        }
    }

    [Theory]
    [InlineData("validator", true)]
    [InlineData("validator", false)]
    [InlineData("orchestrator", true)]
    [InlineData("orchestrator", false)]
    [InlineData("overseer", true)]
    [InlineData("overseer", false)]
    public async Task Admission_and_each_launch_class_share_the_gate(string kind, bool admissionFirst)
    {
        var (client, request) = await Setup();
        var process = new HeldProcess();
        var overseer = ActivatorUtilities.CreateInstance<OverseerService>(_hub.Services, process);
        var conductor = ActivatorUtilities.CreateInstance<ConductorService>(_hub.Services, overseer);
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
                await Ledger.MutateAsync(Caller.Founder, async m =>
                {
                    var row = await TaskService.LoadAsync(m.Db, task.Id, default);
                    row.State = TaskState.Validating;
                    m.Db.TaskValidations.Add(new TaskValidation { TaskId = row.Id, ValidatorKey = "fixture-validator", Verdict = Verdict.Pending, WaitingSince = m.Now });
                    m.Record("task.implemented", row.Id, new { head = "fixture" });
                });
            }
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Ledger.MutateAsync(Caller.Founder, async _ => { entered.SetResult(); await unblock.Task; });
        await entered.Task;
        Task<ProbeAdmissionDto> admission;
        Task<int> pass;
        if (admissionFirst)
        {
            admission = conductor.AdmitProbeAsync(Caller.Founder, request);
            pass = conductor.RunPassAsync();
        }
        else
        {
            pass = conductor.RunPassAsync();
            admission = conductor.AdmitProbeAsync(Caller.Founder, request);
        }
        unblock.SetResult();
        await writer;
        try
        {
            if (admissionFirst)
            {
                Assert.True((await admission).MayExecute);
                Assert.Equal(0, await pass);
                Assert.Equal(0, await conductor.RunPassAsync());
                Assert.False(process.Started.Task.IsCompleted);
                Assert.Empty(_hub.Validators.Started);
                Assert.Empty(_hub.Orchestrators.Started);
            }
            else
            {
                Assert.Equal(1, await pass);
                Assert.Equal("probe_capacity_exhausted", (await Assert.ThrowsAsync<MuthurException>(() => admission)).Code);
                if (kind == "overseer") await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            Assert.Equal(1, (await conductor.StatusAsync()).Running);
        }
        finally { await conductor.StopSessionsAsync(); }
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
