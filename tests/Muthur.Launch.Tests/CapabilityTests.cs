using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Launch.Tests;

public sealed class CapabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muthur-tests", "capability-" + Guid.NewGuid().ToString("n"));
    private readonly Clock _clock = new();
    private readonly Runner _runner = new();
    private readonly Adapter _adapter = new();
    private static readonly HarnessCandidate Candidate = new("fixture", "fixture", "fixture");

    public CapabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private static (string, IReadOnlyList<string>)? Resolve(string name) => (Path.GetFullPath(name), []);
    private CapabilityStore Store => new(Path.Combine(_root, "cache"), _clock);
    private WorkerRequest Request(params string[] requirements) => new(_root, "fixture", "fixture", null,
        ["dotnet *"], ["git push*"], _root, Capabilities: new(requirements, "worker-run", Path.Combine(_root, "cache"), new string('a', 40), _root));

    private async Task<CapabilityIdentity> Identity(WorkerRequest request) =>
        (await new CapabilityEvaluator(_runner, _clock, Resolve).InspectAsync(_adapter, request)).Identity!;

    private CapabilityObservation Observation(CapabilityIdentity identity, string key, string state = "available") =>
        new(identity, key, state, _clock.GetUtcNow(), _clock.GetUtcNow().Add(state == "temporarily-failing" ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24)), "simulated fixture only", 10);

    [Fact]
    public void Requirement_lines_union_without_inferring_browser_or_prose()
    {
        Assert.Equal(["build", "platform:windows", "shell"], CapabilityRequirements.Parse("needs: browser\ncapabilities: shell, build\ncapabilities: build, platform:windows"));
        Assert.Empty(CapabilityRequirements.Parse("Build the shell tool. needs: browser"));
    }

    [Theory]
    [InlineData("capabilities:")]
    [InlineData("capabilities: browser")]
    [InlineData("capabilities: shell,")]
    [InlineData("capabilities: platform:")]
    [InlineData("capabilities: shell build")]
    public void Malformed_requirements_refuse(string text) => Assert.Throws<WorkerDispatchException>(() => CapabilityRequirements.Parse(text));

    [Fact]
    public async Task Same_route_evidence_allows_one_full_start_with_no_avoided_session_claim()
    {
        var request = Request("build");
        var identity = await Identity(request);
        Store.Write(identity, [Observation(identity, "build")]);
        var attempt = Assert.Single(await new WorkerLauncher(_runner, Resolve, _clock, _ => _adapter)
            .RunAsync([Candidate], _ => request, TimeSpan.FromSeconds(1), _ => Task.CompletedTask));
        Assert.True(attempt.Started);
        Assert.True(attempt.CapabilityMatch!.Allowed);
        Assert.Equal(1, attempt.FullStarts);
        Assert.Equal(0, attempt.FullSessionsAvoided);
        Assert.Equal(0, attempt.ProbeStarts);
        Assert.Equal(1, _runner.FullStarts);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("temporarily-failing")]
    public async Task Every_nonavailable_state_rejects_before_agent_identity_and_full_process(string state)
    {
        var request = Request("build");
        var identity = await Identity(request);
        Store.Write(identity, [Observation(identity, "build", state)]);
        var identities = 0;
        var attempt = Assert.Single(await new AgentLauncher(_runner, Resolve, timeProvider: _clock, adapterFor: _ => _adapter)
            .RunAsync([Candidate], _ => request, _ => { identities++; return Task.FromResult(new AgentIdentity("never", "never")); },
                TimeSpan.FromSeconds(1), _ => Task.CompletedTask));
        Assert.False(attempt.Started);
        Assert.Equal("capability_mismatch", attempt.FailureKind);
        Assert.Equal(state, Assert.Single(attempt.CapabilityMatch!.Missing).State);
        Assert.Equal(1, attempt.FullSessionsAvoided);
        Assert.Equal(0, attempt.FullStarts);
        Assert.Equal(0, identities);
        Assert.Equal(0, _runner.FullStarts);
    }

    [Fact]
    public async Task Version_configuration_base_and_launch_path_never_reuse_success()
    {
        var request = Request("shell");
        var identity = await Identity(request);
        Store.Write(identity, [Observation(identity, "shell")]);
        foreach (var other in new[]
        {
            request with { Model = "different" },
            request with { DeniedCommands = ["dotnet *"] },
            request with { Capabilities = request.Capabilities! with { BaseCommit = new string('b', 40) } },
            request with { Capabilities = request.Capabilities! with { LaunchPath = "conductor-validator" } },
        })
        {
            var inspection = await new CapabilityEvaluator(_runner, _clock, Resolve).InspectAsync(_adapter, other);
            Assert.False(inspection.Match.Allowed);
            Assert.NotEqual(identity, inspection.Identity);
        }
        _runner.Version = "fixture 2";
        Assert.False((await new CapabilityEvaluator(_runner, _clock, Resolve).InspectAsync(_adapter, request)).Match.Allowed);
    }

    [Fact]
    public async Task Expiry_and_independent_interaction_keys_fail_closed()
    {
        var identity = await Identity(Request());
        var observations = new[] { Observation(identity, "headless-interaction"), Observation(identity, "native-agent-tools") };
        Assert.False(Store.Match(identity, ["connector-interaction"], observations).Allowed);
        Assert.False(Store.Match(identity, ["native-agent-tools"], observations).Allowed);
        Assert.True(Store.Match(identity, ["headless-interaction"], observations).Allowed);
        _clock.Now = _clock.Now.AddHours(24);
        Assert.Equal("stale", Assert.Single(Store.Match(identity, ["headless-interaction"], observations).Missing).State);
    }

    [Fact]
    public async Task Cache_is_home_scoped_bounded_and_source_generated()
    {
        var identity = await Identity(Request());
        var observation = Observation(identity, "shell");
        Store.Write(identity, [observation]);
        Assert.Equal(observation, Assert.Single(Store.Read(identity).Observations));
        Assert.Empty(new CapabilityStore(Path.Combine(_root, "other")).Read(identity).Observations);
        var json = JsonSerializer.Serialize(new CapabilityInspection(identity, ["shell"], [observation], new(true, []), null), CapabilityJsonContext.Default.CapabilityInspection);
        Assert.Contains("configurationHash", json);
        File.WriteAllText(Store.PathFor(identity), new string('x', CapabilityStore.MaximumBytes + 1));
        Assert.Empty(Store.Read(identity).Observations);
        File.WriteAllText(Store.PathFor(identity), "[null]");
        Assert.NotNull(Store.Read(identity).Diagnostic);
        Assert.Throws<ArgumentException>(() => Store.Write(identity, [observation with { ExpiresAt = observation.ObservedAt.AddHours(25) }]));
        Assert.Throws<ArgumentException>(() => Store.Write(identity, [observation with { State = "temporarily-failing" }]));
    }

    [Fact]
    public async Task Empty_requirements_preserve_launch_without_version_probe()
    {
        var attempt = Assert.Single(await new WorkerLauncher(_runner, Resolve, _clock, _ => _adapter)
            .RunAsync([Candidate], _ => Request(), TimeSpan.FromSeconds(1), _ => Task.CompletedTask));
        Assert.True(attempt.Started);
        Assert.Null(attempt.CapabilityMatch);
        Assert.Equal(0, _runner.VersionReads);
    }

    [Fact]
    public async Task Production_admission_and_unsupported_paths_never_start_a_probe()
    {
        var admission = new RefusedAdmission();
        var engine = new CapabilityProbe(_runner, admission, _clock, Resolve, _ => _adapter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync("T-100", Candidate, Request("build"), TimeSpan.FromSeconds(90)));
        Assert.Equal(1, admission.Calls);
        var request = Request("build");
        var result = await engine.RunAsync("T-100", Candidate, request with { Capabilities = request.Capabilities! with { LaunchPath = "native-subagent" } }, TimeSpan.FromSeconds(90));
        Assert.Equal("capability_probe_unsupported", result.Code);
        Assert.Equal(1, admission.Calls);
        Assert.Equal(0, _runner.VersionReads);
        Assert.Equal(0, _runner.FullStarts);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Adapter : IHarnessAdapter
    {
        public string Name => "fixture";
        public string? WorkerNote => null;
        public string? CapabilityExecutable => "fixture";
        public string? CapabilitySettings(WorkerRequest request) => "fixed-fixture-settings";
        public HarnessInvocation Build(WorkerRequest request) => new("fixture", [], request.Prompt);
        public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result) => new(result.Ok, "STATUS: done", false);
    }

    private sealed class RefusedAdmission : IProbeAdmissionClient
    {
        public int Calls { get; private set; }
        public Task<ProbeAdmissionDto> AdmitAsync(ProbeAdmissionRequest request, CancellationToken ct = default)
        { Calls++; throw new InvalidOperationException("probe_budget_exhausted"); }
        public Task ReleaseAsync(ProbeReleaseRequest request, CancellationToken ct = default) => throw new InvalidOperationException("No reservation owned.");
    }

    private sealed class Runner : ICapabilityProcessRunner
    {
        public string Version { get; set; } = "fixture 1";
        public int VersionReads { get; private set; }
        public int FullStarts { get; private set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            if (arguments.Contains("--version")) { VersionReads++; return Task.FromResult(new ProcessResult(0, Version, "")); }
            if (fileName == "id") return Task.FromResult(new ProcessResult(0, "1000", ""));
            if (fileName == "git")
            {
                var output = arguments[0] switch
                {
                    "rev-parse" when arguments[1] == "HEAD" => new string('a', 40),
                    "rev-parse" => Path.Combine(workingDirectory, ".git"),
                    "config" => "safe.directory\n" + Path.GetFullPath(workingDirectory).Replace('\\', '/') + "\0",
                    _ => "",
                };
                return Task.FromResult(new ProcessResult(0, output, ""));
            }
            FullStarts++;
            return Task.FromResult(new ProcessResult(0, "STATUS: done", ""));
        }
    }
}
