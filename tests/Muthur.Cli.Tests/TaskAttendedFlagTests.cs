using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur task attended` refuses a missing or contradictory flag here, before the hub is called. It has to: on
/// the wire a null reason is the clear, so a forgotten `--reason` would silently lift the flag instead of being
/// refused — and an agent branching on exit 2 would never hear about it.
/// </summary>
public sealed class TaskAttendedFlagTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_reason_and_no_clear_it_says_what_to_pass(string? reason)
    {
        var refusal = TaskCommands.AttendedRefusal(reason, clear: false);

        Assert.NotNull(refusal);
        Assert.Equal("reason_required", refusal.Value.Code);
        Assert.Equal("Say why this task needs a human: --reason \"<why>\", or --clear to lift it.", refusal.Value.Message);
    }

    [Fact]
    public void With_both_it_refuses_rather_than_guessing_which_was_meant()
    {
        var refusal = TaskCommands.AttendedRefusal("a browser", clear: true);

        Assert.NotNull(refusal);
        Assert.Equal("reason_required", refusal.Value.Code);
        Assert.Equal("Pass --reason or --clear, not both.", refusal.Value.Message);
    }

    [Theory]
    [InlineData("a browser", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void One_of_them_alone_is_sent(string? reason, bool clear) =>
        Assert.Null(TaskCommands.AttendedRefusal(reason, clear));
}
