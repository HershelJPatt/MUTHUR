using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Launch.Tests;

public sealed class CapabilityProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muthur-tests", "capability-probe-" + Guid.NewGuid().ToString("n"));
    private readonly FixtureRunner _runner = new();
    private readonly FixtureAdapter _adapter = new();
    public CapabilityProbeTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private (string, IReadOnlyList<string>)? Resolve(string name) => name == "fixture" ? (Path.Combine(_root, "fixture"), []) : ExecutableResolver.Resolve(name);
    private async Task<WorkerRequest> Request()
    {
        async Task<ProcessResult> Git(params string[] args)
        {
            var result = await _runner.RunAsync("git", args, _root);
            Assert.True(result.Ok, result.Message);
            return result;
        }
        await Git("init", "--quiet");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "fixture baseline");
        var head = (await Git("rev-parse", "HEAD")).StdOut.Trim();
        return new(_root, "", "fixture", Path.Combine(_root, ".git"), ["fixture permissions"], ["git push*"], Path.Combine(_root, "scratch"),
            Capabilities: new(["shell", "build", "test", "commit", "worktree-base"], "worker-run", Path.Combine(_root, "cache"), head, _root));
    }

    [Theory]
    [InlineData("execute", "available")]
    [InlineData("prose", "unknown")]
    [InlineData("forged-nonce", "unknown")]
    [InlineData("tampered-fixture", "unknown")]
    [InlineData("denied-build", "unavailable")]
    [InlineData("timeout", "temporarily-failing")]
    [InlineData("cancel", "temporarily-failing")]
    public async Task Admitted_fixture_checks_exact_commands_receipts_inputs_and_cleanup(string mode, string expectedBuild)
    {
        _runner.Mode = mode;
        var request = await Request();
        var admission = new Admission(() => Assert.Empty(Directory.EnumerateDirectories(request.ScratchDirectory)));
        var result = await new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
        Assert.Equal(1, admission.Count);
        Assert.Equal(1, admission.Releases);
        Assert.Equal(1, result.ProbeStarts);
        Assert.Equal(1, _runner.ModelInvocations);
        Assert.Equal(expectedBuild, Assert.Single(result.Observations, o => o.Capability == "build").State);
        Assert.DoesNotContain(result.Observations, o => o.Capability.Contains("interaction", StringComparison.Ordinal) || o.Capability == "native-agent-tools");
        Assert.All(_adapter.Requests, r =>
        {
            Assert.Equal(request.AllowedCommands, r.AllowedCommands);
            Assert.Equal(request.DeniedCommands, r.DeniedCommands);
        });
        var head = await _runner.RunAsync("git", ["rev-parse", "HEAD"], _root);
        Assert.Equal(request.Capabilities!.BaseCommit, head.StdOut.Trim());
        if (mode == "execute") Assert.All(result.Observations, o => Assert.Equal("available", o.State));
        if (mode == "denied-build")
        {
            Assert.Equal("available", Assert.Single(result.Observations, o => o.Capability == "shell").State);
            Assert.Equal("available", Assert.Single(result.Observations, o => o.Capability == "test").State);
        }
        if (mode is "timeout" or "cancel") Assert.All(result.Observations, o => Assert.Equal(TimeSpan.FromMinutes(5), o.ExpiresAt - o.ObservedAt));
    }

    [Fact]
    public async Task Different_probe_settings_refuse_before_model_and_cleanup()
    {
        var request = await Request();
        _adapter.VarySettings = true;
        var admission = new Admission();
        var result = await new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
        Assert.Equal("capability_identity_unknown", result.Code);
        Assert.Equal(0, _runner.ModelInvocations);
        Assert.Equal(1, admission.Releases);
        Assert.Empty(Directory.EnumerateDirectories(request.ScratchDirectory));
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("process-cleanup")]
    public async Task Uncertain_cleanup_retains_reservation_and_never_publishes_cache(string mode)
    {
        var request = await Request();
        _runner.Mode = mode;
        var admission = new Admission();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90)));
        Assert.IsType<ProbeCleanupUncertainException>(error.InnerException);
        Assert.Contains("retained", error.Message);
        Assert.Equal(0, admission.Releases);
        Assert.False(Directory.Exists(request.Capabilities!.CacheDirectory));
    }

    [Fact]
    public async Task Replay_does_not_execute_or_release()
    {
        var request = await Request();
        var admission = new Admission { MayExecute = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90)));
        Assert.Equal(1, admission.Count);
        Assert.Equal(0, admission.Releases);
        Assert.Equal(0, _runner.ModelInvocations);
    }

    private sealed class Admission(Action? onRelease = null) : IProbeAdmissionClient
    {
        public int Count { get; private set; }
        public int Releases { get; private set; }
        public bool MayExecute { get; init; } = true;
        public Task<ProbeAdmissionDto> AdmitAsync(ProbeAdmissionRequest request, CancellationToken ct = default)
        {
            Count++;
            Assert.Equal("T-100", request.Task);
            Assert.Equal("implementer", request.Tier);
            Assert.True(Guid.TryParseExact(request.RunId, "N", out _));
            return Task.FromResult(new ProbeAdmissionDto("reservation", request.Task, request.RunId, MayExecute));
        }
        public Task ReleaseAsync(ProbeReleaseRequest request, CancellationToken ct = default)
        { onRelease?.Invoke(); Releases++; return Task.CompletedTask; }
    }

    private sealed class FixtureAdapter : IHarnessAdapter
    {
        public string Name => "fixture";
        public string? WorkerNote => null;
        public string? CapabilityExecutable => "fixture";
        public bool VarySettings { get; set; }
        public List<WorkerRequest> Requests { get; } = [];
        public string? CapabilitySettings(WorkerRequest request) => VarySettings ? request.WorkingDirectory : "fixture-scope";
        public HarnessInvocation Build(WorkerRequest request)
        { Requests.Add(request); return new("fixture", [], request.Prompt); }
        public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result) => new(result.Ok, "simulated", false);
    }

    private sealed class FixtureRunner : ICapabilityProcessRunner
    {
        private readonly CapabilityProcessRunner _real = new();
        public string Mode { get; set; } = "execute";
        public int ModelInvocations { get; private set; }
        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var isolated = new Dictionary<string, string>(environment ?? new Dictionary<string, string>())
            {
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            };
            if (Mode == "cleanup" && arguments.Contains("worktree") && arguments.Contains("remove")) return new(1, "", "simulated cleanup failure");
            if (Path.GetFileName(fileName) == "fixture")
            {
                if (arguments.Contains("--version")) return new(0, "fixture-v2", "");
                ModelInvocations++;
                if (Mode == "prose") return new(0, "All capabilities available; shell build test commit succeeded.", "");
                if (Mode == "timeout") return new(124, "", "fixture timeout");
                if (Mode == "cancel") throw new OperationCanceledException();
                if (Mode == "process-cleanup") throw new ProbeCleanupUncertainException("Simulated cannot confirm exit.");
                var fixture = Path.Combine(workingDirectory, ".muthur-capability");
                var steps = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(fixture, "commands.json")), CapabilityJsonContext.Default.IReadOnlyListCapabilityProbeStep)!;
                Assert.StartsWith("dotnet build ", Assert.Single(steps, s => s.Key == "build").Command);
                Assert.StartsWith("dotnet test ", Assert.Single(steps, s => s.Key == "test").Command);
                Assert.DoesNotContain(steps, s => s.Command.Contains("msbuild", StringComparison.OrdinalIgnoreCase));
                foreach (var step in steps)
                {
                    var command = Mode == "denied-build" && step.Key == "build" ? "$global:LASTEXITCODE = 126" : step.Command;
                    var result = await _real.RunAsync("pwsh", ["-NoProfile", "-Command", command + "\n" + step.Receipt], workingDirectory,
                        timeout: timeout, ct: ct, scrubEnvironment: scrubEnvironment, environment: isolated);
                    Assert.True(result.Ok, result.Message);
                }
                if (Mode == "forged-nonce") File.WriteAllText(Path.Combine(fixture, "build.receipt.json"), "{\"nonce\":\"wrong\",\"exitCode\":0}");
                if (Mode == "tampered-fixture") File.AppendAllText(Path.Combine(fixture, "fixture.proj"), "<!-- changed -->");
                return new(0, "Simulated harness, real deterministic SDK fixture commands.", "");
            }
            return await _real.RunAsync(fileName, arguments, workingDirectory, stdin, timeout ?? TimeSpan.FromSeconds(10), ct, scrubEnvironment, isolated);
        }
    }
}
