using System.CommandLine;
using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

public sealed class VerificationCommandTests
{
    [Fact]
    public void Run_requires_all_six_local_inputs()
    {
        var root = new RootCommand();
        VerificationCommands.AddTo(root);
        Assert.Equal(6, root.Parse(["verify", "run"]).Errors.Count);
        var error = VerificationCommands.ParseError(root.Parse(["verify", "run"]));
        Assert.NotNull(error);
        Assert.Equal(2, error.ExitCode);
        Assert.Equal("invalid", error.Status);
        Assert.NotEqual(Guid.Empty, error.RunId);
        Assert.Empty(root.Parse(["verify", "run", "--repo", "repo", "--ref", "HEAD", "--spec", "specs/T-107.md",
            "--recipe", "muthur", "--output", "out", "--cache", "cache"]).Errors);
    }

    [Fact]
    public void Cleanup_requires_output_and_no_hub_options()
    {
        var root = new RootCommand();
        VerificationCommands.AddTo(root);
        Assert.Single(root.Parse(["verify", "cleanup"]).Errors);
        Assert.Empty(root.Parse(["verify", "cleanup", "--output", "out"]).Errors);
    }
}
