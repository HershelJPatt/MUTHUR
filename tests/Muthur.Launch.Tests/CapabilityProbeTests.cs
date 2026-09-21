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
    [InlineData("denied-build", "unavailable")]
    [InlineData("timeout", "temporarily-failing")]
    [InlineData("cancel", "temporarily-failing")]
    public async Task Admitted_fixture_checks_receipts_outputs_and_cleans_up(string mode, string expectedBuild)
    {
        _runner.Mode = mode;
        var request = await Request();
        var admission = new Admission(_runner);
        var result = await new CapabilityProbe(_runner, admission, resolve: _ => (Path.Combine(_root, "fixture"), []), adapterFor: _ => _adapter)
            .RunAsync(new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
        Assert.Equal(1, admission.Count);
        Assert.True(admission.Disposed);
        Assert.Equal(1, result.ProbeStarts);
        Assert.Equal(1, _runner.ModelInvocations);
        Assert.Equal(expectedBuild, Assert.Single(result.Observations, o => o.Capability == "build").State);
        Assert.DoesNotContain(result.Observations, o => o.Capability.Contains("interaction", StringComparison.Ordinal) || o.Capability == "native-agent-tools");
        Assert.All(_adapter.Requests, r =>
        {
            Assert.Equal(request.AllowedCommands, r.AllowedCommands);
            Assert.Equal(request.DeniedCommands, r.DeniedCommands);
        });
        Assert.Empty(Directory.EnumerateDirectories(request.ScratchDirectory));
        var head = await _runner.RunAsync("git", ["rev-parse", "HEAD"], _root);
        Assert.Equal(request.Capabilities!.BaseCommit, head.StdOut.Trim());
        if (mode == "execute") Assert.All(result.Observations, o => Assert.Equal("available", o.State));
        if (mode == "denied-build") Assert.Equal("available", Assert.Single(result.Observations, o => o.Capability == "shell").State);
        if (mode is "timeout" or "cancel") Assert.All(result.Observations, o => Assert.Equal(TimeSpan.FromMinutes(5), o.ExpiresAt - o.ObservedAt));
    }

    [Fact]
    public async Task Different_probe_settings_refuse_before_model_and_cleanup()
    {
        var request = await Request();
        _adapter.VarySettings = true;
        var admission = new Admission(_runner);
        var result = await new CapabilityProbe(_runner, admission, resolve: _ => (Path.Combine(_root, "fixture"), []), adapterFor: _ => _adapter)
            .RunAsync(new("fixture", "fixture", null), request, TimeSpan.FromSeconds(90));
        Assert.Equal("capability_identity_unknown", result.Code);
        Assert.Equal(0, _runner.ModelInvocations);
        Assert.True(admission.Disposed);
        Assert.Empty(Directory.EnumerateDirectories(request.ScratchDirectory));
    }

    private sealed class Admission(FixtureRunner runner) : ICapabilityProbeAdmission, ICapabilityProbeLease
    {
        public int Count { get; private set; }
        public bool Disposed { get; private set; }
        public IProcessRunner Processes => runner;
        public Task<ICapabilityProbeLease?> AdmitAsync(HarnessCandidate candidate, WorkerRequest request, TimeSpan timeout, CancellationToken ct)
        { Count++; return Task.FromResult<ICapabilityProbeLease?>(this); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
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
        {
            Requests.Add(request);
            return new("fixture", [], request.Prompt);
        }
        public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result) => new(result.Ok, "simulated", false);
    }

    private sealed class FixtureRunner : IProcessRunner
    {
        private readonly ProcessRunner _real = new();
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
            if (Path.GetFileName(fileName) == "fixture")
            {
                if (arguments.Contains("--version")) return new(0, "fixture-v1", "");
                ModelInvocations++;
                if (Mode == "prose") return new(0, "All capabilities available; shell build test commit succeeded.", "");
                if (Mode == "timeout") return new(124, "", "fixture timeout");
                if (Mode == "cancel") throw new OperationCanceledException();
                var script = Path.Combine(workingDirectory, ".muthur-capability", "probe.ps1");
                if (Mode == "denied-build")
                {
                    // Simulated deterministic tool refusal, preserving all other steps and their receipts.
                    var text = File.ReadAllText(script).Replace("& dotnet msbuild (Join-Path $fixture 'fixture.proj') /t:Build /nologo /noautoresponse", "throw 'fixture denied build'");
                    File.WriteAllText(script, text);
                }
                var result = await _real.RunAsync("pwsh", ["-NoProfile", "-File", script], workingDirectory, timeout: timeout, ct: ct,
                    scrubEnvironment: scrubEnvironment, environment: isolated);
                if (Mode == "forged-nonce") File.WriteAllText(Path.Combine(workingDirectory, ".muthur-capability", "receipts.json"), "{\"nonce\":\"wrong\",\"steps\":{\"build\":0}}");
                return result;
            }
            return await _real.RunAsync(fileName, arguments, workingDirectory, stdin, timeout ?? TimeSpan.FromSeconds(10), ct, scrubEnvironment, isolated);
        }
    }
}
