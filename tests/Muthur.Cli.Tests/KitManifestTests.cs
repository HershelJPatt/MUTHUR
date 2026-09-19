using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

// MUTHUR_KIT is process-wide and two classes here now point it at different kits, so the assembly runs its
// collections one at a time. Without this, a manifest test could swap the kit out from under KitInstallTests
// mid-install, or read the repository's kit where it expects its own scratch one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Muthur.Cli.Tests;

/// <summary>
/// A malformed `kit.json` used to reach the write loop and throw there: the AOT build has no stack trace
/// support, so the founder got an address dump, and whatever the loop had already written stayed written.
/// These drive `kit install` over deliberately broken manifests and read back the error it prints.
/// </summary>
public sealed class KitManifestTests : IDisposable
{
    private const string Harness = "broken";

    private static string RepositoryRoot =>
        Path.GetDirectoryName(
            ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}."))!;

    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly string scratch = Directory.CreateTempSubdirectory("muthur-manifest-").FullName;
    private readonly List<string> repositories = [];

    private string Kit => Path.Combine(scratch, "kit");
    private string HarnessDir => Path.Combine(Kit, Harness);
    private string ManifestPath => Path.Combine(HarnessDir, "kit.json");

    /// <summary>A directory beside the repository, so a manifest that escapes has somewhere visible to land.</summary>
    private string Outside => Path.Combine(scratch, "outside");

    private string Repository { get; }

