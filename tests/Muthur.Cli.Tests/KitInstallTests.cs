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
public sealed class KitInstallTests : IDisposable
{
    private static string RepositoryRoot =>
        Path.GetDirectoryName(
            ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}."))!;

    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly List<string> repositories = [];

    // xUnit runs the tests of one class sequentially and no other class in this assembly reads MUTHUR_KIT,
    // so pointing it at the kit beside this repository's manifest for the life of the class is safe.
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
        Assert.Contains("is a defect in the spec", File.ReadAllText(installed), StringComparison.Ordinal);
        Assert.Contains(
            "no browser, no GUI and no human",
            File.ReadAllText(Path.Combine(repo, "specs", "_TEMPLATE.md")),
            StringComparison.Ordinal);
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
