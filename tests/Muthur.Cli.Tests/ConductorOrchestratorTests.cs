using System.CommandLine;
using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur conductor orchestrators on|off` decides nothing of its own: the hub owns the switch and records the
/// decision. What the CLI owns is the shape of the command tree — a founder who types the words in the task's
/// verification section must reach the endpoint, and an `on` that parsed as anything else would be a switch the
/// founder believes they threw.
/// </summary>
public sealed class ConductorOrchestratorTests
{
    private static Command Orchestrators()
    {
        var root = new RootCommand("test");
        WorkerCommands.AddTo(root);
        return root.Subcommands.Single(c => c.Name == "conductor").Subcommands.Single(c => c.Name == "orchestrators");
    }

    [Fact]
    public void The_switch_hangs_off_the_conductor_group_the_founder_already_knows()
    {
        var orchestrators = Orchestrators();

        Assert.Contains(orchestrators.Subcommands, c => c.Name == "on");
        Assert.Contains(orchestrators.Subcommands, c => c.Name == "off");
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public void Both_words_parse_without_an_error_a_founder_would_have_to_read(string word)
    {
        var root = new RootCommand("test");
        WorkerCommands.AddTo(root);

        var parsed = root.Parse(["conductor", "orchestrators", word]);

        Assert.Empty(parsed.Errors);
        Assert.Equal(word, parsed.CommandResult.Command.Name);
    }
}
