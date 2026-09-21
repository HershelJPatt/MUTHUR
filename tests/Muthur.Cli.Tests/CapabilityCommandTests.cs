using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Launch;

namespace Muthur.Cli.Tests;

public sealed class CapabilityCommandTests
{
    [Theory]
    [InlineData("conductor-validator", 90)]
    [InlineData("native-subagent", 90)]
    [InlineData("worker-run", 121)]
    [InlineData("worker-run", 0)]
    public async Task Unsupported_paths_and_invalid_budgets_refuse_before_process_or_admission(string path, int seconds)
    {
        var root = new RootCommand();
        Globals.AddTo(root);
        CapabilityCommands.AddTo(root, new NeverRunner());
        var parse = root.Parse(["capability", "probe", "--task", "T-1", "--spec", "specs/T-1.md", "--base", "task/T-1",
            "--default-branch", "main", "--harness", "fixture", "--launch-path", path, "--timeout-seconds", seconds.ToString()]);
        Assert.Empty(parse.Errors);
        Assert.Equal(2, await parse.InvokeAsync());
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    public void Supported_probe_requires_task_and_accepts_bounded_budget(int seconds)
    {
        var root = new RootCommand();
        CapabilityCommands.AddTo(root, new NeverRunner());
        string[] arguments = ["capability", "probe", "--spec", "specs/T-1.md", "--base", "task/T-1",
            "--default-branch", "main", "--harness", "fixture", "--launch-path", "worker-run", "--timeout-seconds", seconds.ToString()];
        Assert.Single(root.Parse(arguments).Errors);
        Assert.Empty(root.Parse([.. arguments, "--task", "T-1"]).Errors);
    }

    [Fact]
    public void Inspect_requires_all_frozen_identity_inputs()
    {
        var root = new RootCommand();
        CapabilityCommands.AddTo(root, new NeverRunner());
        Assert.Equal(5, root.Parse("capability inspect").Errors.Count);
    }

    private sealed class NeverRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null) => throw new Xunit.Sdk.XunitException("A refused probe must not launch any process.");
    }
}
