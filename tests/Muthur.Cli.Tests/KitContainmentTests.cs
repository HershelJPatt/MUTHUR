using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// Rule 22. Rule 14 decided containment by how a path is spelled, and a junction is a path that does not stay
/// where it resolves: `repo/link/escaped.md` is textually under the repository, and the filesystem sent the
/// write to the junction's target. These build the attack out of real reparse points, because a faked one
/// would only test the story we told ourselves about the platform. The rows that must keep working — a link
/// pointing back inside, a `--repo` named through one — are here too: a rule that refused them would be worse
/// than the hole it closes.
/// </summary>
[Collection(KitEnvironment.Name)]
public sealed class KitContainmentTests : IDisposable
{
    private const string Harness = "linked";

    private static string RepositoryRoot =>
        Path.GetDirectoryName(
            ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}."))!;

    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly string scratch = Directory.CreateTempSubdirectory("muthur-containment-").FullName;
    private readonly List<string> repositories = [];
    private readonly List<string> links = [];

    /// <summary>The repository as the constructor left it, so "nothing was written" can be said exactly.</summary>
    private readonly string[] fixture;

    /// <summary>Where <c>repo\dangling</c> pointed while its target still existed.</summary>
    private readonly string danglingTarget;

    private string Kit => Path.Combine(scratch, "kit");
    private string HarnessDir => Path.Combine(Kit, Harness);
    private string ManifestPath => Path.Combine(HarnessDir, "kit.json");

    /// <summary>A directory beside the repository, so a manifest that escapes has somewhere visible to land.</summary>
    private string Outside => Path.Combine(scratch, "outside");

    /// <summary>The same, one directory over, for a <c>from</c> that leaves the kit rather than the repository.</summary>
    private string KitOutside => Path.Combine(scratch, "kitoutside");

    private string Repository { get; }

    /// <summary>The repository named through a junction, which must go on installing exactly as the repository does.</summary>
    private string Via => Path.Combine(scratch, "via");

    private string Docs => Path.Combine(Repository, "docs");

    public KitContainmentTests()
    {
        Repository = Path.Combine(scratch, "repo");
        try
        {
            Directory.CreateDirectory(Path.Combine(Kit, "core"));
            Directory.CreateDirectory(HarnessDir);
            Directory.CreateDirectory(Outside);
            Directory.CreateDirectory(KitOutside);
            File.WriteAllText(Path.Combine(Kit, "core", "dummy.md"), "x");
            File.WriteAllText(Path.Combine(HarnessDir, "source.md"), "a procedure");
            File.WriteAllText(Path.Combine(HarnessDir, "second.md"), "the second one");
            File.WriteAllText(Path.Combine(KitOutside, "stolen.md"), "not the kit's to give");

            Directory.CreateDirectory(Repository);
            Directory.CreateDirectory(Docs);

            // Out of the repository, and the reported defect.
            CreateLink(Path.Combine(Repository, "link"), Outside);
            // Back into it, which is legitimate and must go on installing.
            CreateLink(Path.Combine(Repository, "inward"), Docs);
            // A chain: the OS collapses it, so one hop or two must read the same.
            CreateLink(Path.Combine(Repository, "hop"), Outside);
            CreateLink(Path.Combine(Repository, "chain"), Path.Combine(Repository, "hop"));
            // Out of the kit directory, which is rule 12's half of the same hole.
            CreateLink(Path.Combine(HarnessDir, "out"), KitOutside);
            // The whole repository named through a junction.
            CreateLink(Via, Repository);

            // A junction whose target is then deleted: Directory.Exists stays true and the path still resolves
            // outside, so it is refused rather than crashed on. Its target is under Outside, so an escape through
            // it would show up in the same place every other escape does.
            var gone = Path.Combine(Outside, "gone");
            Directory.CreateDirectory(gone);
            var dangling = Path.Combine(Repository, "dangling");
            CreateLink(dangling, gone);
            danglingTarget = Directory.ResolveLinkTarget(dangling, returnFinalTarget: true)!.FullName;
            Directory.Delete(gone);

            fixture = Directory.GetFileSystemEntries(Repository).Order().ToArray();
            Environment.SetEnvironmentVariable(KitCommands.KitVariable, Kit);
        }
        catch
        {
            RemoveLinks();
            Directory.Delete(scratch, recursive: true);
            throw;
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, previousKit);

        // Outside is the only place a manifest in this fixture can reach, so whatever is in it at the end of a
        // test escaped containment — collected before the cleanup, asserted after it, so a failure here still
        // leaves a clean machine behind.
        var escaped = Directory.GetFileSystemEntries(Outside);

        // Remove aliases before targets; recursive deletion of a junction chain can throw on Windows.
        RemoveLinks();
        var outsideSurvived = Directory.Exists(Outside);
        var source = Path.Combine(KitOutside, "stolen.md");
        var sourceAfterUnlink = File.Exists(source) ? File.ReadAllText(source) : null;

        foreach (var repo in repositories) Directory.Delete(repo, recursive: true);
        Directory.Delete(scratch, recursive: true);

        Assert.Empty(escaped);
        Assert.True(outsideSurvived, "Unlinking the fixture removed the outside target.");
        Assert.Equal("not the kit's to give", sourceAfterUnlink);
        Assert.False(Directory.Exists(scratch), "The fixture left its scratch root behind.");
        foreach (var repo in repositories)
            Assert.False(Directory.Exists(repo), "The fixture left a shipped-kit repository outside its scratch root.");
    }

