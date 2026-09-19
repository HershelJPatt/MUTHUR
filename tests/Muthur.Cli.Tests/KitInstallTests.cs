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

    [Theory]
    [InlineData("claude", ".claude/skills/muthur-orchestrate/SKILL.md")]
    [InlineData("codex", ".muthur/procedures/orchestrate.md")]
    [InlineData("generic", ".muthur/procedures/orchestrate.md")]
    public async Task Every_harness_installs_the_orchestrate_procedure_with_the_headless_rule(string harness, string procedure)
    {
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", harness, "--repo", repo));

        var installed = Path.Combine(repo, procedure);
        Assert.True(File.Exists(installed), $"The {harness} kit installed no {procedure}.");

        // The rule is written twice on purpose - the section teaches and the ## Rules bullet enforces - so
        // each half is pinned by a phrase only it uses. The defect-class wording is common to both and is
        // pinned as well: it is the phrasing the founder asked for.
        var procedureText = File.ReadAllText(installed);
        Assert.True(
            procedureText.Contains("no browser, no GUI, no hands", StringComparison.Ordinal),
            $"{procedure} has lost the 'Verification a conductor-started session can run' section.");
        Assert.True(
            procedureText.Contains("no browser, no GUI and no human", StringComparison.Ordinal),
            $"{procedure} has lost the ## Rules bullet that restates the section.");
        Assert.True(
            procedureText.Contains("is a defect in the spec", StringComparison.Ordinal),
            $"{procedure} no longer calls such a spec a defect in the spec.");

        Assert.Contains(
            "no browser, no GUI and no human",
            File.ReadAllText(Path.Combine(repo, "specs", "_TEMPLATE.md")),
            StringComparison.Ordinal);
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
}
