using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Launch;

namespace Muthur.Cli.Tests;

public sealed class CapabilityCommandTests
{
    [Theory]
    [InlineData("worker-run", 90)]
    [InlineData("worker-run", 120)]
    [InlineData("conductor-validator", 90)]
    [InlineData("native-subagent", 90)]
    [InlineData("worker-run", 121)]
    [InlineData("worker-run", 0)]
    public async Task Production_probe_never_calls_any_process(string path, int seconds)
    {
        var root = new RootCommand();
        Globals.AddTo(root);
        CapabilityCommands.AddTo(root, new NeverRunner());
        var parse = root.Parse(["capability", "probe", "--spec", "specs/T-1.md", "--base", "task/T-1",
            "--default-branch", "main", "--harness", "fixture", "--launch-path", path, "--timeout-seconds", seconds.ToString()]);
        Assert.Empty(parse.Errors);
        Assert.Equal(2, await parse.InvokeAsync());
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
            IReadOnlyDictionary<string, string>? environment = null) => throw new InvalidOperationException("A production probe must not launch any process.");
    }
}
