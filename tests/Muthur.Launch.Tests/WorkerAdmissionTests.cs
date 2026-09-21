using Muthur.Contracts;

namespace Muthur.Launch.Tests;

internal sealed class AdmittedFixture : IWorkerAdmissionClient
{
    public List<WorkerAdmissionRequest> Requests { get; } = [];
    public List<WorkerReleaseRequest> Releases { get; } = [];
    public bool Replay { get; set; }
    public bool Deny { get; set; }
    public bool FailRelease { get; set; }
    public Action? Admitted { get; set; }
    public Func<WorkerAdmissionRequest, WorkerAdmissionDto>? Response { get; set; }
    public Task<WorkerAdmissionDto> AdmitAsync(WorkerAdmissionRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (Deny) throw new InvalidOperationException("fixture denial");
        Admitted?.Invoke();
        if (Response is not null) return Task.FromResult(Response(request));
        return Task.FromResult(new WorkerAdmissionDto(Guid.NewGuid().ToString("N"), request.Task, request.RunId, !Replay));
    }
    public Task ReleaseAsync(WorkerReleaseRequest request, CancellationToken ct = default)
    {
        Assert.False(ct.IsCancellationRequested);
        Releases.Add(request);
        if (FailRelease) throw new InvalidOperationException("fixture release failure");
        return Task.CompletedTask;
    }
    public static WorkerAdmissionContext Context(string root, AdmittedFixture? client = null) =>
        new(client ?? new(), "T-1", "implementer", root, "base", "blob", "spec.md", null, null, "worker/fixture");
}

internal sealed class ContainedFixture(IProcessRunner runner) : IWorkerProcessRunner
{
    public bool Supported => true;
    public WorkerCleanup Cleanup { get; set; } = WorkerCleanup.ExitedAndTreeEmpty;
    public bool Started { get; set; } = true;
    public async Task<WorkerProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null) =>
        new(await runner.RunAsync(fileName, arguments, workingDirectory, stdin, timeout, ct, scrubEnvironment, environment), Started, Cleanup);
}

