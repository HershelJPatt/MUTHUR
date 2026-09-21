using System.Text.Json;

namespace Muthur.Launch.Tests;

public sealed class ThroughputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muthur-tests", "throughput-" + Guid.NewGuid().ToString("n"));
    private readonly ProcessRunner _processes = new();
    public ThroughputTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        Muthur.XmlDocCheck.Tests.RepositoryFixtureCleanup.Delete(_root);
    }

    private async Task<string> Git(params string[] args)
    {
        var result = await _processes.RunAsync("git", args, _root);
        Assert.True(result.Ok, result.Message);
        return result.StdOut.Trim();
    }

    private async Task Initialize()
    {
        await Git("init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(_root, "spec.md"), "Frozen spec.");
        await Git("add", "spec.md");
        await Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "Spec");
        await Git("checkout", "-b", "task/source");
    }

    [Fact]
    public async Task Assignment_is_bound_to_the_caller_spec_and_named_base_and_detects_movement()
    {
        await Initialize();
        var worktree = Path.Combine(_root, "child");
        var assignment = await WorkerAssignment.ResolveAsync(_processes, _root, "HEAD", "main", "spec.md", "worker/test", worktree);
        Assert.Equal("task/source", assignment.BaseBranch);
        Assert.Equal(await Git("rev-parse", "HEAD"), assignment.BaseCommit);
        Assert.Equal(await Git("rev-parse", "HEAD:spec.md"), assignment.SpecBlob);
        await Git("worktree", "add", "-b", assignment.WorkerBranch, worktree, assignment.BaseCommit);
        await assignment.VerifyCreatedAsync(_processes);
        var prompt = WorkerPrompt.Compose("contract", "spec.md", null, assignment.WorkerBranch, ["verify all"], null, assignment);
        foreach (var value in new[] { assignment.BaseCommit, assignment.BaseBranch, assignment.DefaultBranch, assignment.SpecBlob, worktree, "verify all" })
            Assert.Contains(value, prompt);
        await File.WriteAllTextAsync(Path.Combine(_root, "spec.md"), "Amended spec.");
        await Git("add", "spec.md");
        await Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "Amend");
        Assert.Equal("base_moved", (await Assert.ThrowsAsync<WorkerDispatchException>(() => assignment.VerifyCreatedAsync(_processes))).Code);
    }

    [Theory]
    [InlineData("missing", "main", "spec.md")]
    [InlineData("task/source", "missing", "spec.md")]
    [InlineData("task/source", "main", "missing.md")]
    [InlineData("task/source", "main", "../spec.md")]
    [InlineData("task/source", "main", ".git")]
    public async Task Incomplete_assignments_refuse_before_worktree_or_inference(string source, string defaultBranch, string spec)
    {
        await Initialize();
        var worktree = Path.Combine(_root, "child");
        await Assert.ThrowsAsync<WorkerDispatchException>(() => WorkerAssignment.ResolveAsync(_processes, _root, source, defaultBranch, spec, "worker/test", worktree));
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task Detached_HEAD_is_not_a_named_base()
    {
        await Initialize();
        await Git("checkout", "--detach");
        await Assert.ThrowsAsync<WorkerDispatchException>(() => WorkerAssignment.ResolveAsync(_processes, _root, "HEAD", "main", "spec.md", "worker/test", Path.Combine(_root, "child")));
    }

    [Fact]
    public async Task Git_trust_is_exact_process_local_and_preserves_existing_configuration()
    {
        await Initialize();
        var inherited = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "test.marker", ["GIT_CONFIG_VALUE_0"] = "kept" };
        var environment = SessionWorkspace.GitEnvironment(_root, key => inherited.GetValueOrDefault(key));
        Assert.Equal("2", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal(_root.Replace('\\', '/'), environment["GIT_CONFIG_VALUE_1"]);
        Assert.DoesNotContain("*", environment.Values);
        var result = await _processes.RunAsync("git", ["config", "--get", "test.marker"], _root, environment: environment);
        Assert.Equal("kept", result.StdOut.Trim());
        result = await _processes.RunAsync("git", ["config", "--get", "safe.directory"], _root, environment: environment);
        Assert.Equal(_root.Replace('\\', '/'), result.StdOut.Trim());
        var request = new WorkerRequest(_root, "prompt", "test", null, [], [], _root, GitEnvironment: environment);
        var invocation = new CodexAdapter("codex", null).Build(request);
        Assert.Contains("workspace-write", invocation.Arguments);
        Assert.Contains($"shell_environment_policy.set.GIT_CONFIG_VALUE_1=\"{JsonEncodedText.Encode(_root.Replace('\\', '/'))}\"", invocation.Arguments);
        Assert.Equal("1", inherited["GIT_CONFIG_COUNT"]);
        Assert.DoesNotContain(invocation.Arguments, a => a.Contains("test.marker") || a.Contains("kept"));
        var differentOwner = new Dictionary<string, string> { ["GIT_TEST_ASSUME_DIFFERENT_OWNER"] = "1" };
        Assert.False((await _processes.RunAsync("git", ["status", "--porcelain"], _root, environment: differentOwner)).Ok);
        foreach (var pair in environment) differentOwner[pair.Key] = pair.Value;
        Assert.True((await _processes.RunAsync("git", ["status", "--porcelain"], _root, environment: differentOwner)).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_attempts_cannot_reuse_an_old_final_report(bool agent)
    {
        var runner = new CapturingRunner();
        var request = new WorkerRequest(_root, "prompt", "test", null, [], [], _root);
        async Task<IReadOnlyList<WorkerAttempt>> Run() => agent
            ? await new AgentLauncher(runner, _ => ("fake", [])).RunAsync([new("codex", "test", null)], _ => request,
                _ => Task.FromResult(new AgentIdentity("test", "token")), TimeSpan.FromMinutes(1), _ => Task.CompletedTask)
            : await new WorkerLauncher(runner, _ => ("fake", []), workerProcesses: new ContainedFixture(runner), admission: AdmittedFixture.Context(_root)).RunAsync([new("codex", "test", null)], _ => request,
                TimeSpan.FromMinutes(1), _ => Task.CompletedTask);
        var first = Assert.Single(await Run());
        var second = Assert.Single(await Run());
        Assert.True(first.Outcome.Success);
        Assert.False(second.Outcome.Success);
        Assert.Equal("timeout", second.FailureKind);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(runner.Files[0], runner.Files[1]);
        Assert.Contains("Timed out", second.Outcome.Report);
        Assert.DoesNotContain("old success", second.Outcome.Report);
        if (!agent) Assert.Contains("MUTHUR_TOKEN", runner.Scrubbed!);
    }

    [Fact]
    public void A_process_failure_is_not_hidden_by_its_final_message()
    {
        File.WriteAllText(Path.Combine(_root, "codex-last-message.txt"), "STATUS: done");
        var outcome = new CodexAdapter("codex", null).Interpret(new(_root, "prompt", "test", null, [], [], _root), new(124, "", "Timed out"));
        Assert.False(outcome.Success);
        Assert.Contains("Timed out", outcome.Report);
        Assert.Contains("STATUS: done", outcome.Report);
    }

    private sealed class CapturingRunner : IProcessRunner
    {
        public List<string> Files { get; } = [];
        public IReadOnlyCollection<string>? Scrubbed { get; private set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? stdin = null,
            TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Scrubbed = scrubEnvironment;
            var path = arguments[arguments.ToList().IndexOf("--output-last-message") + 1];
            Files.Add(path);
            if (Files.Count == 1) File.WriteAllText(path, "STATUS: done\nold success");
            return Task.FromResult(Files.Count == 1 ? new ProcessResult(0, "", "") : new ProcessResult(124, "", "Timed out"));
        }
    }
}
