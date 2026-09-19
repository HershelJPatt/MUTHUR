using System.CommandLine;
using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// `agent list` is a pass-through, so the query string is the only thing the CLI can get wrong — and the worse
/// of the two ways to get it wrong is a --all that parses and is then never read. The founder asks for every
/// session, the hub is asked for the default, and what comes back reads exactly like a quiet organization.
/// </summary>
public sealed class AgentListQueryTests
{
    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "?all=true")]
    public void The_one_filter_is_either_absent_or_the_whole_query(bool all, string expected) =>
        Assert.Equal(expected, AgentCommands.AgentListQuery(all));

    [Theory]
    [InlineData("?all=true", "agent", "list", "--all")]
    [InlineData("", "agent", "list")]
    public void The_parsed_option_is_the_one_the_query_string_is_built_from(string expected, params string[] args)
    {
        var root = new RootCommand("test");
        AgentCommands.AddTo(root);
        var list = root.Subcommands.Single(c => c.Name == "agent").Subcommands.Single(c => c.Name == "list");
        var all = (Option<bool>)Assert.Single(list.Options);
        Assert.Equal("--all", all.Name);

        Assert.Equal(expected, AgentCommands.AgentListQuery(root.Parse(args).GetValue(all)));
    }
}