public sealed class WorkerAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muthur-tests", "worker-admission-" + Guid.NewGuid().ToString("N"));
    public WorkerAdmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private sealed class Runner : IProcessRunner
    {
        public int Starts;
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Starts++;
            return Task.FromResult(new ProcessResult(0, "{\"result\":\"usage limit reached\",\"is_error\":true}", ""));
        }
    }
    private WorkerRequest Request => new(_root, "prompt", "model", null, [], [], Path.Combine(_root, "scratch"));
    private static readonly HarnessCandidate[] Candidates = [new("claude", "one", "a"), new("claude", "two", "b")];

    [Theory]
    [InlineData("denied")]
    [InlineData("replay")]
    [InlineData("missing")]
    public async Task No_admission_permission_means_no_setup_scratch_or_execution(string kind)
    {
        var runner = new Runner();
        var admission = new AdmittedFixture { Deny = kind == "denied", Replay = kind == "replay" };
        var setup = 0;
        var attempts = await new WorkerLauncher(runner, _ => ("fake", []), workerProcesses: new ContainedFixture(runner),
            admission: kind == "missing" ? null : AdmittedFixture.Context(_root, admission),
            prepare: _ => { setup++; return Task.CompletedTask; }).RunAsync(Candidates, _ => Request, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);
        Assert.Single(attempts); Assert.Equal(0, runner.Starts); Assert.Equal(0, setup);
        Assert.Empty(admission.Releases); Assert.False(Directory.Exists(Request.ScratchDirectory));
        Assert.Equal("worker_admission_failed", attempts[0].FailureKind);
    }

    [Theory]
    [InlineData(WorkerCleanup.CleanupUncertain, false, 1, 0)]
    [InlineData(WorkerCleanup.ExitedAndTreeEmpty, true, 1, 1)]
    [InlineData(WorkerCleanup.ExitedAndTreeEmpty, false, 2, 2)]
    public async Task Fallback_requires_confirmed_cleanup_and_release(WorkerCleanup cleanup, bool failRelease, int starts, int releases)
    {
        var runner = new Runner();
        var admission = new AdmittedFixture { FailRelease = failRelease };
        var attempts = await new WorkerLauncher(runner, _ => ("fake", []), workerProcesses: new ContainedFixture(runner) { Cleanup = cleanup },
            admission: AdmittedFixture.Context(_root, admission)).RunAsync(Candidates, _ => Request, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);
        Assert.Equal(starts, runner.Starts); Assert.Equal(starts, admission.Requests.Count); Assert.Equal(releases, admission.Releases.Count);
        Assert.Equal(starts, admission.Requests.Select(r => r.RunId).Distinct().Count());
        Assert.Equal(admission.Requests.Select(r => r.RunId), attempts.Select(a => a.RunId));
        Assert.All(attempts, a => Assert.NotNull(a.ReservationId));
        if (cleanup == WorkerCleanup.CleanupUncertain || failRelease) Assert.False(attempts[^1].Outcome.Success);
    }

    [Fact]
    public async Task Cancellation_after_admission_releases_without_setup_or_launch()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new Runner();
        var admission = new AdmittedFixture { Admitted = cancel.Cancel };
        var attempts = await new WorkerLauncher(runner, _ => ("fake", []), workerProcesses: new ContainedFixture(runner),
            admission: AdmittedFixture.Context(_root, admission)).RunAsync(Candidates, _ => Request, TimeSpan.FromSeconds(1), _ => Task.CompletedTask, cancel.Token);
        Assert.Equal(0, runner.Starts); Assert.Single(admission.Releases);
        Assert.Equal(WorkerCleanup.NotStarted, Assert.Single(attempts).Cleanup);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setup_failure_releases_only_with_confirmed_subprocess_cleanup(bool confirmed)
    {
        var runner = new Runner();
        var admission = new AdmittedFixture();
        var attempts = await new WorkerLauncher(runner, _ => ("fake", []), workerProcesses: new ContainedFixture(runner),
            admission: AdmittedFixture.Context(_root, admission), prepare: _ => throw new IOException("setup failed"),
            setupCleanupConfirmed: () => confirmed).RunAsync(Candidates, _ => Request, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);
        Assert.Equal(0, runner.Starts); Assert.Single(admission.Requests);
        Assert.Equal(confirmed ? 1 : 0, admission.Releases.Count);
        Assert.Equal(confirmed ? "worker_setup_failed" : "worker_cleanup_uncertain", Assert.Single(attempts).FailureKind);
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("task")]
    [InlineData("run")]
    public async Task Mismatched_response_does_not_execute_or_release(string field)
    {
        var runner = new Runner();
        var admission = new AdmittedFixture
        {
            Response = r => new(field == "reservation" ? "invalid" : Guid.NewGuid().ToString("N"),
                field == "task" ? "T-2" : r.Task, field == "run" ? Guid.NewGuid().ToString("N") : r.RunId, true),
        };
        var attempts = await new WorkerLauncher(runner, _ => ("fake", []), workerProcesses: new ContainedFixture(runner),
            admission: AdmittedFixture.Context(_root, admission)).RunAsync(Candidates, _ => Request, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);
        Assert.Single(attempts); Assert.Equal(0, runner.Starts); Assert.Empty(admission.Releases);
        Assert.False(Directory.Exists(Request.ScratchDirectory));
    }

    [Fact]
    public void Binding_is_stable_and_changes_with_inputs()
    {
        var context = AdmittedFixture.Context(_root);
        var request = Request;
        var first = context.Bind(Candidates[0], request, "run", TimeSpan.FromMinutes(1));
        Assert.Equal(first, context.Bind(Candidates[0], request, "run", TimeSpan.FromMinutes(1)));
        Assert.NotEqual(first.InputHash, context.Bind(Candidates[0], request with { Prompt = "changed" }, "run", TimeSpan.FromMinutes(1)).InputHash);
        Assert.NotEqual(first.InputHash, (context with { Parent = "" }).Bind(Candidates[0], request, "run", TimeSpan.FromMinutes(1)).InputHash);
        Assert.NotEqual(first.InputHash, context.Bind(Candidates[0], request with { AllowedCommands = ["a", "b"] }, "run", TimeSpan.FromMinutes(1)).InputHash);
    }
}
