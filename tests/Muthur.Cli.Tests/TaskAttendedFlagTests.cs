using System.CommandLine;
using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur task attended` refuses a missing or contradictory flag here, before the hub is called. It has to: on
/// the wire a null reason is the clear, so a forgotten `--reason` would silently lift the flag instead of being
/// refused — and an agent branching on exit 2 would never hear about it. The guard reads whether --reason was
/// supplied, not whether it has content, so the five rows below are the whole truth table.
/// </summary>
public sealed class TaskAttendedFlagTests
{
    private const string SayWhy = "Say why this task needs a human: --reason \"<why>\", or --clear to lift it.";

    [Fact]
    public void With_neither_flag_it_says_what_to_pass()
    {
        var refusal = TaskCommands.AttendedRefusal(reasonSupplied: false, reason: null, clear: false);

        Assert.NotNull(refusal);
        Assert.Equal("reason_required", refusal.Value.Code);
        Assert.Equal(SayWhy, refusal.Value.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reason_is_not_a_reason(string reason)
    {
        var refusal = TaskCommands.AttendedRefusal(reasonSupplied: true, reason, clear: false);

        Assert.NotNull(refusal);
        Assert.Equal("reason_required", refusal.Value.Code);
        Assert.Equal(SayWhy, refusal.Value.Message);
    }

    [Fact]
    public void With_both_it_refuses_rather_than_guessing_which_was_meant()
    {
        var refusal = TaskCommands.AttendedRefusal(reasonSupplied: true, "a browser", clear: true);

        Assert.NotNull(refusal);
        Assert.Equal("reason_required", refusal.Value.Code);
        Assert.Equal("Pass --reason or --clear, not both.", refusal.Value.Message);
    }

    /// <summary>
    /// The one that shipped: `--reason "   " --clear` read as "no reason, so the clear was meant", and the hub
    /// lifted the flag. A blank reason is still a supplied reason, so this is the contradiction, not a clear.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reason_alongside_clear_is_still_a_contradiction(string reason)
    {
        var refusal = TaskCommands.AttendedRefusal(reasonSupplied: true, reason, clear: true);

        Assert.NotNull(refusal);
        Assert.Equal("reason_required", refusal.Value.Code);
        Assert.Equal("Pass --reason or --clear, not both.", refusal.Value.Message);
    }

    [Fact]
    public void A_reason_with_content_alone_is_sent() =>
        Assert.Null(TaskCommands.AttendedRefusal(reasonSupplied: true, "a browser", clear: false));

    [Fact]
    public void Clear_alone_is_sent() =>
        Assert.Null(TaskCommands.AttendedRefusal(reasonSupplied: false, reason: null, clear: true));
}

/// <summary>
/// The wiring, not just the helper. Supplied-ness has to come from the parse — a whitespace `--reason` is
/// supplied, an absent one is not — because the moment the guard infers it from the value instead, `--reason
/// "   " --clear` stops being a contradiction and becomes a silent clear. Parsing only: the refusal never
/// reaches the hub, so nothing here needs one.
/// </summary>
public sealed class TaskAttendedWiringTests
{
    private static (bool Supplied, string? Value, bool Clear) Parse(params string[] args)
    {
        var root = new RootCommand("test");
        TaskCommands.AddTo(root);
        var attended = root.Subcommands.Single(c => c.Name == "task").Subcommands.Single(c => c.Name == "attended");
        var reason = (Option<string?>)attended.Options.Single(o => o.Name == "--reason");
        var clear = (Option<bool>)attended.Options.Single(o => o.Name == "--clear");

        var parse = root.Parse(args);
        return (parse.GetResult(reason) is not null, parse.GetValue(reason), parse.GetValue(clear));
    }

    [Fact]
    public void A_whitespace_reason_with_clear_is_refused_by_the_real_command()
    {
        var (supplied, value, clear) = Parse("task", "attended", "T-1", "--reason", "   ", "--clear");

        Assert.True(supplied);
        Assert.True(clear);
        var refusal = TaskCommands.AttendedRefusal(supplied, value, clear);
        Assert.NotNull(refusal);
        Assert.Equal("Pass --reason or --clear, not both.", refusal.Value.Message);
    }

    [Fact]
    public void An_absent_reason_with_clear_is_not_supplied_and_is_sent()
    {
        var (supplied, value, clear) = Parse("task", "attended", "T-1", "--clear");

        Assert.False(supplied);
        Assert.True(clear);
        Assert.Null(TaskCommands.AttendedRefusal(supplied, value, clear));
    }

    [Fact]
    public void Neither_flag_is_refused_by_the_real_command()
    {
        var (supplied, value, clear) = Parse("task", "attended", "T-1");

        Assert.False(supplied);
        Assert.False(clear);
        var refusal = TaskCommands.AttendedRefusal(supplied, value, clear);
        Assert.NotNull(refusal);
        Assert.Equal("Say why this task needs a human: --reason \"<why>\", or --clear to lift it.", refusal.Value.Message);
    }
}
