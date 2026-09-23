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

    [Theory]
    [InlineData("git grep -q -e '^T-2$' HEAD -- T-2.txt", new[] { "T-2.txt" })]
    [InlineData("git show HEAD:docs/notes.md", new[] { "docs/notes.md" })]
    [InlineData(@"pwsh ./scripts/check.ps1 -Path .\src\App.cs", new[] { "scripts/check.ps1", "src/App.cs" })]
    [InlineData("./scripts/smoke.sh fixtures/input.json | Select-String 'ok'", new[] { "fixtures/input.json" })]
    [InlineData("dotnet build", new string[0])]
    [InlineData("muthur.exe --version", new string[0])]
    [InlineData("dotnet test tests/Muthur.Server.Tests --filter FullyQualifiedName~Receipts", new string[0])]
    [InlineData("dotnet test --filter Muthur.Server.Tests.Receipts -o out/results.trx", new string[0])]
    [InlineData("dotnet test > test.log", new string[0])]
    [InlineData("git worktree add ../scratch -b task/T-1-work", new string[0])]
    [InlineData("muthur log --limit 20   # conductor.staffing for T-1", new string[0])]
    [InlineData("$stderr = Join-Path $scratch 'stderr.txt'", new string[0])]
    [InlineData("git grep -q 'two words.txt' HEAD -- /etc/passwd ../outside.txt C:/abs/file.txt https://example.com/a.txt", new string[0])]
    [InlineData("1..3 | ForEach-Object { git grep -q x HEAD -- T-1.txt; if ($LASTEXITCODE -ne 0) { throw 'failed' } }", new[] { "T-1.txt" })]
    public void Command_paths_are_the_relative_file_arguments_not_commands_flags_or_outputs(string command, string[] expected)
    {
        Assert.Equal(expected, SpecVerification.CommandPaths(command));
    }

    private const string WithUnits = """
        # T-2 — Write the marker

        ## Context

        - **Files:** `README.md` is context, not a unit's output.

        ## Units of work

        ### Unit A — marker
        - **Files:** create `T-2.txt`; modify
          `src/Muthur.Server/Services/TaskService.cs`
        - **Does:** writes the marker.

        ### Unit B — tests
        - **Files:**
          - `tests/Muthur.Server.Tests/`
          - `ValidatorSessionLauncher.cs`
        - **Acceptance:** `notes/unlisted.md` exists.

        ## Verification

        ```
        git grep -q -e '^T-2$' HEAD -- T-2.txt
        git grep -q x HEAD -- T-2-notes.md
        ```
        """;

    [Fact]
    public void Produced_files_are_what_the_units_list_under_files()
    {
        var parsed = SpecVerification.Parse(WithUnits);

        Assert.Equal(["T-2.txt", "src/Muthur.Server/Services/TaskService.cs", "tests/Muthur.Server.Tests", "ValidatorSessionLauncher.cs"], parsed.Produced);
        Assert.Equal([new VerificationPath("git grep -q -e '^T-2$' HEAD -- T-2.txt", "T-2.txt"), new VerificationPath("git grep -q x HEAD -- T-2-notes.md", "T-2-notes.md")], parsed.Paths);
        Assert.True(parsed.IsProduced("T-2.txt"));
        Assert.True(parsed.IsProduced("tests/Muthur.Server.Tests/SpecGuardTests.cs"));
        Assert.True(parsed.IsProduced("src/Muthur.Server/Services/ValidatorSessionLauncher.cs"));
        Assert.False(parsed.IsProduced("T-2-notes.md"));
        Assert.False(parsed.IsProduced("README.md"));
        Assert.False(parsed.IsProduced("notes/unlisted.md"));
    }

    [Fact]
    public void Without_unit_headings_a_files_line_anywhere_counts()
    {
        var parsed = SpecVerification.Parse("# T-1\n\nFiles: T-1.txt, docs/T-1.md.\n\n## Verification\n\n```\ndotnet build\n```\n");

        Assert.Equal(["T-1.txt", "docs/T-1.md"], parsed.Produced);
    }

    [Fact]
    public void A_spec_that_lists_no_files_produces_nothing()
    {
        Assert.Empty(SpecVerification.Parse(CommandsOnly).Produced);
    }
}
