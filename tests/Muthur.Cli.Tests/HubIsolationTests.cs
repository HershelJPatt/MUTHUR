using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// That a real command action can be invoked here at all, and that invoking one reaches nothing. The first
/// test is what proves the module initializer ran; the two below are safe only because it did, which is why
/// it sits above them.
/// </summary>
public sealed class HubIsolationTests
{
    /// <summary>
    /// `Program` is a top-level program, so its root command is built inside a `&lt;Main&gt;$` no C# caller can
    /// name. This assembles the same tree from the same factories `Program` calls rather than restating one.
    /// </summary>
    private static async Task<int> Invoke(params string[] args)
    {
        var root = new RootCommand("test");
        Globals.AddTo(root);
        TaskCommands.AddTo(root);

        var parse = root.Parse(args);
        // A command that never parsed also never reaches a hub, and would satisfy the exit code below for
        // the wrong reason. Exit 4 only means "reached nothing" once the action actually ran.
        Assert.Empty(parse.Errors);
        return await parse.InvokeAsync();
    }

    [Fact]
    public void The_hub_environment_points_at_nothing()
    {
        Assert.Equal(HubIsolation.UnreachableUrl, MuthurEnvironment.Url);
        Assert.True(Directory.Exists(MuthurEnvironment.Home));
        Assert.False(File.Exists(Path.Combine(MuthurEnvironment.Home, MuthurEnvironment.FounderTokenFile)));
    }

    [Fact]
    public async Task An_action_that_reaches_for_the_hub_reaches_nothing() =>
        Assert.Equal(ExitCodes.NotRunning, await Invoke("task", "list"));

    /// <summary>
    /// The exact request the T-31 implementer refused to aim at production, which the fixture makes safe.
    /// </summary>
    [Fact]
    public async Task A_write_action_reaches_nothing_either() =>
        Assert.Equal(ExitCodes.NotRunning, await Invoke("task", "attended", "T-1", "--clear", "--founder"));
}
