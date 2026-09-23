using System.CommandLine;
using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur receipts` is a pass-through: the hub owns the window's default and its bounds, so the only thing
/// the CLI can get wrong is the query string it builds. Validating --hours here would be the bug — a founder
/// who types 100000 would get an argument error instead of the thirty days the hub would have clamped them to.
/// </summary>
public sealed class ReceiptsCommandTests
{
    private static Command Receipts()
    {
        var root = new RootCommand("test");
        SystemCommands.AddTo(root);
        return root.Subcommands.Single(c => c.Name == "receipts");
    }

    [Fact]
    public void The_command_is_on_the_root_with_a_description_help_can_print()
    {
        var receipts = Receipts();

        Assert.False(string.IsNullOrWhiteSpace(receipts.Description));
        // --hours and --task and nothing else: the endpoint needs no token, so there is no option here implying otherwise.
        Assert.Equal(["--hours", "--task"], receipts.Options.Select(o => o.Name));
    }

    [Fact]
    public void An_absent_window_sends_no_parameter_and_leaves_the_default_to_the_hub() =>
        Assert.Equal("", SystemCommands.ReceiptsQuery(null));

    [Theory]
    [InlineData(1, "?hours=1")]
    [InlineData(24, "?hours=24")]
    [InlineData(720, "?hours=720")]
    public void A_window_becomes_the_query_string(int hours, string expected) =>
        Assert.Equal(expected, SystemCommands.ReceiptsQuery(hours));

    [Theory]
    [InlineData(0, "?hours=0")]
    [InlineData(-5, "?hours=-5")]
    [InlineData(100000, "?hours=100000")]
    public void A_window_outside_the_bounds_is_sent_as_typed_for_the_hub_to_clamp(int hours, string expected) =>
        Assert.Equal(expected, SystemCommands.ReceiptsQuery(hours));

    /// <summary>
    /// One task, sent as typed: the hub owns what a task id looks like and answers 422 for one it cannot read,
    /// which is the same division of labour as the window. Escaped, because the hand that types it is the
    /// hand that pastes "T-12 " with a space or "T&12" with a typo.
    /// </summary>
    [Theory]
    [InlineData(null, "T-12", "?task=T-12")]
    [InlineData(6, "T-12", "?hours=6&task=T-12")]
    [InlineData(6, "t-12", "?hours=6&task=t-12")]
    [InlineData(null, "T&12", "?task=T%2612")]
    [InlineData(null, "", "")]
    public void A_task_is_the_second_parameter_and_escaped(int? hours, string task, string expected) =>
        Assert.Equal(expected, SystemCommands.ReceiptsQuery(hours, task));

    [Theory]
    [InlineData("?hours=6", "receipts", "--hours", "6")]
    [InlineData("?task=T-7", "receipts", "--task", "T-7")]
    [InlineData("?hours=48&task=T-7", "receipts", "--task", "T-7", "--hours", "48")]
    [InlineData("", "receipts")]
    public void The_parsed_options_are_the_ones_the_query_string_is_built_from(string expected, params string[] args)
    {
        var root = new RootCommand("test");
        SystemCommands.AddTo(root);
        var receipts = root.Subcommands.Single(c => c.Name == "receipts");
        var hours = (Option<int?>)receipts.Options.Single(o => o.Name == "--hours");
        var task = (Option<string?>)receipts.Options.Single(o => o.Name == "--task");
        var parsed = root.Parse(args);

        Assert.Equal(expected, SystemCommands.ReceiptsQuery(parsed.GetValue(hours), parsed.GetValue(task)));
    }
}
