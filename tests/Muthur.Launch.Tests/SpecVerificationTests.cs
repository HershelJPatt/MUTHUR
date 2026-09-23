namespace Muthur.Launch.Tests;

/// <summary>
/// What a spec says about its own verification. The parser decides whether a script can judge the work, so the
/// fixtures are the shapes specs have actually taken: commands in a fenced block, prose that drives a browser, and
/// nothing at all.
/// </summary>
public sealed class SpecVerificationTests
{
    /// <summary>The historical shape: a Verification section that needs a live browser and never says so.</summary>
    private const string LiveBrowser = """
        # T-64 — Collision pill on the board

        ## Goal

        Show a pill when two tasks touch the same file.

        ## Verification

        Open the dashboard in a browser and click the board tab; the pill renders inside Virtualize.
        Take a screenshot for the evidence.
        """;

    private const string CommandsOnly = """
        # T-70 — Receipts endpoint

        ## Goal

        Serve receipts.

        ## Verification

        ```
        dotnet build
        # the receipts tests
        dotnet test tests/Muthur.Server.Tests --filter Receipts

        ```

        ## Notes

        ```
        echo not a verification command
        ```
        """;

    [Fact]
    public void Commands_are_the_fenced_lines_under_the_verification_heading()
    {
        var parsed = SpecVerification.Parse(CommandsOnly);

        Assert.Equal(["dotnet build", "dotnet test tests/Muthur.Server.Tests --filter Receipts"], parsed.Commands);
        Assert.True(parsed.HasSection);
        Assert.Null(parsed.Validation);
        Assert.Empty(parsed.Needs);
        Assert.False(parsed.RequiresJudgment);
    }

    [Fact]
    public void A_browser_walkthrough_with_no_needs_line_is_judgment_with_no_commands()
    {
        var parsed = SpecVerification.Parse(LiveBrowser);

        Assert.True(parsed.HasSection);
        Assert.Empty(parsed.Commands);
        Assert.Contains(parsed.Prose, line => line.Contains("click", StringComparison.Ordinal));
        Assert.True(parsed.RequiresJudgment);
    }

    [Fact]
    public void No_verification_section_requires_judgment()
    {
        var parsed = SpecVerification.Parse("# T-1 — Build it\n\n## Goal\n\nDo the thing.\n\n```\necho not under verification\n```\n");

        Assert.False(parsed.HasSection);
        Assert.Empty(parsed.Commands);
        Assert.True(parsed.RequiresJudgment);
    }

    [Theory]
    [InlineData("validation: judgment", "judgment")]
    [InlineData("Validation: Checks-Only", "checks-only")]
    [InlineData("- **validation:** `judgment`", "judgment")]
    public void A_standalone_validation_line_is_read_anywhere(string line, string expected)
    {
        var parsed = SpecVerification.Parse($"# T-1 — Build it\n\n{line}\n\n## Verification\n\n```\ndotnet test\n```\n");

        Assert.Equal(expected, parsed.Validation);
        Assert.Equal(expected == "judgment", parsed.RequiresJudgment);
    }

    [Theory]
    [InlineData("validation: vibes")]
    [InlineData("validation: judgment\nvalidation: checks-only")]
    public void An_unknown_or_inconsistent_validation_line_is_refused(string lines)
    {
        var ex = Assert.Throws<WorkerDispatchException>(() => SpecVerification.Parse($"# T-1\n{lines}\n## Verification\n```\necho\n```\n"));

        Assert.Equal("spec_validation_invalid", ex.Code);
    }

    [Fact]
    public void Needs_are_comma_separated_standalone_lines_and_make_the_spec_judgment()
    {
        var parsed = SpecVerification.Parse("# T-1\n\n> needs: headless-browser, device.\n\n## Verification\n\n**needs:** `gpu`\n\n```\ndotnet test\n```\n");

        Assert.Equal(["headless-browser", "device", "gpu"], parsed.Needs);
        Assert.Single(parsed.Commands);
        Assert.True(parsed.RequiresJudgment);
    }

    [Fact]
    public void Prose_about_needs_is_not_a_declaration()
    {
        var parsed = SpecVerification.Parse("# T-1\n\n## Verification\n\nNothing here needs: a browser, honestly.\n\n```\ndotnet test\n```\n");

        Assert.Empty(parsed.Needs);
        Assert.False(parsed.RequiresJudgment);
    }
}
