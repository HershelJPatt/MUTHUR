using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

[CollectionDefinition("Session minutes process environment", DisableParallelization = true)]
public sealed class SessionMinutesProcessEnvironment;

/// <summary>
/// An orchestrator session and a validator session share the same launch machinery but not the same wall clock
/// (U3.3): a validator checks out one commit and runs the spec's verification, so it is capped shorter. This never
/// runs a real harness — resolution needs a file on PATH, and <see cref="CapturingProcessRunner"/> intercepts
/// before anything actually spawns — so what it proves is the wiring: each launcher asks <see cref="AgentLauncher"/>
/// for its own configured minutes, not the other one's.
/// </summary>
[Collection("Session minutes process environment")]
public sealed class ConductorSessionMinutesTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
    private readonly CapturingProcessRunner _processes = new();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        _hub.Dispose();
        _repo.Dispose();
    }

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public List<TimeSpan?> Timeouts { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            lock (Timeouts) Timeouts.Add(timeout);
            return Task.FromResult(new ProcessResult(1, "", "fixture never really runs"));
        }
    }

    [Fact]
    public async Task An_orchestrator_and_a_validator_are_capped_by_their_own_configured_minutes()
    {
        _hub.Settings["Muthur:ConductorOrchestratorMinutes"] = "45";
        _hub.Settings["Muthur:ConductorValidatorMinutes"] = "30";
        await _hub.AddProjectAsync(repoPath: _repo.Path);

        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec()))).EnsureSuccessStatusCode();
        _repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        // Resolution needs a file, but CapturingProcessRunner intercepts execution: no harness is actually launched.
        File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile),
            """{"tiers":{"mastermind":[{"harness":"codex","model":"fixture","account":"account"}],"implementer":[{"harness":"codex","model":"fixture","account":"account"}]}}""");
        File.WriteAllText(Path.Combine(_hub.DataDir, OperatingSystem.IsWindows() ? "codex.cmd" : "codex"), "fixture");
        Environment.SetEnvironmentVariable("PATH", _hub.DataDir + Path.PathSeparator + _path);

        var taskId = int.Parse(task.Id.AsSpan(2));

        var orchestrator = ActivatorUtilities.CreateInstance<OrchestratorSessionLauncher>(_hub.Services, (IProcessRunner)_processes);
        try { await orchestrator.StartAsync(new OrchestratorAssignment(taskId, task.Id, "Feature", "demo")); }
        catch { /* the fixture reports failure on purpose; only the requested timeout matters here */ }

        var validator = ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services, (IProcessRunner)_processes);
        try { await validator.StartAsync(new ConductorAssignment(taskId, task.Id, "Feature", "demo", "win-validator", AvoidHarness: null)); }
        catch { /* same fixture, same reason */ }

        Assert.Equal(2, _processes.Timeouts.Count);
        Assert.Equal(TimeSpan.FromMinutes(45), _processes.Timeouts[0]);
        Assert.Equal(TimeSpan.FromMinutes(30), _processes.Timeouts[1]);
    }
}
