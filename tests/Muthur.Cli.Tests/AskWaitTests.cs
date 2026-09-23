using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur ask --wait` is the ask and the inbox wait in one command, so a session spends one round trip on a
/// question instead of two and never forgets the second half. The hub caps one long-poll at 900 s; a longer
/// wait is several of them.
/// </summary>
public sealed class AskWaitTests
{
    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(60, new[] { 60 })]
    [InlineData(900, new[] { 900 })]
    [InlineData(901, new[] { 900, 1 })]
    [InlineData(2000, new[] { 900, 900, 200 })]
    public void A_wait_is_split_into_inbox_polls_the_hub_accepts(int seconds, int[] expected) =>
        Assert.Equal(expected, MessageCommands.WaitSlices(seconds));

    [Fact]
    public void The_option_parses_beside_the_others()
    {
        var root = new RootCommand();
        Globals.AddTo(root);
        MessageCommands.AddTo(root);
        var parsed = root.Parse(["ask", "CSV or JSON?", "--task", "T-2", "--option", "csv", "--option", "json", "--wait", "900"]);
        Assert.Empty(parsed.Errors);
    }
}
