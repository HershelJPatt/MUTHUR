using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Tests;

public sealed class WorkerBaseTests
{
    [Fact]
    public async Task Omitted_base_starts_a_worker_at_the_callers_branch()
    {
        using var repository = new GitFixture();
        await repository.InitializeAsync();
        var discovered = await repository.GitAsync(repository.Nested, "rev-parse", "--show-toplevel");
        Assert.Equal(Path.GetFullPath(repository.Caller), Path.GetFullPath(discovered));

        var resolved = await WorkerCommands.ResolveBaseAsync(repository.Processes, discovered, null, CancellationToken.None);
        Assert.Equal("task/caller", resolved);
        await repository.GitAsync(discovered, "worktree", "add", "-b", "worker/test", repository.Worker, resolved);

        var callerHead = await repository.GitAsync(repository.Caller, "rev-parse", "HEAD");
        var mainHead = await repository.GitAsync(repository.Main, "rev-parse", "HEAD");
        Assert.NotEqual(mainHead, callerHead);
        Assert.Equal(callerHead, await repository.GitAsync(repository.Worker, "rev-parse", "HEAD"));
        Assert.Equal("caller", await File.ReadAllTextAsync(Path.Combine(repository.Worker, "marker")));
    }

    [Fact]
    public async Task Explicit_main_overrides_the_callers_branch()
    {
        using var repository = new GitFixture();
        await repository.InitializeAsync();
        var discovered = await repository.GitAsync(repository.Nested, "rev-parse", "--show-toplevel");

        Assert.Equal("main", await WorkerCommands.ResolveBaseAsync(repository.Processes, discovered, "main", CancellationToken.None));
    }

    [Theory]
    [InlineData(1, "", "HEAD")]
    [InlineData(0, "  task/caller\r\n", "task/caller")]
    [InlineData(0, "", "")]
    public async Task Branch_lookup_preserves_git_results_and_process_options(int exitCode, string stdout, string expected)
    {
        using var cancellation = new CancellationTokenSource();
        var repository = Path.Combine(Path.GetTempPath(), "caller");
        var processes = new RecordingRunner();
        processes.Expect(repository, ["branch", "--show-current"], new ProcessResult(exitCode, stdout, ""), cancellation.Token);

        Assert.Equal(expected, await WorkerCommands.ResolveBaseAsync(processes, repository, null, cancellation.Token));
        processes.AssertComplete();
    }

    [Theory]
    [InlineData("task/explicit")]
    [InlineData("HEAD")]
    [InlineData("")]
    public async Task Explicit_base_is_unchanged_and_bypasses_git(string explicitBase)
    {
        var processes = new RecordingRunner();
        Assert.Equal(explicitBase, await WorkerCommands.ResolveBaseAsync(processes, "unused", explicitBase, CancellationToken.None));
        processes.AssertComplete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_action_resolves_base_in_the_discovered_worktree(bool explicitBase)
    {
        var shared = Path.Combine(Path.GetTempPath(), "worker-base-fake", "main");
        var caller = Path.Combine(shared, ".worktrees", "caller");
        var processes = new RecordingRunner();
        processes.Expect(Environment.CurrentDirectory, ["rev-parse", "--show-toplevel"], new ProcessResult(0, caller, ""));
        processes.Expect(caller, ["rev-parse", "--git-common-dir"], new ProcessResult(0, Path.Combine(shared, ".git"), ""));
        if (!explicitBase)
            processes.Expect(caller, ["branch", "--show-current"], new ProcessResult(0, "task/caller", ""));

        var root = new RootCommand("test");
        Globals.AddTo(root);
        WorkerCommands.AddTo(root, processes);
        string[] args = explicitBase
            ? ["worker", "run", "--spec", "specs/example.md", "--base", "main"]
            : ["worker", "run", "--spec", "specs/example.md"];
        var parse = root.Parse(args);
        Assert.Empty(parse.Errors);
        Assert.Equal(ExitCodes.NotRunning, await parse.InvokeAsync());
        processes.AssertComplete();
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly Queue<(string Directory, string[] Arguments, ProcessResult Result, CancellationToken? Token)> expected = new();

        public void Expect(string directory, string[] arguments, ProcessResult result, CancellationToken? token = null) =>
            expected.Enqueue((directory, arguments, result, token));

        public void AssertComplete() => Assert.Empty(expected);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Assert.NotEmpty(expected);
            var next = expected.Dequeue();
            Assert.Equal("git", fileName);
            Assert.Equal(next.Arguments, arguments);
            Assert.Equal(next.Directory, workingDirectory);
            Assert.Equal(TimeSpan.FromMinutes(1), timeout);
            if (next.Token is { } token) Assert.Equal(token, ct);
            Assert.Null(stdin);
            Assert.Null(scrubEnvironment);
            Assert.Null(environment);
            return Task.FromResult(next.Result);
        }
    }

    private sealed class GitFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "muthur-worker-base-tests", Guid.NewGuid().ToString("n"));
        public ProcessRunner Processes { get; } = new();
        public string Main => Path.Combine(root, "main");
        public string Caller => Path.Combine(root, "caller");
        public string Nested => Path.Combine(Caller, "nested", "directory");
        public string Worker => Path.Combine(root, "worker");

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(Main);
            await GitAsync(Main, "init", "-b", "main");
            Directory.CreateDirectory(Path.Combine(Main, "specs"));
            await File.WriteAllTextAsync(Path.Combine(Main, "specs", "example.md"), "Frozen spec");
            await GitAsync(Main, "add", "specs/example.md");
            await CommitAsync(Main, "Add spec");
            await GitAsync(Main, "worktree", "add", "-b", "task/caller", Caller, "main");
            await File.WriteAllTextAsync(Path.Combine(Caller, "marker"), "caller");
            await GitAsync(Caller, "add", "marker");
            await CommitAsync(Caller, "Add caller marker");
            Directory.CreateDirectory(Nested);
        }

        private Task<string> CommitAsync(string directory, string message) =>
            GitAsync(directory, "-c", "user.name=Worker Base Tests", "-c", "user.email=worker-base@example.invalid", "commit", "-m", message);

        public async Task<string> GitAsync(string directory, params string[] arguments)
        {
            var result = await Processes.RunAsync("git", arguments, directory, timeout: TimeSpan.FromMinutes(1));
            Assert.True(result.Ok, result.Message);
            return result.StdOut.Trim();
        }

        public void Dispose()
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }
}