    public KitManifestTests()
    {
        Directory.CreateDirectory(Path.Combine(Kit, "core"));
        Directory.CreateDirectory(HarnessDir);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Kit, "core", "dummy.md"), "x");
        File.WriteAllText(Path.Combine(HarnessDir, "source.md"), "a procedure");
        File.WriteAllText(Path.Combine(HarnessDir, "includes.md"), "before {{core:nope.md}} after");
        File.WriteAllText(Path.Combine(scratch, "secret.txt"), "not the kit's to give");

        Repository = Path.Combine(scratch, "repo");
        Directory.CreateDirectory(Repository);

        Environment.SetEnvironmentVariable(KitCommands.KitVariable, Kit);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, previousKit);
        foreach (var repo in repositories) Directory.Delete(repo, recursive: true);
        Directory.Delete(scratch, recursive: true);
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

    /// <summary>Installs <paramref name="manifest"/> and reads back the exit code and the error body on stderr.</summary>
    private async Task<(int Exit, string Code, string Message)> Rejected(string manifest)
    {
        File.WriteAllText(ManifestPath, manifest);

        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await Invoke("kit", "install", "--harness", Harness, "--repo", Repository);
        }
        finally
        {
            Console.SetError(previous);
        }

        using var body = JsonDocument.Parse(stderr.ToString());
        return (exit, body.RootElement.GetProperty("code").GetString()!, body.RootElement.GetProperty("message").GetString()!);
    }

    private async Task Rejects(string manifest, string expected)
    {
        var (exit, code, message) = await Rejected(manifest);

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.Equal(ManifestPath + expected, message);
        Assert.Empty(Directory.GetFileSystemEntries(Repository));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    [Theory]
    // Rule 3: the root is not an object.
    [InlineData("""[]""", " must contain a JSON object.")]
    // Rule 4: no "files" key.
    [InlineData("""{"harness":"broken"}""", " has no \"files\" array.")]
    // Rule 5: "files" is not an array.
    [InlineData("""{"files":42}""", ": \"files\" must be an array, not number.")]
    // Rule 6: an entry is not an object.
    [InlineData("""{"files":["source.md"]}""", ": entry 0 must be an object, not string.")]
    // Rule 7: no "from".
    [InlineData("""{"files":[{"to":"docs/x.md"}]}""", ": entry 0 has no \"from\".")]
    // Rule 8: "from" is not a string.
    [InlineData("""{"files":[{"from":42,"to":"docs/x.md"}]}""", ": entry 0 has a \"from\" that is number, not a string.")]
    // Rule 9: no "to", and a "to" that is not a string.
    [InlineData("""{"files":[{"from":"source.md"}]}""", ": entry 0 has no \"to\".")]
    [InlineData("""{"files":[{"from":"source.md","to":[]}]}""", ": entry 0 has a \"to\" that is array, not a string.")]
    // Rule 10: "mode" is present and not a string.
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x.md","mode":7}]}""", ": entry 0 (docs/x.md) has a \"mode\" that is number, not a string.")]
    // Rule 11: "validator" is present and not a boolean.
    [InlineData("""{"files":[{"from":"source.md","to":"briefs/x.md","validator":"yes"}]}""", ": entry 0 (briefs/x.md) has a \"validator\" that is string, not a boolean.")]
    // Rule 12: "from" resolves outside the kit directory.
    [InlineData("""{"files":[{"from":"../../secret.txt","to":"docs/x.md"}]}""", ": entry 0 (docs/x.md) reads \"../../secret.txt\", which is outside the kit directory.")]
    // Rule 13: "from" names nothing.
    [InlineData("""{"files":[{"from":"nope.md","to":"docs/x.md"}]}""", ": entry 0 (docs/x.md) reads \"nope.md\", which does not exist.")]
    // Rule 14: "to" climbs out of the repository.
    [InlineData("""{"files":[{"from":"source.md","to":"../outside/escaped.md"}]}""", ": entry 0 writes \"../outside/escaped.md\", which is outside the repository.")]
    // Rule 15: a {{core:...}} token naming a file that is not in kit/core.
    [InlineData("""{"files":[{"from":"includes.md","to":"docs/x.md"}]}""", ": entry 0 (docs/x.md) includes \"{{core:nope.md}}\", which does not exist.")]
    public Task A_manifest_the_founder_can_fix_is_named_field_and_all(string manifest, string expected) =>
        Rejects(manifest, expected);

    /// <summary>Rule 2. The reader's own message carries the line and position, so only its opening is fixed.</summary>
    [Fact]
    public async Task Json_that_stops_mid_array_is_reported_as_json()
    {
        var (exit, code, message) = await Rejected("""{"files":[{"from":"source.md","to":"docs/x.md"}""");

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.StartsWith($"{ManifestPath} is not valid JSON: ", message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Repository));
    }

    /// <summary>
    /// Rule 1. `kit install` checks the manifest exists before it reads it, so the only way to reach this
    /// through the command is to make the read itself fail; the validator is asked directly instead.
    /// </summary>
    [Fact]
    public void A_manifest_that_cannot_be_read_says_so_rather_than_throwing()
    {
        // A directory is the portable unreadable file: ReadAllText on one is UnauthorizedAccessException.
        var unreadable = Path.Combine(scratch, "kit.json");
        Directory.CreateDirectory(unreadable);

        Assert.False(KitCommands.TryReadManifest(unreadable, HarnessDir, Kit, Repository, out _, out var problem));
        Assert.StartsWith($"{unreadable} could not be read: ", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rule 14's other half. Path.Combine hands back an absolute "to" unchanged, so before this a manifest
    /// could name any path on the disk and `--repo` was advisory.
    /// </summary>
    [Fact]
    public async Task An_absolute_destination_is_outside_the_repository_however_it_is_spelled()
    {
        var absolute = Path.Combine(Outside, "absolute.md");
        var manifest = $$"""{"files":[{"from":"source.md","to":"{{absolute.Replace("\\", "\\\\")}}"}]}""";

        await Rejects(manifest, $": entry 0 writes \"{absolute}\", which is outside the repository.");
        Assert.False(File.Exists(absolute));
    }

    /// <summary>
    /// The defect behind the whole task: entries were written as the loop walked them, so a manifest whose
    /// ninth entry was wrong had already written eight. Validation is a separate pass for exactly this.
    /// </summary>
    [Fact]
    public async Task A_bad_second_entry_leaves_the_repository_completely_empty()
    {
        var manifest = """
            {"files":[
              {"from":"source.md","to":"docs/first.md"},
              {"from":"source.md","to":"docs/second.md","mode":7}
            ]}
            """;

        var (exit, code, _) = await Rejected(manifest);

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.Empty(Directory.GetFileSystemEntries(Repository));
        Assert.False(File.Exists(Path.Combine(Repository, "docs", "first.md")));
        Assert.False(File.Exists(Path.Combine(Repository, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(Repository, ProjectContext.FileName)));
    }

    /// <summary>
    /// The three shipped manifests must go on installing exactly as they did. Every `to` they name is read
    /// out of the manifest itself, so an entry added to a kit is covered without touching this test.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("generic")]
    public async Task A_shipped_kit_still_installs_every_file_it_names(string harness)
    {
        var kit = Path.Combine(RepositoryRoot, "kit");
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, kit);
        var repo = NewRepository();

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", harness, "--repo", repo));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(kit, harness, "kit.json")));
        foreach (var to in manifest.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("to").GetString()!))
            Assert.True(File.Exists(Path.Combine(repo, to)), $"The {harness} kit no longer installs {to}.");

        Assert.True(File.Exists(Path.Combine(repo, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(repo, ProjectContext.FileName)));
    }

    /// <summary>A manifest with nothing wrong with it still writes what it asks for.</summary>
    [Fact]
    public async Task A_valid_scratch_manifest_writes_its_entry()
    {
        File.WriteAllText(ManifestPath, """{"harness":"broken","files":[{"from":"source.md","to":"docs/x.md"}]}""");

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", Harness, "--repo", Repository));
        Assert.Equal("a procedure", File.ReadAllText(Path.Combine(Repository, "docs", "x.md")));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }
}
