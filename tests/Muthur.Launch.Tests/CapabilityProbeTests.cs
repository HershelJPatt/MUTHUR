using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Launch.Tests;

[Collection("Capability environment")]
public sealed class CapabilityProbeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muthur-tests", "capability-probe-" + Guid.NewGuid().ToString("n"));
    private readonly FixtureRunner _runner = new();
    private readonly FixtureAdapter _adapter = new();
    private bool _repositoryInitialized;
    private bool _cleanupCompleted;
    public CapabilityProbeTests() => Directory.CreateDirectory(_root);
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => CleanupAsync();

    private async Task CleanupAsync()
    {
        if (_cleanupCompleted) return;
        AssertRealCallsCompleted();
        var boundary = Path.GetFullPath(Path.Combine(_root, "scratch")) + Path.DirectorySeparatorChar;
        foreach (var worktree in _runner.AddedWorktrees)
        {
            var full = Path.GetFullPath(worktree);
            Assert.True(full.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                $"Recorded worktree is outside fixture scratch: {full}");
            for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                if (Path.Exists(current)) Assert.False((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0, $"Linked cleanup path: {current}");
                if (current == Path.GetFullPath(_root)) break;
            }
            if (Directory.Exists(full)) await _runner.CleanupGitAsync(_root, "worktree", "remove", "--force", "--force", full);
            Assert.False(Path.Exists(full), $"Worktree remains after cleanup: {full}");
        }
        if (_repositoryInitialized)
        {
            var listing = await _runner.CleanupGitAsync(_root, "worktree", "list", "--porcelain");
            foreach (var worktree in _runner.AddedWorktrees)
                Assert.DoesNotContain("worktree " + worktree.Replace('\\', '/'), listing.StdOut);
        }
        AssertRealCallsCompleted();
        Muthur.XmlDocCheck.Tests.RepositoryFixtureCleanup.Delete(_root);
        Assert.False(Directory.Exists(_root));
        _cleanupCompleted = true;
    }

    private void AssertRealCallsCompleted()
    {
        Assert.True(_runner.UncertainRealCall is null, $"Real process ownership is uncertain: {_runner.UncertainRealCall}\n{_runner.CommandDiagnostics}");
        Assert.True(_runner.RealCallsInFlight == 0, $"Expected 0 real calls in flight, actual {_runner.RealCallsInFlight}.\n{_runner.CommandDiagnostics}");
        Assert.True(_runner.RealCallsStarted == _runner.RealCallsCompleted,
            $"Expected {_runner.RealCallsStarted} completed real calls, actual {_runner.RealCallsCompleted}.\n{_runner.CommandDiagnostics}");
    }

    private (string, IReadOnlyList<string>)? Resolve(string name) => name == "fixture" ? (Path.Combine(_root, "fixture"), []) : ExecutableResolver.Resolve(name);
    private async Task<ProcessResult> Git(params string[] arguments)
    {
        var result = await _runner.RunAsync("git", arguments, _root);
        Assert.True(result.Ok, $"Fixture git {string.Join(' ', arguments)} in {_root} failed with exit code {result.ExitCode}.\n{_runner.CommandDiagnostics}");
        return result;
    }

    private async Task<WorkerRequest> Request()
    {
        await Git("init", "--quiet");
        _repositoryInitialized = true;
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
        Assert.True(result.ProbeStarts == 1, $"{result.Code}: {result.Detail}\n{_runner.CommandDiagnostics}");
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
    [InlineData("guard-refuses", "available")]
    [InlineData("execute", "unavailable")]
    public async Task A_guarded_harness_proves_its_deny_list_by_the_refusal_in_its_stream(string mode, string expected)
    {
        _runner.Mode = mode;
        _adapter.GuardBlockMarker = PiGuard.BlockMarker;
        var request = await Request();
        var result = await new CapabilityProbe(_runner, new Admission(), resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));

        Assert.True(result.ProbeStarts == 1, $"{result.Code}: {result.Detail}");
        Assert.Equal(expected, Assert.Single(result.Observations, o => o.Capability == "deny-list").State);
        Assert.Equal("available", Assert.Single(result.Observations, o => o.Capability == "build").State);
        var cached = new CapabilityStore(request.Capabilities!.CacheDirectory).Read(result.Observations[0].Identity).Observations;
        Assert.Contains(cached, o => o.Capability == "deny-list" && o.State == expected);
    }

    [Theory]
    [InlineData("execute", "available")]
    [InlineData("denied-build", "unavailable")]
    public async Task A_harness_whose_shell_is_sh_runs_every_row_in_its_bash_form(string mode, string expectedBuild)
    {
        _runner.Mode = mode;
        _runner.Shell = CapabilityShell.Posix;
        _adapter.ProbeShell = CapabilityShell.Posix;
        var request = await Request();
        var result = await new CapabilityProbe(_runner, new Admission(), resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));

        Assert.True(result.ProbeStarts == 1, $"{result.Code}: {result.Detail}\n{_runner.CommandDiagnostics}");
        Assert.Equal(expectedBuild, Assert.Single(result.Observations, o => o.Capability == "build").State);
        foreach (var row in new[] { "shell", "worktree-base", "test", "commit" }.Where(r => mode == "execute" || r != "test"))
            Assert.True(Assert.Single(result.Observations, o => o.Capability == row).State == "available", $"{row}\n{_runner.CommandDiagnostics}");
        var prompt = Assert.Single(_adapter.Requests).Prompt;
        Assert.Contains("standalone bash shell-tool command", prompt);
        Assert.DoesNotContain("LASTEXITCODE", prompt);
        Assert.DoesNotContain("Set-Content", prompt);
    }

    [Fact]
    public void Pi_rows_carry_the_bash_form_and_claude_and_codex_rows_are_unchanged()
    {
        Assert.Equal(CapabilityShell.Posix, Harnesses.Find("pi")!.ProbeShell);
        foreach (var name in new[] { "claude", "codex", "codex-oss" })
            Assert.Equal(CapabilityShell.PowerShell, Harnesses.Find(name)!.ProbeShell);
        var fixture = Path.Combine(_root, "fix ture");
        var powershell = CapabilityFixture.Steps(fixture, "n0nce", denyList: true);
        Assert.Equal(powershell, CapabilityFixture.Steps(fixture, "n0nce", denyList: true, shell: CapabilityShell.PowerShell));
        Assert.All(powershell, s => Assert.Contains("ConvertTo-Json", s.Receipt));
        var posix = CapabilityFixture.Steps(fixture, "n0nce", denyList: true, shell: CapabilityShell.Posix);
        Assert.Equal(powershell.Select(s => s.Key), posix.Select(s => s.Key));
        Assert.All(posix, s => Assert.StartsWith("printf ", s.Receipt));
        Assert.All(posix, s => Assert.DoesNotContain("LASTEXITCODE", s.Command + s.Receipt));
        // Paths go to sh with forward slashes, which Git Bash and the native tools it starts both read.
        Assert.All(posix, s => Assert.Contains($"'{fixture.Replace('\\', '/')}/{s.Key}.receipt.json'", s.Receipt));
        Assert.DoesNotContain(posix, s => s.Command.Contains(fixture, StringComparison.Ordinal) && fixture.Contains('\\'));
        Assert.Equal(CapabilityFixture.DeniedProbeCommand, Assert.Single(posix, s => s.Key == "deny-list").Command);
    }

    [Fact]
    public async Task A_harness_that_enforces_its_own_deny_list_is_not_asked_to_run_a_denied_command()
    {
        var request = await Request();
        var result = await new CapabilityProbe(_runner, new Admission(), resolve: Resolve, adapterFor: _ => _adapter)
            .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));

        Assert.True(result.ProbeStarts == 1, $"{result.Code}: {result.Detail}");
        Assert.DoesNotContain(result.Observations, o => o.Capability == "deny-list");
        Assert.DoesNotContain(CapabilityFixture.DeniedProbeCommand, Assert.Single(_adapter.Requests).Prompt);
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
        => await AssertRetainedReservationAsync(mode);

    [Theory]
    [InlineData("cleanup")]
    [InlineData("process-cleanup")]
    public async Task Test_owned_cleanup_removes_retained_worktree_without_releasing_reservation(string mode)
    {
        var (admission, worktree) = await AssertRetainedReservationAsync(mode);
        await CleanupAsync();
        Assert.False(Directory.Exists(worktree));
        Assert.False(Directory.Exists(_root));
        Assert.Equal(0, admission.Releases);
        Assert.Equal(1, _runner.RealRemoveAttempts);
        AssertRealCallsCompleted();
    }

    [Theory]
    [InlineData("init")]
    [InlineData("commit")]
    public async Task Uncertain_cleanup_setup_failure_reports_real_command(string step)
    {
        var request = await Request();
        _runner.Mode = "cleanup";
        _runner.SetupFailureStep = step;
        _runner.SetupFailureExitCode = 124;
        var admission = new Admission();
        var error = await Assert.ThrowsAsync<Xunit.Sdk.TrueException>(() => AssertRetainedReservationAsync("cleanup", request, admission));
        Assert.Contains("Expected 1 model invocation, actual 0", error.Message);
        Assert.Contains("Real command:", error.Message);
        Assert.Contains($"Simulated setup failure: git {step}", error.Message);
        Assert.Contains("exit code 124", error.Message);
        Assert.DoesNotContain("private stdout", error.Message);
        Assert.DoesNotContain("private stderr", error.Message);
        AssertRealCallsCompleted();
        var worktree = Assert.Single(_runner.AddedWorktrees);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal(1, _runner.SimulatedRemoveAttempts);
        Assert.Equal(0, _runner.RealRemoveAttempts);
        await CleanupAsync();
        Assert.False(Directory.Exists(worktree));
        Assert.False(Directory.Exists(_root));
        Assert.Equal(0, admission.Releases);
    }

    private async Task<(Admission Admission, string Worktree)> AssertRetainedReservationAsync(string mode, WorkerRequest? request = null, Admission? admission = null)
    {
        request ??= await Request();
        _runner.Mode = mode;
        admission ??= new Admission();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var result = await new CapabilityProbe(_runner, admission, resolve: Resolve, adapterFor: _ => _adapter)
                .RunAsync("T-100", new("fixture", "fixture", "fixture"), request, TimeSpan.FromSeconds(90));
            Assert.Fail($"Probe returned instead of retaining the reservation: {result.Code}: {result.Detail}\n{_runner.CommandDiagnostics}");
        });
        Assert.IsType<ProbeCleanupUncertainException>(error.InnerException);
        Assert.Contains("retained", error.Message);
        Assert.Equal(0, admission.Releases);
        Assert.False(Directory.Exists(request.Capabilities!.CacheDirectory));
        Assert.True(_runner.ModelInvocations == 1, $"Expected 1 model invocation, actual {_runner.ModelInvocations}.\n{_runner.CommandDiagnostics}");
        Assert.True(_runner.RealCallsStarted > 0);
        AssertRealCallsCompleted();
        var worktree = Assert.Single(_runner.AddedWorktrees);
        Assert.True(Directory.Exists(worktree));
        Assert.Equal(mode == "cleanup" ? 1 : 0, _runner.SimulatedRemoveAttempts);
        Assert.Equal(0, _runner.RealRemoveAttempts);
        return (admission, worktree);
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
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "*.txt filter=marker\n");
        File.WriteAllText(Path.Combine(_root, "filtered.txt"), "fixture\n");
        await Git("add", ".gitattributes", "filtered.txt");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "filter fixture");
        var head = (await Git("rev-parse", "HEAD")).StdOut.Trim();
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
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "Directory.Build.props filter=marker\n");
        var project = Path.Combine(_root, "Directory.Build.props");
        File.WriteAllText(project, "<Project />\n");
        await Git("add", ".gitattributes", "Directory.Build.props");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "tracked identity fixture");
        var head = (await Git("rev-parse", "HEAD")).StdOut.Trim();
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

    /// <summary>The sh pi runs on this machine: Git Bash on Windows (never the WSL launcher in System32), bash elsewhere.</summary>
    internal static string PosixShell()
    {
        if (!OperatingSystem.IsWindows()) return "bash";
        var git = ExecutableResolver.Resolve("git") ?? throw new InvalidOperationException("Git for Windows is required.");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(git.FileName))!;
        foreach (var candidate in new[] { Path.Combine(root, "bin", "bash.exe"), Path.Combine(Path.GetDirectoryName(root)!, "bin", "bash.exe") })
            if (File.Exists(candidate)) return candidate;
        throw new InvalidOperationException($"Git Bash not found beside {git.FileName}.");
    }

    private sealed class FixtureAdapter : IHarnessAdapter
    {
        public string Name => "fixture";
        public string? WorkerNote => null;
        public string? CapabilityExecutable => "fixture";
        public bool VarySettings { get; set; }
        public List<WorkerRequest> Requests { get; } = [];
        public string? CapabilitySettings(WorkerRequest request) => VarySettings ? request.WorkingDirectory : "fixture-scope";
        public string? GuardBlockMarker { get; set; }
        public CapabilityShell ProbeShell { get; set; }
        public HarnessInvocation Build(WorkerRequest request)
        { Requests.Add(request); return new("fixture", [], request.Prompt); }
        public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result) => new(result.Ok, "simulated", false);
    }

    private sealed class FixtureRunner : ICapabilityProcessRunner
    {
        private readonly CapabilityProcessRunner _real = new();
        private readonly List<string> _addedWorktrees = [];
        private readonly List<string> _commandObservations = [];
        public string CommandDiagnostics => string.Join(Environment.NewLine, _commandObservations);
        public IReadOnlyList<string> AddedWorktrees => _addedWorktrees.AsReadOnly();
        public int RealCallsStarted { get; private set; }
        public int RealCallsCompleted { get; private set; }
        public int RealCallsInFlight { get; private set; }
        public string? UncertainRealCall { get; private set; }
        public int SimulatedRemoveAttempts { get; private set; }
        public int RealRemoveAttempts { get; private set; }
        public string Mode { get; set; } = "execute";
        /// <summary>The shell the simulated harness runs each step in, as the harness's own shell tool would.</summary>
        public CapabilityShell Shell { get; set; }
        public int ModelInvocations { get; private set; }
        public string? ReservedEntry { get; set; }
        public string? ExternalDirectory { get; set; }
        public string? SetupFailureStep { get; set; }
        public int SetupFailureExitCode { get; set; }

        public async Task<ProcessResult> CleanupGitAsync(string workingDirectory, params string[] arguments)
        {
            // Simulated uncertainty retains production ownership; real calls still confirm exit and pipe drain.
            string[] command = ["-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
                "-c", "core.fsmonitor=false", "-c", "submodule.recurse=false", "-c", "diff.external=", .. arguments];
            var result = await RunRealAsync("git", command, workingDirectory, timeout: TimeSpan.FromSeconds(30));
            Assert.True(result.Ok, $"Fixture cleanup git {string.Join(' ', command)} in {workingDirectory} failed with exit code {result.ExitCode}: {result.Message}");
            return result;
        }

        private async Task<ProcessResult> RunRealAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var isolated = new Dictionary<string, string>(environment ?? new Dictionary<string, string>())
            {
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            };
            RealCallsStarted++;
            RealCallsInFlight++;
            if (fileName == "git" && arguments.Contains("worktree") && arguments.Contains("remove")) RealRemoveAttempts++;
            var uncertain = false;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            int? exitCode = null;
            string? exceptionType = null;
            try
            {
                var result = await _real.RunAsync(fileName, arguments, workingDirectory, stdin, timeout, ct, scrubEnvironment, isolated);
                exitCode = result.ExitCode;
                if (result.Ok && fileName == "git" && arguments.Contains("worktree") && arguments.Contains("add"))
                    _addedWorktrees.Add(Path.GetFullPath(arguments[arguments.ToList().IndexOf("--detach") + 1], workingDirectory));
                return result;
            }
            catch (Exception error)
            {
                exceptionType = error.GetType().FullName;
                uncertain = error is ProbeCleanupUncertainException;
                if (uncertain) UncertainRealCall = $"{fileName} {string.Join(' ', arguments)} in {workingDirectory}";
                throw;
            }
            finally
            {
                _commandObservations.Add($"Real command: {fileName} {string.Join(' ', arguments)} in {workingDirectory}; exit code {exitCode?.ToString() ?? "none"}; requested timeout {timeout?.ToString() ?? "default"}; elapsed {elapsed.Elapsed.TotalSeconds:F1}s; exception {exceptionType ?? "none"}.");
                if (!uncertain) RealCallsCompleted++;
                RealCallsInFlight--;
            }
        }

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            if (Mode == "cancel-setup" && arguments.Contains("--version")) throw new OperationCanceledException();
            if (SetupFailureStep is not null && arguments.Contains(SetupFailureStep) &&
                workingDirectory.EndsWith(Path.Combine(".muthur-capability", "commit"), StringComparison.Ordinal))
            {
                _commandObservations.Add($"Simulated setup failure: {fileName} {SetupFailureStep}; arguments {string.Join(' ', arguments)} in {workingDirectory}; exit code {SetupFailureExitCode}; requested timeout {timeout?.ToString() ?? "default"}.");
                return new(SetupFailureExitCode, "private stdout", "private stderr");
            }
            if (Mode == "cleanup" && arguments.Contains("worktree") && arguments.Contains("remove"))
            {
                SimulatedRemoveAttempts++;
                return new(1, "", "simulated cleanup failure");
            }
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
                var refusals = "";
                foreach (var step in steps)
                {
                    if (step.Key == "deny-list" && Mode == "guard-refuses")
                    {
                        // What pi's stream carries for a blocked call: the reason, inside a JSON string.
                        refusals += $$$"""{"type":"message_end","message":{"role":"toolResult","content":[{"type":"text","text":"muthur-guard: blocked \"{{{step.Command}}}\" matches the denied pattern \"git push*\"."}]}}""" + "\n";
                        continue;
                    }
                    var command = Mode == "denied-build" && step.Key == "build" ? "$global:LASTEXITCODE = 126" : step.Command;
                    var result = Shell == CapabilityShell.Posix
                        ? await RunRealAsync(PosixShell(), ["-c", (Mode == "denied-build" && step.Key == "build" ? "(exit 126)" : step.Command) + "\n" + step.Receipt],
                            workingDirectory, timeout: timeout, ct: ct, scrubEnvironment: scrubEnvironment, environment: environment)
                        : await RunRealAsync("pwsh", ["-NoProfile", "-Command", command + "\n" + step.Receipt], workingDirectory,
                            timeout: timeout, ct: ct, scrubEnvironment: scrubEnvironment, environment: environment);
                    Assert.True(result.Ok, result.Message);
                }
                if (Mode == "forged-nonce") File.WriteAllText(Path.Combine(fixture, "build.receipt.json"), "{\"nonce\":\"wrong\",\"exitCode\":0}");
                if (Mode == "tampered-fixture") File.AppendAllText(Path.Combine(fixture, "fixture.proj"), "<!-- changed -->");
                return new(0, refusals + "Simulated harness, real deterministic SDK fixture commands.", "");
            }
            var run = await RunRealAsync(fileName, arguments, workingDirectory, stdin, timeout ?? TimeSpan.FromSeconds(30), ct, scrubEnvironment, environment);
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
                        var link = await RunRealAsync("cmd.exe", ["/c", "mklink", "/J", reserved, target], workingDirectory, timeout: TimeSpan.FromSeconds(30), ct: ct);
                        Assert.True(link.Ok, link.Message);
                    }
                    else Directory.CreateSymbolicLink(reserved, target);
                }
            }
            return run;
        }
    }
}
