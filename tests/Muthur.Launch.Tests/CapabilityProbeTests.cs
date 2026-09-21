using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Launch.Tests;

[Collection("Capability environment")]
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
        Assert.True(result.ProbeStarts == 1, $"{result.Code}: {result.Detail}");
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
            Assert.Equal("unavailable", Assert.Single(result.Observations, o => o.Capability == "test").State);
        }
        if (mode is "timeout" or "cancel") Assert.All(result.Observations, o => Assert.Equal(TimeSpan.FromMinutes(5), o.ExpiresAt - o.ObservedAt));
    }

    [Theory]
    [InlineData("init", 1)]
    [InlineData("init", 124)]
    [InlineData("commit", 1)]
    [InlineData("commit", 124)]
    public async Task Commit_setup_failure_identifies_step_and_exit_code(string step, int exitCode)
    {
        var request = await Request();
        _runner.SetupFailureStep = step;
        _runner.SetupFailureExitCode = exitCode;
        var admission = new Admission(() => Assert.Empty(Directory.EnumerateDirectories(request.ScratchDirectory)));
        var result = await new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
        Assert.Equal("capability_probe_setup_failed", result.Code);
        Assert.Equal($"Isolated commit fixture git {step} failed with exit code {exitCode}.", result.Detail);
        Assert.Equal(0, result.ProbeStarts);
        Assert.Equal(0, _runner.ModelInvocations);
        Assert.Equal(1, admission.Releases);
        Assert.Empty(result.Observations);
        Assert.False(Directory.Exists(request.Capabilities!.CacheDirectory));
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

    [Fact]
    public async Task Cancellation_during_identity_setup_releases_without_start_or_cache()
    {
        var request = await Request();
        _runner.Mode = "cancel-setup";
        var admission = new Admission();
        var result = await new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
        Assert.Equal(0, result.ProbeStarts);
        Assert.Equal(0, _runner.ModelInvocations);
        Assert.Equal(1, admission.Releases);
        Assert.Empty(result.Observations);
        Assert.False(Directory.Exists(request.Capabilities!.CacheDirectory));
    }

    [Theory]
    [InlineData("hooks")]
    [InlineData("smudge")]
    [InlineData("process")]
    [InlineData("clean")]
    public async Task Host_setup_never_runs_configured_helpers(string mode)
    {
        var request = await Request();
        var marker = Path.Combine(_root, "helper-started");
        var hooks = Path.Combine(_root, ".git", "marker-hooks");
        Directory.CreateDirectory(hooks);
        var helper = Path.Combine(hooks, "post-checkout");
        File.WriteAllText(helper, "#!/bin/sh\nprintf started > '" + marker.Replace('\\', '/') + "'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        async Task Git(params string[] args) => Assert.True((await _runner.RunAsync("git", args, _root)).Ok);
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "*.txt filter=marker\n");
        File.WriteAllText(Path.Combine(_root, "filtered.txt"), "fixture\n");
        await Git("add", ".gitattributes", "filtered.txt");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "filter fixture");
        var head = (await _runner.RunAsync("git", ["rev-parse", "HEAD"], _root)).StdOut.Trim();
        request = request with { Capabilities = request.Capabilities! with { BaseCommit = head } };
        await Git("config", "core.hooksPath", hooks);
        await Git("config", "core.fsmonitor", helper);
        await Git("config", "diff.external", helper);
        await Git("config", "submodule.recurse", "true");
        if (mode != "hooks") await Git("config", "filter.marker." + mode, helper);
        _adapter.VarySettings = true;
        var admission = new Admission();
        var result = await new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
        Assert.Equal(mode == "hooks" ? "capability_identity_unknown" : "capability_probe_checkout_filter", result.Code);
        Assert.False(File.Exists(marker));
        Assert.Equal(0, result.ProbeStarts);
        Assert.Equal(0, _runner.ModelInvocations);
        Assert.Equal(1, admission.Releases);
        Assert.False(Directory.Exists(request.Capabilities!.CacheDirectory));
        if (Directory.Exists(request.ScratchDirectory)) Assert.Empty(Directory.EnumerateDirectories(request.ScratchDirectory));
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("smudge")]
    [InlineData("process")]
    public async Task Evaluator_refuses_filters_before_identity_diff_or_full_start(string mode)
    {
        var request = await Request();
        async Task Git(params string[] args) => Assert.True((await _runner.RunAsync("git", args, _root)).Ok);
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "Directory.Build.props filter=marker\n");
        var project = Path.Combine(_root, "Directory.Build.props");
        File.WriteAllText(project, "<Project />\n");
        await Git("add", ".gitattributes", "Directory.Build.props");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "tracked identity fixture");
        var head = (await _runner.RunAsync("git", ["rev-parse", "HEAD"], _root)).StdOut.Trim();
        request = request with { Capabilities = request.Capabilities! with { BaseCommit = head } };
        var marker = Path.Combine(_root, "filter-started");
        await Git("config", "filter.marker." + mode, "echo started > '" + marker.Replace('\\', '/') + "'; cat");
        // Force diff to inspect the tracked bytes instead of trusting the index stat cache.
        File.AppendAllText(project, "<!-- changed -->\n");

        var inspection = await new CapabilityEvaluator(_runner, resolve: Resolve).InspectAsync(_adapter, request);
        Assert.Null(inspection.Identity);
        Assert.False(inspection.Match.Allowed);
        Assert.All(inspection.Match.Missing, missing => Assert.Equal("unknown", missing.State));
        Assert.Contains("capability_probe_checkout_filter", inspection.Diagnostic);
        Assert.False(File.Exists(marker));
        Assert.Equal(0, _runner.ModelInvocations);
        Assert.False(Directory.Exists(request.Capabilities.CacheDirectory));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("link")]
    [InlineData("broken-link")]
    public async Task Reserved_entries_refuse_without_following_links_and_release_after_cleanup(string entry)
    {
        var request = await Request();
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        var marker = Path.Combine(external, "marker");
        File.WriteAllText(marker, "preserve");
        _runner.ReservedEntry = entry;
        _runner.ExternalDirectory = external;
        var admission = new Admission(() => Assert.Empty(Directory.EnumerateDirectories(request.ScratchDirectory)));
        var result = await new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
        Assert.Equal("capability_probe_reserved_path", result.Code);
        Assert.Equal(0, result.ProbeStarts);
        Assert.Equal(0, _runner.ModelInvocations);
        Assert.Equal(1, admission.Releases);
        Assert.Equal("preserve", File.ReadAllText(marker));
        Assert.Single(Directory.EnumerateFileSystemEntries(external));
        Assert.False(Directory.Exists(request.Capabilities!.CacheDirectory));
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
        public string? ReservedEntry { get; set; }
        public string? ExternalDirectory { get; set; }
        public string? SetupFailureStep { get; set; }
        public int SetupFailureExitCode { get; set; }
        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            if (Mode == "cancel-setup" && arguments.Contains("--version")) throw new OperationCanceledException();
            if (SetupFailureStep is not null && arguments.Contains(SetupFailureStep) &&
                workingDirectory.EndsWith(Path.Combine(".muthur-capability", "commit"), StringComparison.Ordinal))
                return new(SetupFailureExitCode, "private stdout", "private stderr");
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
            var run = await _real.RunAsync(fileName, arguments, workingDirectory, stdin, timeout ?? TimeSpan.FromSeconds(10), ct, scrubEnvironment, isolated);
            if (run.Ok && ReservedEntry is not null && arguments.Contains("worktree") && arguments.Contains("add"))
            {
                var worktree = arguments[arguments.ToList().IndexOf("--detach") + 1];
                var reserved = Path.Combine(worktree, ".muthur-capability");
                if (ReservedEntry == "file") File.WriteAllText(reserved, "reserved");
                else if (ReservedEntry == "directory") Directory.CreateDirectory(reserved);
                else
                {
                    var target = ReservedEntry == "link" ? ExternalDirectory! : Path.Combine(ExternalDirectory!, "missing");
                    if (OperatingSystem.IsWindows())
                    {
                        var link = await _real.RunAsync("cmd.exe", ["/c", "mklink", "/J", reserved, target], workingDirectory, ct: ct);
                        Assert.True(link.Ok, link.Message);
                    }
                    else Directory.CreateSymbolicLink(reserved, target);
                }
            }
            return run;
        }
    }
}
