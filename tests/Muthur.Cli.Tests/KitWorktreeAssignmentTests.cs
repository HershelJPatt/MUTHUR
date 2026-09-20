using System.CommandLine;
using System.Text.RegularExpressions;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

[Collection(KitEnvironment.Name)]
public sealed class KitWorktreeAssignmentTests : IDisposable
{
    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly string repository = Directory.CreateTempSubdirectory("muthur-worktree-kit-").FullName;

    public KitWorktreeAssignmentTests()
    {
        var root = Path.GetDirectoryName(ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException($"No {ProjectContext.FileName} found from {AppContext.BaseDirectory}."))!;
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, Path.Combine(root, "kit"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, previousKit);
        Directory.Delete(repository, recursive: true);
    }

    private async Task<string> InstallAndRead(string path)
    {
        var root = new RootCommand("test");
        Globals.AddTo(root);
        KitCommands.AddTo(root);
        var parse = root.Parse(["kit", "install", "--harness", "claude", "--repo", repository]);
        Assert.Empty(parse.Errors);
        Assert.Equal(ExitCodes.Ok, await parse.InvokeAsync());
        return File.ReadAllText(Path.Combine(repository, ".claude", path));
    }

    private static string Prose(string text) => Regex.Replace(text, @"\s+", " ");

    private static void AssertNoPreparedTreeMigration(string text)
    {
        var prose = Prose(text);
        Assert.Contains("Neither mode adopts an already-prepared worktree", prose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not call EnterWorktree or write through a prepared sibling path", prose);
        Assert.Contains("Do not copy uncommitted output by hand between trees", prose);
        Assert.Contains("`STATUS: blocked` before writes", prose);
        Assert.Contains("expected/actual root and branch", prose);
        Assert.Contains("redispatch via `muthur worker run`", prose);
    }

    [Fact]
    public async Task Rendered_orchestrate_distinguishes_native_allocation_from_explicit_worker_output()
    {
        var text = await InstallAndRead("skills/muthur-orchestrate/SKILL.md");
        var prose = Prose(text);

        AssertNoPreparedTreeMigration(text);
        Assert.Contains("harness allocates the path and output branch", prose);
        Assert.Contains("not a prepared absolute worktree", prose);
        Assert.Contains("frozen base branch is distinct from", prose);
        Assert.Contains("prefer `muthur worker run` when deterministic output branch placement is required", prose);
        Assert.Contains("output branch must not already exist; do not pre-create its worktree", prose);
        var command = Regex.Match(text, @"muthur worker run --tier implementer[^\r\n`]+").Value;
        foreach (var argument in new[]
        {
            "--spec specs/T-n.md", "--unit \"Unit A\"", "--task T-n", "--base task/T-n-<slug>",
            "--branch task/T-n-unit-a", "--note \"Base branch task/T-n-<slug>; dispatch SHA <full-commit-sha>; default branch main.\""
        })
            Assert.Contains(argument, command);
        Assert.Contains("--tier mastermind", text);
        Assert.Contains("Check reported WORKTREE and BRANCH", prose);
        Assert.Contains("committed branch output", prose);
        Assert.Contains("**into the task branch only**", prose);
        Assert.DoesNotContain("start an implementer in its own git worktree on branch", prose);
    }

    [Theory]
    [InlineData("muthur-implementer")]
    [InlineData("muthur-specialist")]
    public async Task Rendered_agents_keep_isolation_and_report_actual_assignment_without_migration(string agent)
    {
        var text = await InstallAndRead($"agents/{agent}.md");
        var prose = Prose(text);
        var frontMatter = text.Split("---", StringSplitOptions.None)[1];

        Assert.Matches(@"(?m)^isolation: worktree\r?$", frontMatter);
        AssertNoPreparedTreeMigration(text);
        Assert.Contains("git rev-parse --show-toplevel", text);
        Assert.Contains("git branch --show-current", text);
        Assert.Contains("Stay in that worktree and branch", prose);
        Assert.Contains("With native isolation, the actual isolated root and branch are your assignment", prose);
        Assert.Contains("launcher-provided root or output branch, require the actual values to match", prose);
        Assert.Contains("WORKTREE: <absolute actual root>", text);
        Assert.Contains("BRANCH: <branch>", text);
        Assert.Contains("base/SHA/clean-tree and history recovery guards apply to both modes", prose);
        Assert.DoesNotContain("must be the tree you were given", prose);
        Assert.DoesNotContain("intended to be based on the orchestrator's task branch", prose);
    }
}
