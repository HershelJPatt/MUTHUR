using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// The include that carries every core procedure into the Claude kit had no test of any kind, so a rule
/// added to `kit/core/` could silently fail to reach the harness most of this organization runs on. These
/// install this repository's own kit into throwaway repositories and read back what a session would be given.
/// </summary>
[Collection(KitEnvironment.Name)]
public sealed class KitInstallTests : IDisposable
{
    private static string RepositoryRoot =>
        Path.GetDirectoryName(
            ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}."))!;

    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly List<string> repositories = [];

    // xUnit runs the tests of one class sequentially, and KitEnvironment keeps the other class that points
    // MUTHUR_KIT somewhere from running alongside this one, so holding it for the life of the class is safe.
    public KitInstallTests() =>
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, Path.Combine(RepositoryRoot, "kit"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, previousKit);
        foreach (var repo in repositories) Directory.Delete(repo, recursive: true);
    }

    private string NewRepository()
    {
        var repo = Directory.CreateTempSubdirectory("muthur-kit-").FullName;
        repositories.Add(repo);
        return repo;
    }

    private static async Task<int> Invoke(params string[] args)
    {
        var root = new RootCommand("test");
        Globals.AddTo(root);
        KitCommands.AddTo(root);

        var parse = root.Parse(args);
        Assert.Empty(parse.Errors);
        return await parse.InvokeAsync();
    }

    /// <summary>
    /// The rule lives in the procedure every session loads; the verification paths it points at live in the
    /// reference installed beside it, which a session reads when it reaches step 3. Both halves have to arrive
    /// on every harness, and the procedure has to say where the other half is.
    /// </summary>
    [Theory]
    [InlineData("claude", ".claude/skills/muthur-orchestrate/SKILL.md", ".claude/skills/muthur-orchestrate/reference.md")]
    [InlineData("codex", ".muthur/procedures/orchestrate.md", ".muthur/procedures/orchestrate-reference.md")]
    [InlineData("generic", ".muthur/procedures/orchestrate.md", ".muthur/procedures/orchestrate-reference.md")]
    public async Task Every_harness_installs_the_orchestrate_procedure_with_the_headless_rule(string harness, string procedure, string reference)
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", harness, "--repo", repo));

        var installed = Path.Combine(repo, procedure);
        Assert.True(File.Exists(installed), $"The {harness} kit installed no {procedure}.");
        var installedReference = Path.Combine(repo, reference);
        Assert.True(File.Exists(installedReference), $"The {harness} kit installed no {reference}.");

        var procedureText = File.ReadAllText(installed);
        foreach (var phrase in new[] { "before implementation submission", "is a defect in the spec",
            "Verification a conductor-started session can run", "muthur msg inbox --wait 900", "muthur task notes T-n --file" })
            Assert.Contains(phrase, procedureText, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint your task evidence", procedureText, StringComparison.Ordinal);

        var referenceText = File.ReadAllText(installedReference);
        foreach (var phrase in new[] { "HTTP-only assertions", "Connector-driven UI", "Installed headless interaction",
            "needs: headless-browser", "needs: browser", "before implementation submission",
            "shared two-session ceiling", "Protected agent definitions", "Decision routing", "Delegation lessons" })
            Assert.Contains(phrase, referenceText, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint your task evidence", referenceText, StringComparison.Ordinal);

        var template = File.ReadAllText(Path.Combine(repo, "specs", "_TEMPLATE.md"));
        Assert.Contains("needs: headless-browser", template, StringComparison.Ordinal);
        Assert.Contains("needs: browser", template, StringComparison.Ordinal);
        Assert.Contains("equivalent bounded runner", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// `kit install` writes `kit/core/spec-template.md` over `specs/_TEMPLATE.md`, and this repository is
    /// itself a MUTHUR project, so its checked-in template is one of those installed copies. Let the two
    /// drift and every future install here produces a diff nobody asked for.
    /// </summary>
    [Fact]
    public void This_repositorys_spec_template_is_still_the_kit_source_byte_for_byte()
    {
        var source = Path.Combine(RepositoryRoot, "kit", "core", "spec-template.md");
        var installed = Path.Combine(RepositoryRoot, "specs", "_TEMPLATE.md");

        Assert.True(
            File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(installed)),
            $"{installed} has drifted from its source {source}, which kit install writes over it.");
    }

    /// <summary>
    /// Thirteen units in one day arrived on the wrong branch. The kit is the only thing that stops that
    /// hurting, and a rule that reaches only some harnesses does not.
    /// </summary>
    [Theory]
    [InlineData("claude", ".claude/skills/muthur-orchestrate/SKILL.md")]
    [InlineData("codex", ".muthur/procedures/orchestrate.md")]
    [InlineData("generic", ".muthur/procedures/orchestrate.md")]
    public async Task Every_harness_tells_an_orchestrator_where_a_spawned_worktree_comes_from(string harness, string procedure)
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", harness, "--repo", repo));

        var installed = Path.Combine(repo, procedure);
        Assert.True(File.Exists(installed), $"The {harness} kit installed no {procedure}.");

        var procedureText = File.ReadAllText(installed);
        Assert.True(
            procedureText.Contains("cut from the repository's HEAD, not from yours", StringComparison.Ordinal),
            $"{procedure} has lost the paragraph saying where a spawned worktree is cut from.");
        Assert.True(
            procedureText.Contains("carry the branch name and the commit sha", StringComparison.Ordinal),
            $"{procedure} no longer requires a delegation prompt to carry the spec's branch and sha.");
        Assert.True(
            procedureText.Contains("names the base branch, the commit sha of the spec on it", StringComparison.Ordinal),
            $"{procedure} has lost the ## Rules bullet that restates it.");
    }

    /// <summary>
    /// The base check kept thirteen misplaced units harmless in one day; every installed implementer
    /// contract, including the specialist's, must explain why the kit cannot lose it.
    /// </summary>
    [Theory]
    [InlineData("claude", ".claude/agents/muthur-implementer.md")]
    [InlineData("claude", ".claude/agents/muthur-specialist.md")]
    [InlineData("codex", ".muthur/procedures/implementer.md")]
    [InlineData("generic", ".muthur/procedures/implementer.md")]
    public async Task Every_harness_says_why_the_implementers_base_check_exists(string harness, string procedure)
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", harness, "--repo", repo));

        var installed = Path.Combine(repo, procedure);
        Assert.True(File.Exists(installed), $"The {harness} kit installed no {procedure}.");

        Assert.True(
            File.ReadAllText(installed).Contains("not yours to tidy away", StringComparison.Ordinal),
            $"{procedure} no longer says why its base check exists; a future reader will tidy it away.");
    }

    /// <summary>
    /// The Claude adapter must not undo the rule that kept thirteen units on the right lineage: its old
    /// claim made an orchestrator trust a base the harness never used.
    /// </summary>
    [Fact]
    public async Task The_claude_kit_does_not_tell_an_orchestrator_the_worktree_comes_from_its_own_HEAD()
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", "claude", "--repo", repo));

        var procedure = ".claude/skills/muthur-orchestrate/SKILL.md";
        var procedureText = File.ReadAllText(Path.Combine(repo, procedure));
        Assert.False(
            procedureText.Contains("created from your current HEAD", StringComparison.Ordinal),
            $"{procedure} claims the worktree is created from your current HEAD; that is false and makes an orchestrator trust a base the harness never used.");
        Assert.True(
            procedureText.Contains("not the branch you are standing on", StringComparison.Ordinal),
            $"{procedure} no longer says the worktree is not cut from the branch the orchestrator is standing on.");
    }

    [Theory]
    [InlineData("claude", ".claude/agents/muthur-implementer.md")]
    [InlineData("claude", ".claude/agents/muthur-specialist.md")]
    [InlineData("codex", ".muthur/procedures/implementer.md")]
    [InlineData("generic", ".muthur/procedures/implementer.md")]
    public async Task Installed_implementers_verify_dispatch_before_reconciling_and_reading_the_spec(string harness, string procedure)
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", harness, "--repo", repo));

        var text = File.ReadAllText(Path.Combine(repo, procedure));
        Assert.Contains("named local base branch, its full commit SHA at dispatch, and the named default branch", text, StringComparison.Ordinal);
        Assert.Contains("Missing input means `STATUS: blocked`", text, StringComparison.Ordinal);
        Assert.Contains("Before any ancestry test, reset or spec read", text, StringComparison.Ordinal);
        Assert.Contains("git show-ref --verify \"refs/heads/<base>\"", text, StringComparison.Ordinal);
        Assert.Contains("git rev-parse --verify \"refs/heads/<base>^{commit}\"", text, StringComparison.Ordinal);
        Assert.Contains("git rev-parse --verify \"HEAD^{commit}\"", text, StringComparison.Ordinal);
        Assert.Contains("Require resolved base SHA to equal the full dispatch SHA", text, StringComparison.Ordinal);
        Assert.Contains("Missing refs, failed resolution or a moved", text, StringComparison.Ordinal);
        Assert.Contains("Make no product edits, reset, merge or rebase, and never fall back to another checkout", text, StringComparison.Ordinal);
        Assert.Contains("must redispatch with an updated frozen assignment if the base moved", text, StringComparison.Ordinal);
        Assert.Contains("Require `git status --porcelain` to succeed and be empty before reconciliation", text, StringComparison.Ordinal);
        Assert.Contains("git cat-file -t \"refs/heads/<base>:<spec path>\"` to succeed and return `blob`", text, StringComparison.Ordinal);
        Assert.Contains("committed spec, another object type or any failed Git command blocks", text, StringComparison.Ordinal);
        Assert.Contains("initial HEAD matches the base", text, StringComparison.Ordinal);
        Assert.Contains("exit 0 means ancestor, 1 means not ancestor, and any other exit code", text, StringComparison.Ordinal);
        Assert.Contains("Never treat a Git error as permission to reset", text, StringComparison.Ordinal);
        Assert.Contains("git rev-parse --verify \"refs/heads/<default branch>^{commit}\"`; both must succeed or you are blocked", text, StringComparison.Ordinal);
        Assert.Contains("After either allowed reset, require reset command success, resulting HEAD equal to the resolved base SHA", text, StringComparison.Ordinal);
        Assert.Contains("base still equal to dispatch SHA, and `git status --porcelain` successful and empty; otherwise blocked", text, StringComparison.Ordinal);
        Assert.Contains("it retains the ownership and changed-unit checks above and must still verify the base has not moved", text, StringComparison.Ordinal);
        Assert.Contains("After reconciliation and immediately before reading the spec, re-resolve the named base", text, StringComparison.Ordinal);
        Assert.Contains("and require it still matches dispatch SHA", text, StringComparison.Ordinal);
        Assert.Contains("git show \"refs/heads/<base>:<spec path>\"` must succeed", text, StringComparison.Ordinal);
        Assert.Contains("Never recover the spec alone", text, StringComparison.Ordinal);
        Assert.Contains("BASE: <named base branch, dispatch SHA, initial HEAD, resulting HEAD, and any recovery>", text, StringComparison.Ordinal);
        Assert.Contains("expected branch/SHA and observed HEAD/base (or missing), including missing refs/spec or a moved base", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("muthur-implementer")]
    [InlineData("muthur-specialist")]
    public async Task Claude_agent_descriptions_request_the_full_dispatch_inputs(string agent)
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", "claude", "--repo", repo));

        var description = File.ReadLines(Path.Combine(repo, ".claude", "agents", $"{agent}.md"))
            .Single(line => line.StartsWith("description:", StringComparison.Ordinal));
        Assert.Contains("named local base branch, its full commit SHA at dispatch, the named default branch", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claude_orchestration_supplies_dispatch_inputs_and_an_explicit_base_fallback()
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", "claude", "--repo", repo));

        var text = File.ReadAllText(Path.Combine(repo, ".claude/skills/muthur-orchestrate/SKILL.md"));
        Assert.Contains("MUTHUR does not select the native Agent worktree start point", text, StringComparison.Ordinal);
        Assert.Contains("muthur worker run --tier implementer --spec specs/T-n.md --unit \"<unit>\" --task T-n --base task/T-n-<slug>", text, StringComparison.Ordinal);
        Assert.Contains("--note \"Base branch task/T-n-<slug>; dispatch SHA <full-commit-sha>; default branch main.\"", text, StringComparison.Ordinal);
        Assert.Contains("current WorkerPrompt does not automatically supply them", text, StringComparison.Ordinal);
        Assert.Contains("It does not repair arbitrary dirty or divergent existing worktrees", text, StringComparison.Ordinal);
        Assert.Contains("named local base branch, full commit SHA at dispatch, named default branch", text, StringComparison.Ordinal);
        Assert.Contains("A moved base requires `STATUS: blocked` and an updated frozen redispatch", text, StringComparison.Ordinal);
    }

    /// <summary>The claude kit is the only one that reaches a core procedure through a `{{core:…}}` token.</summary>
    [Fact]
    public async Task No_installed_file_is_left_holding_an_unexpanded_include()
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", "claude", "--repo", repo));

        foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
            Assert.False(
                File.ReadAllText(file).Contains("{{core:", StringComparison.Ordinal),
                $"{Path.GetRelativePath(repo, file)} still holds an unexpanded {{{{core:...}}}} include.");
    }

    /// <summary>
    /// `kit list` had no test of any kind until the enumeration behind it moved into `KitDirectory`, where the
    /// hub reads it too. What the CLI prints is the contract a founder reads before `kit install`.
    /// </summary>
    [Fact]
    public async Task Kit_list_names_every_harness_this_repository_ships_a_kit_for()
    {
        var printed = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(printed);
        try
        {
            Assert.Equal(ExitCodes.Ok, await Invoke("kit", "list"));
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal("[\"claude\",\"codex\",\"generic\",\"sim\"]", printed.ToString().Trim());
    }
}