    /// <summary>
    /// A directory link from <paramref name="link"/> to <paramref name="target"/>. Measured on this machine:
    /// <c>Directory.CreateSymbolicLink</c> throws "A required privilege is not held by the client" without
    /// developer mode or elevation, and <c>mklink /J</c> succeeds — and a junction is the reparse point a
    /// founder actually has without asking anyone. If neither works this test cannot build the attack it
    /// claims to measure, so it throws: xunit 2.9.3 has no dynamic skip, and a containment test that quietly
    /// passes when it could not create its own link is worse than one that fails.
    /// </summary>
    private void CreateLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            links.Add(link);
            return;
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            links.Add(link);
            return;
        }
        catch (IOException)
        {
            // No SeCreateSymbolicLinkPrivilege. A junction needs none, and reparses identically.
        }

        var info = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!Directory.Exists(link) || Directory.ResolveLinkTarget(link, returnFinalTarget: true) is null)
            throw new InvalidOperationException(
                $"Neither a symbolic link nor a junction could be created at \"{link}\" -> \"{target}\": "
                + $"mklink exited {process.ExitCode}: {stderr}{stdout}. This test measures containment against a "
                + "link, and cannot measure it against a link that does not exist.");
        links.Add(link);
    }

    private void RemoveLinks()
    {
        for (var index = links.Count - 1; index >= 0; index--)
            Directory.Delete(links[index], recursive: false);
        links.Clear();
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

    private async Task<int> Install(string manifest, string? repo = null)
    {
        File.WriteAllText(ManifestPath, manifest);
        return await Invoke("kit", "install", "--harness", Harness, "--repo", repo ?? Repository);
    }

    /// <summary>Installs <paramref name="manifest"/> and reads back the exit code and the error body on stderr.</summary>
    private async Task<(int Exit, string Code, string Message)> Rejected(string manifest, string? repo = null)
    {
        File.WriteAllText(ManifestPath, manifest);

        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await Invoke("kit", "install", "--harness", Harness, "--repo", repo ?? Repository);
        }
        finally
        {
            Console.SetError(previous);
        }

        using var body = JsonDocument.Parse(stderr.ToString());
        return (exit, body.RootElement.GetProperty("code").GetString()!, body.RootElement.GetProperty("message").GetString()!);
    }

    private async Task Rejects(string manifest, string expected, string? repo = null)
    {
        var (exit, code, message) = await Rejected(manifest, repo);

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.Equal(ManifestPath + expected, message);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// The repository exactly as the constructor built it. Not <c>Assert.Empty</c>, because this fixture's
    /// repository is not empty — it holds the links the attack needs — so "nothing was written" has to be said
    /// against what was there before, and against the one directory a link points back into.
    /// </summary>
    private void AssertNothingWasWritten()
    {
        Assert.Equal(fixture, Directory.GetFileSystemEntries(Repository).Order());
        Assert.Empty(Directory.GetFileSystemEntries(Docs));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>Where a link leads, asked of the platform directly, so the expected message is not this rule's own answer.</summary>
    private static string RealTargetOf(string link) =>
        Directory.ResolveLinkTarget(link, returnFinalTarget: true)!.FullName;

    private static string Manifest(string to, string from = "source.md") =>
        $$"""{"files":[{"from":"{{from}}","to":"{{to}}"}]}""";

    /// <summary>
    /// The defect. `Path.GetFullPath` normalises a path as a string, so "link/escaped.md" is textually under
    /// the repository, rule 14 said yes, and the write landed beside it. Refused by where the path leads, and
    /// the message names the resolved path: the manifest line looks innocent and the reason is a link in the
    /// founder's own repository, so "outside the repository" alone would send them hunting for a ".." that is
    /// not there.
    /// </summary>
    [Fact]
    public Task A_destination_through_a_junction_out_of_the_repository_is_refused()
    {
        var real = Path.Combine(RealTargetOf(Path.Combine(Repository, "link")), "escaped.md");

        return Rejects(
            Manifest("link/escaped.md"),
            $": entry 0 writes \"link/escaped.md\", which resolves through a link to \"{real}\", outside the repository.");
    }

    /// <summary>
    /// One component deeper, which is the reason every component is resolved rather than the leaf alone: a path
    /// *under* a link is not itself reported as a link, so asking only about "link/sub/escaped.md" gets null.
    /// </summary>
    [Fact]
    public Task A_destination_deeper_under_that_junction_is_refused_too()
    {
        var real = Path.Combine(RealTargetOf(Path.Combine(Repository, "link")), "sub", "escaped.md");

        return Rejects(
            Manifest("link/sub/escaped.md"),
            $": entry 0 writes \"link/sub/escaped.md\", which resolves through a link to \"{real}\", outside the repository.");
    }

    /// <summary>
    /// A chain of two junctions. `returnFinalTarget` collapses it in the platform, which is why there is no
    /// loop and no depth cap here — and why one hop and two must produce the same answer.
    /// </summary>
    [Fact]
    public Task A_destination_through_a_chain_of_junctions_is_refused()
    {
        var real = Path.Combine(RealTargetOf(Path.Combine(Repository, "chain")), "escaped.md");

        return Rejects(
            Manifest("chain/escaped.md"),
            $": entry 0 writes \"chain/escaped.md\", which resolves through a link to \"{real}\", outside the repository.");
    }

    /// <summary>
    /// The behaviour that must not change. A junction pointing back *inside* the repository is legitimate —
    /// the founder's own, and none of MUTHUR's business — so it installs, through the link, to where the link
    /// leads. A rule that refused this would be worse than the hole it closes.
    /// </summary>
    [Fact]
    public async Task A_destination_through_a_junction_back_into_the_repository_still_installs()
    {
        Assert.Equal(ExitCodes.Ok, await Install(Manifest("inward/ok.md")));

        Assert.Equal("a procedure", File.ReadAllText(Path.Combine(Docs, "ok.md")));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>An ordinary destination, with every junction above present and none of them used.</summary>
    [Fact]
    public async Task An_ordinary_destination_installs_with_junctions_sitting_in_the_repository()
    {
        Assert.Equal(ExitCodes.Ok, await Install(Manifest("docs/ok.md")));

        Assert.Equal("a procedure", File.ReadAllText(Path.Combine(Docs, "ok.md")));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>
    /// Rule 12's half of the same hole: a "from" textually under the kit that reads through a link out of it.
    /// The manifest would otherwise pick its own source and install a file MUTHUR never shipped.
    /// </summary>
    [Theory]
    [InlineData("stolen.md")]
    [InlineData("missing.md")]
    public Task A_source_through_a_junction_out_of_the_kit_is_refused(string source)
    {
        var real = Path.Combine(RealTargetOf(Path.Combine(HarnessDir, "out")), source);

        return Rejects(
            Manifest("docs/x.md", from: $"out/{source}"),
            $": entry 0 (docs/x.md) reads \"out/{source}\", which resolves through a link to \"{real}\", outside the kit directory.");
    }

    /// <summary>
    /// The root has to be resolved too, and this is the test that says so: `--repo` named through a junction
    /// resolves to its real path, so comparing a resolved destination against an unresolved root would refuse
    /// every legitimate destination in the repository — every install through such a path, in other words.
    /// </summary>
    [Fact]
    public async Task A_repository_named_through_a_junction_still_installs()
    {
        Assert.Equal(ExitCodes.Ok, await Install(Manifest("docs/ok.md"), repo: Via));

        Assert.Equal("a procedure", File.ReadAllText(Path.Combine(Docs, "ok.md")));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>Unwritable must bound its ancestor walk by the same resolved root as its destinations.</summary>
    [Fact]
    public async Task A_repository_named_through_a_junction_still_refuses_a_file_in_the_parent_path()
    {
        var blocker = Path.Combine(Docs, "blocker");
        File.WriteAllText(blocker, "already here");

        var (exit, code, message) = await Rejected(Manifest("docs/blocker/x.md"), repo: Via);

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.Equal($"{ManifestPath}: entry 0 writes \"docs/blocker/x.md\", but \"docs/blocker\" is a file in the repository, so its parent directory cannot be created.", message);
        Assert.Equal(fixture, Directory.GetFileSystemEntries(Repository).Order());
        Assert.Equal(blocker, Assert.Single(Directory.GetFileSystemEntries(Docs)));
        Assert.Equal("already here", File.ReadAllText(blocker));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>
    /// Rule 20, one rule over and the same defect: with "inward" a junction to "docs", these two entries are a
    /// file-versus-directory collision that a comparison of spellings cannot see — and the write phase would
    /// have met it with the first file already on disk.
    /// </summary>
    [Fact]
    public Task Two_destinations_that_collide_only_after_a_link_is_followed_are_refused() =>
        Rejects(
            """{"files":[{"from":"source.md","to":"inward/x.md"},{"from":"source.md","to":"docs/x.md/y.md"}]}""",
            ": entry 0 writes \"inward/x.md\" and entry 1 writes \"docs/x.md/y.md\"; one cannot be both a file and a directory.");

    /// <summary>
    /// The other half of rule 20, and the other thing resolving destinations buys: through the link these two
    /// are the *same* path, so they are last-one-wins rather than two files — which is what the format has
    /// always allowed, and what the repository ends up with either way.
    /// </summary>
    [Fact]
    public async Task Two_destinations_that_are_the_same_path_through_a_link_are_the_last_one_winning()
    {
        var manifest = """
            {"files":[
              {"from":"source.md","to":"inward/x.md"},
              {"from":"second.md","to":"docs/x.md"}
            ]}
            """;

        Assert.Equal(ExitCodes.Ok, await Install(manifest));

        Assert.Equal("the second one", File.ReadAllText(Path.Combine(Docs, "x.md")));
        Assert.Equal(Path.Combine(Docs, "x.md"), Assert.Single(Directory.GetFileSystemEntries(Docs)));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>
    /// A junction whose target has been deleted. `Directory.Exists` on it is still true and it still resolves
    /// to the missing target, so it is refused as the escape it is. The row where a wrong platform claim would
    /// leave a hole rather than an over-refusal, which is why it is measured here rather than reasoned about.
    /// </summary>
    [Fact]
    public Task A_destination_through_a_junction_whose_target_is_gone_is_refused_rather_than_crashing() =>
        Rejects(
            Manifest("dangling/x.md"),
            $": entry 0 writes \"dangling/x.md\", which resolves through a link to \"{Path.Combine(danglingTarget, "x.md")}\", outside the repository.");

    /// <summary>
    /// And the manifests that ship. Every file each kit installs is read out of the kit's own manifest, so the
    /// count is the manifest's rather than a number written down here — a kit that gains a file is covered
    /// without touching this test, and a rule that started refusing one would be caught by the same assertion.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("generic")]
    public async Task A_shipped_kit_installs_exactly_the_files_its_manifest_names(string harness)
    {
        var kit = Path.Combine(RepositoryRoot, "kit");
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, kit);
        var repo = Directory.CreateTempSubdirectory("muthur-containment-kit-").FullName;
        repositories.Add(repo);

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", harness, "--repo", repo));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(kit, harness, "kit.json")));
        var named = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("to").GetString()!.Replace('/', Path.DirectorySeparatorChar))
            .Distinct()
            .Order()
            .ToArray();

        // The installer's own two files are in no manifest, so they are taken out here and asserted separately.
        var installed = Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(repo, file))
            .Where(file => file != ".gitignore" && file != ProjectContext.FileName)
            .Order()
            .ToArray();

        Assert.Equal(named, installed);
        Assert.True(File.Exists(Path.Combine(repo, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(repo, ProjectContext.FileName)));
    }
}
