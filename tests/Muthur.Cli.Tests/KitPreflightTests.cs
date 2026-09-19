using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// Three shapes that used to crash `kit install` with an unhandled exception and — in an AOT build with
/// StackTraceSupport off — an address dump instead of a sentence. All three are decided by the repository
/// rather than by the manifest, which is why T-8 did not reach them: it closed every shape a manifest can
/// be wrong about. Each is refused before anything is written, because a try/catch round the write loop
/// would turn the crash into a sentence and leave the half-installed repository.
/// </summary>
[Collection(KitEnvironment.Name)]
public sealed class KitPreflightTests : IDisposable
{
    private static string RepositoryRoot =>
        Path.GetDirectoryName(
            ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException($"No {ProjectContext.FileName} found from {AppContext.BaseDirectory}."))!;

    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly List<string> repositories = [];

    public KitPreflightTests() =>
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, Path.Combine(RepositoryRoot, "kit"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, previousKit);
        foreach (var repo in repositories)
        {
            foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);   // a read-only file will not delete
            Directory.Delete(repo, recursive: true);
        }
    }

    private string NewRepository()
    {
        var repo = Directory.CreateTempSubdirectory("muthur-preflight-").FullName;
        repositories.Add(repo);
        return repo;
    }

    private static async Task<int> Install(string repo, string harness = "claude")
    {
        var root = new RootCommand("test");
        Globals.AddTo(root);
        KitCommands.AddTo(root);
        var parse = root.Parse(["kit", "install", "--harness", harness, "--repo", repo]);
        Assert.Empty(parse.Errors);
        return await parse.InvokeAsync();
    }

    /// <summary>Nothing may be written by a refused install — that is the half of the damage a catch cannot undo.</summary>
    private static void AssertNothingInstalled(string repo, params string[] allowed)
    {
        var unexpected = Directory.EnumerateFileSystemEntries(repo)
            .Select(Path.GetFileName)
            .Where(name => !allowed.Contains(name))
            .ToList();
        Assert.Empty(unexpected);
    }

    /// <summary>
    /// F2, and the worst of the three: no manifest is involved. `Install` writes these two whatever the kit
    /// says, so a directory at either path fails every install of every shipped kit, after writing real files.
    /// </summary>
    [Theory]
    [InlineData(".gitignore")]
    [InlineData("muthur.project.json")]
    public async Task An_installer_file_that_is_a_directory_is_refused_before_anything_is_written(string name)
    {
        var repo = NewRepository();
        Directory.CreateDirectory(Path.Combine(repo, name));

        Assert.Equal(ExitCodes.RuleViolation, await Install(repo));
        AssertNothingInstalled(repo, name);
    }

    /// <summary>F1: the mirror of "the destination is an existing directory", with the answer the other way round.</summary>
    [Theory]
    [InlineData(".claude")]
    [InlineData(".claude/skills")]
    public async Task An_ancestor_of_a_destination_that_is_a_file_is_refused(string ancestor)
    {
        var repo = NewRepository();
        var path = Path.Combine(repo, ancestor.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a directory\n");

        Assert.Equal(ExitCodes.RuleViolation, await Install(repo));
    }

    /// <summary>
    /// F3: an ordinary condition rather than a manifest fault, included because it crashed rather than
    /// reporting, and because the pass that answers the other two answers it for nothing.
    /// </summary>
    [Fact]
    public async Task A_read_only_destination_is_refused_rather_than_crashing()
    {
        var repo = NewRepository();
        var ignore = Path.Combine(repo, ".gitignore");
        File.WriteAllText(ignore, "# already here\n");
        File.SetAttributes(ignore, FileAttributes.ReadOnly);

        Assert.Equal(ExitCodes.RuleViolation, await Install(repo));
        AssertNothingInstalled(repo, ".gitignore");
    }

    /// <summary>And a repository with nothing in the way still installs, which is what stops this being a cure worse than the disease.</summary>
    [Fact]
    public async Task A_clean_repository_still_installs()
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Install(repo));

        Assert.True(File.Exists(Path.Combine(repo, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(repo, "muthur.project.json")));
    }

    /// <summary>
    /// The validator's repro, and a promise this spec made and then broke: a "create" entry whose destination
    /// already exists is kept untouched, so a read-only one is never written and must not be refused. Every
    /// shipped kit ships briefs/validator.md that way on purpose, so refusing it refused every install into a
    /// repository where a founder had protected their own brief.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("generic")]
    public async Task A_read_only_file_this_install_would_keep_rather_than_write_is_not_refused(string harness)
    {
        var repo = NewRepository();
        var brief = Path.Combine(repo, "briefs", "validator.md");
        Directory.CreateDirectory(Path.GetDirectoryName(brief)!);
        const string Founders = "# validator\nThe founder's own words.\n";
        File.WriteAllText(brief, Founders);
        File.SetAttributes(brief, FileAttributes.ReadOnly);

        Assert.Equal(ExitCodes.Ok, await Install(repo, harness));

        Assert.Equal(Founders, File.ReadAllText(brief));   // kept, byte for byte
    }

    /// <summary>
    /// The other half, so the narrowing did not simply switch F3 off: a read-only destination this install
    /// *would* write is still refused. .gitignore without the worktrees line is one the install must append to.
    /// </summary>
    [Fact]
    public async Task A_read_only_file_this_install_would_write_is_still_refused()
    {
        var repo = NewRepository();
        var ignore = Path.Combine(repo, ".gitignore");
        File.WriteAllText(ignore, "# nothing ignored yet\n");
        File.SetAttributes(ignore, FileAttributes.ReadOnly);

        Assert.Equal(ExitCodes.RuleViolation, await Install(repo));
    }

    /// <summary>And one it would leave alone because it already says what the install would add.</summary>
    [Fact]
    public async Task A_read_only_gitignore_that_already_ignores_the_worktrees_is_not_refused()
    {
        var repo = NewRepository();
        var ignore = Path.Combine(repo, ".gitignore");
        File.WriteAllText(ignore, "bin/\n.worktrees/\n");
        File.SetAttributes(ignore, FileAttributes.ReadOnly);

        Assert.Equal(ExitCodes.Ok, await Install(repo));
    }

    /// <summary>
    /// The validator's round-2 repro, and the same defect one layer in: a re-install writes nothing to a file
    /// whose content already matches, so a founder who protects an already-current kit file must still be able
    /// to re-run the install. Covers overwrite mode (claude, generic) and section mode (codex).
    /// </summary>
    [Theory]
    [InlineData("claude", ".claude/agents/muthur-implementer.md")]
    [InlineData("generic", ".muthur/procedures/orchestrate.md")]
    [InlineData("codex", "AGENTS.md")]
    public async Task A_read_only_file_a_reinstall_would_leave_unchanged_is_not_refused(string harness, string path)
    {
        var repo = NewRepository();
        Assert.Equal(ExitCodes.Ok, await Install(repo, harness));

        var installed = Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar));
        var before = File.ReadAllText(installed);
        File.SetAttributes(installed, FileAttributes.ReadOnly);

        Assert.Equal(ExitCodes.Ok, await Install(repo, harness));

        Assert.Equal(before, File.ReadAllText(installed));   // untouched, because nothing needed writing
    }

    /// <summary>
    /// And the half that keeps the check honest: the same file, read-only, with content the install *would*
    /// change. Refused, because this one really cannot be written.
    /// </summary>
    [Fact]
    public async Task A_read_only_file_a_reinstall_would_change_is_still_refused()
    {
        var repo = NewRepository();
        Assert.Equal(ExitCodes.Ok, await Install(repo));

        var installed = Path.Combine(repo, ".claude", "agents", "muthur-implementer.md");
        File.WriteAllText(installed, "somebody edited this by hand\n");
        File.SetAttributes(installed, FileAttributes.ReadOnly);

        Assert.Equal(ExitCodes.RuleViolation, await Install(repo));
    }
}
