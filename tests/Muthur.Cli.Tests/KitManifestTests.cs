using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// The classes that point MUTHUR_KIT somewhere. The variable is process-global, so a test that swapped the
/// kit out mid-install for another class would be a race nobody could reproduce; xUnit runs one collection
/// at a time, which is the smallest thing that stops it.
/// </summary>
[CollectionDefinition(KitEnvironment.Name, DisableParallelization = true)]
public sealed class KitEnvironment
{
    public const string Name = "kit-environment";
}

/// <summary>
/// A malformed `kit.json` used to reach the write loop and throw there: the AOT build has no stack trace
/// support, so the founder got an address dump, and whatever the loop had already written stayed written.
/// These drive `kit install` over deliberately broken manifests and read back the error it prints.
/// </summary>
[Collection(KitEnvironment.Name)]
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
        return await Rejected();
    }

    private async Task<(int Exit, string Code, string Message)> Rejected()
    {
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
    // Rule 9b: an empty "to" is its own mistake, not a containment failure.
    [InlineData("""{"files":[{"from":"source.md","to":""}]}""", ": entry 0 has an empty \"to\".")]
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
    /// Rule 16. System.Text.Json hands back a string with a NUL in it quite happily; Path.GetFullPath refuses
    /// it, and rules 12 and 14 called that unguarded, so validation crashed before it could refuse anything.
    /// The rule is the class — "the platform will not take this as a path" — not the two strings that found it.
    /// </summary>
    [Theory]
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x\u0000.md"}]}""", "to")]
    [InlineData("""{"files":[{"from":"go\u0000od.md","to":"docs/x.md"}]}""", "from")]
    public async Task A_string_the_platform_will_not_take_as_a_path_is_refused_rather_than_thrown(string manifest, string field)
    {
        var (exit, code, message) = await Rejected(manifest);

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.StartsWith($"{ManifestPath}: entry 0 has a \"{field}\" that is not a usable path: ", message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Repository));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>
    /// Rule 17. A 300-character component resolves without complaint and then kills Directory.CreateDirectory,
    /// so this was the one shape that passed validation entirely and died in the write phase.
    /// </summary>
    [Theory]
    [InlineData("to", "writes")]
    [InlineData("from", "reads")]
    public Task A_path_component_no_filesystem_accepts_is_refused_with_its_length(string field, string verb)
    {
        var component = new string('a', 300);
        var spelled = $"{component}/x.md";
        var manifest = field == "to"
            ? $$"""{"files":[{"from":"source.md","to":"{{spelled}}"}]}"""
            : $$"""{"files":[{"from":"{{spelled}}","to":"docs/x.md"}]}""";

        return Rejects(
            manifest,
            $": entry 0 {verb} \"{spelled}\", whose path component \"{new string('a', 40)}…\" is 300 characters; the limit is 255.");
    }

    /// <summary>
    /// Rule 18, found by sweeping rather than by the three shapes that were reported: Path.GetFullPath takes a
    /// control character quite happily and Win32 does not, so `"to": "docs/x\u0001.md"` passed validation whole
    /// and threw in File.WriteAllText — having already created `docs/`. Which characters a name may not hold is
    /// asked of the platform, so a platform that allows all of them has nothing here to fail.
    /// </summary>
    [Fact]
    public async Task Every_character_the_platform_forbids_in_a_file_name_is_refused_before_the_write()
    {
        // The separators on that list are legitimate between components, and a NUL never reaches this rule:
        // Path.GetFullPath refuses it one step earlier, as rule 16.
        var forbidden = Path.GetInvalidFileNameChars()
            .Where(ch => ch is not '\0' && ch != Path.DirectorySeparatorChar && ch != Path.AltDirectorySeparatorChar);

        foreach (var ch in forbidden)
        {
            var escape = $"\\u{(int)ch:x4}";
            await Rejects(
                $$"""{"files":[{"from":"source.md","to":"docs/x{{escape}}.md"}]}""",
                $": entry 0 writes \"docs/x{ch}.md\", which contains {escape}, a character this platform does not allow in a file name.");
        }
    }

    /// <summary>
    /// Rule 19. A "to" ending in a separator satisfies rules 1-18 whole — non-empty, unrooted, inside the
    /// repository, no component too long, nothing forbidden in any component — and then WriteFile creates the
    /// directory and asks File.WriteAllText to write to a path with no file on the end of it. The separators
    /// are asked of the platform: a backslash is a separator on Windows and a legal file name character
    /// elsewhere, and a rule that said otherwise would refuse a manifest that works.
    /// </summary>
    [Fact]
    public async Task A_destination_that_names_a_directory_is_refused_before_anything_is_created()
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }.Distinct();

        foreach (var separator in separators)
            foreach (var directory in new[] { "docs", "a/b", "briefs" })
            {
                var to = directory + separator;
                await Rejects(
                    $$"""{"files":[{"from":"source.md","to":"{{to.Replace("\\", "\\\\")}}"}]}""",
                    $": entry 0 writes \"{to}\", which names a directory, not a file.");
            }
    }

    /// <summary>
    /// Rule 19's other spellings of the same thing. "docs/." and "docs/x.md/.." name a directory exactly as
    /// "docs/" does, and the rule as first written — ends in a separator — was narrower than its own name:
    /// both resolved cleanly, passed every other rule, and died in the write phase with the directory created.
    /// </summary>
    [Theory]
    // A "to" of "." or "briefs/.." resolves to the repository root itself, which rule 14 refuses one step
    // earlier and with a better sentence; these are the spellings that stay inside it.
    [InlineData("docs/.")]
    [InlineData("docs/x.md/..")]
    [InlineData("docs/x.md/.")]
    [InlineData("a/b/c/..")]
    public Task A_last_component_of_dot_or_dotdot_names_a_directory_too(string to) =>
        Rejects(
            $$"""{"files":[{"from":"source.md","to":"{{to}}"}]}""",
            $": entry 0 writes \"{to}\", which names a directory, not a file.");

    /// <summary>
    /// Rule 20's seed. `Install` writes .gitignore and muthur.project.json whether or not the manifest asks
    /// for them, so an entry writing *underneath* one turns it into a directory — a collision with a file no
    /// entry names, which comparing entries against each other could never see.
    /// </summary>
    [Theory]
    [InlineData(".gitignore/x.md", ".gitignore")]
    [InlineData(".gitignore/deeper/x.md", ".gitignore")]
    [InlineData("muthur.project.json/x.md", "muthur.project.json")]
    public Task A_destination_under_a_file_the_installer_writes_itself_is_refused(string to, string own) =>
        Rejects(
            $$"""{"files":[{"from":"source.md","to":"{{to}}"}]}""",
            $": entry 0 writes \"{to}\", which collides with \"{own}\", a file kit install writes itself.");

    /// <summary>
    /// The other half of the seed: writing one of those files *itself* is not a collision. The installer
    /// leaves muthur.project.json alone when it already exists and appends to .gitignore, so both are
    /// last-one-wins like any other repeated destination.
    /// </summary>
    [Fact]
    public async Task A_destination_that_is_one_of_the_installers_own_files_is_not_a_collision()
    {
        File.WriteAllText(ManifestPath, """{"files":[{"from":"source.md","to":".gitignore"}]}""");

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", Harness, "--repo", Repository));
        Assert.Contains(".worktrees/", File.ReadAllText(Path.Combine(Repository, ".gitignore")));
    }

    /// <summary>
    /// Rule 20, and the first rule that reads the manifest as a whole. Each entry below is valid on its own;
    /// the fault is the pair, so the write loop used to meet it with the earlier entry already on disk.
    /// </summary>
    [Theory]
    // The reported shape: a file, then the directory holding it.
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x.md"},{"from":"source.md","to":"docs"}]}""",
        ": entry 0 writes \"docs/x.md\" and entry 1 writes \"docs\"; one cannot be both a file and a directory.")]
    // The same pair the other way round, which died in a different exception for the same reason.
    [InlineData("""{"files":[{"from":"source.md","to":"docs"},{"from":"source.md","to":"docs/x.md"}]}""",
        ": entry 0 writes \"docs\" and entry 1 writes \"docs/x.md\"; one cannot be both a file and a directory.")]
    // A file, then a file underneath it: the ancestor need not be an entry's whole directory.
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x.md"},{"from":"source.md","to":"docs/x.md/y.md"}]}""",
        ": entry 0 writes \"docs/x.md\" and entry 1 writes \"docs/x.md/y.md\"; one cannot be both a file and a directory.")]
    // The colliding pair is not always the first two, and the message names the two that collide.
    [InlineData("""{"files":[{"from":"source.md","to":"a.md"},{"from":"source.md","to":"docs/x.md"},{"from":"source.md","to":"docs"}]}""",
        ": entry 1 writes \"docs/x.md\" and entry 2 writes \"docs\"; one cannot be both a file and a directory.")]
    // Spelling is not the test: the comparison is of resolved paths, not of what the manifest wrote.
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x.md"},{"from":"source.md","to":"./docs"}]}""",
        ": entry 0 writes \"docs/x.md\" and entry 1 writes \"./docs\"; one cannot be both a file and a directory.")]
    public Task Two_entries_that_need_one_path_to_be_both_a_file_and_a_directory_are_refused(string manifest, string expected) =>
        Rejects(manifest, expected);

    /// <summary>
    /// The other half of rule 20: two entries naming the same path are not a collision. That is last-one-wins,
    /// which the manifest format has always allowed, and refusing it would be a new rule nobody asked for.
    /// </summary>
    [Fact]
    public async Task Two_entries_writing_the_same_path_are_the_last_one_winning_not_a_collision()
    {
        File.WriteAllText(Path.Combine(HarnessDir, "second.md"), "the second one");
        File.WriteAllText(ManifestPath, """
            {"files":[
              {"from":"source.md","to":"docs/x.md"},
              {"from":"second.md","to":"docs/x.md"}
            ]}
            """);

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", Harness, "--repo", Repository));
        Assert.Equal("the second one", File.ReadAllText(Path.Combine(Repository, "docs", "x.md")));
    }

    /// <summary>
    /// A "to" naming a directory that is already there. Decided by the repository rather than by the manifest,
    /// which is the question rule 13 already asks one directory over — it calls File.Exists on the source — and
    /// reachable by re-running an install after a "to" changed from a file to a directory name.
    /// </summary>
    [Theory]
    [InlineData("docs")]
    [InlineData("docs/x.md")]
    public async Task A_destination_that_is_already_a_directory_is_refused_before_the_write(string to)
    {
        Directory.CreateDirectory(Path.Combine(Repository, "docs", "x.md"));

        var (exit, code, message) = await Rejected($$"""{"files":[{"from":"source.md","to":"{{to}}"}]}""");

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.Equal($"{ManifestPath}: entry 0 writes \"{to}\", which is an existing directory in the repository.", message);
        // The directory the test made and nothing else: no .gitignore, no muthur.project.json.
        Assert.Equal(Path.Combine(Repository, "docs"), Assert.Single(Directory.GetFileSystemEntries(Repository)));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>
    /// The null device as a *directory* component, one level above rule 18: it holds no forbidden character,
    /// resolves cleanly, and then Windows follows it into the device namespace rather than creating a
    /// directory. The whole device namespace was refused first; measuring Directory.CreateDirectory on each
    /// name showed only NUL fails, so only NUL is named here. Trailing dots and spaces come off first, because
    /// Win32 takes them off before it looks the name up.
    /// </summary>
    [Fact]
    public async Task The_null_device_used_as_a_directory_is_refused()
    {
        // The name means nothing to a filesystem that does not reserve it, where "NUL/x.md" is an ordinary
        // path and refusing it would be this validator inventing a rule the platform does not have.
        if (!OperatingSystem.IsWindows()) return;

        foreach (var name in new[] { "NUL", "nul", "Nul", "NUL ", "NUL." })
            await Rejects(
                $$"""{"files":[{"from":"source.md","to":"{{name}}/x.md"}]}""",
                $": entry 0 writes \"{name}/x.md\", whose component \"{name}\" is a reserved device name on this platform.");
    }

    /// <summary>
    /// The over-refusal that measurement retired. Every reserved name but NUL creates an ordinary directory
    /// here, so a manifest naming one installs — refusing the whole device namespace refused manifests that
    /// work, and contradicted the same question already settled the other way for file names below.
    /// </summary>
    [Theory]
    [InlineData("CON/x.md")]
    [InlineData("PRN/x.md")]
    [InlineData("AUX/x.md")]
    [InlineData("COM1/x.md")]
    [InlineData("LPT1/x.md")]
    // COM0 is not in the device namespace at all, and never was.
    [InlineData("COM0/x.md")]
    public Task A_reserved_name_other_than_the_null_device_is_a_directory_like_any_other(string to) =>
        Installs(to);

    /// <summary>
    /// A reserved name as a *file* name installs as an ordinary file inside the repository, which is why the
    /// device check asks only about the components before the last one.
    /// </summary>
    [Theory]
    [InlineData("docs/CON")]
    [InlineData("COM1")]
    [InlineData("docs/NUL.md")]
    public Task A_reserved_name_as_a_file_name_still_installs(string to) => Installs(to);

    /// <summary>Installs a one-entry manifest writing <paramref name="to"/> and reads the file back.</summary>
    private async Task Installs(string to)
    {
        File.WriteAllText(ManifestPath, $$"""{"files":[{"from":"source.md","to":"{{to}}"}]}""");

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", Harness, "--repo", Repository));
        Assert.Equal("a procedure", File.ReadAllText(Path.Combine(Repository, to)));
    }

    /// <summary>
    /// The same class as rules 17, 18 and 19 — a component that resolves but cannot be created — said by what
    /// Win32 takes off a name. It trims trailing spaces and dots, so a component made only of those trims to
    /// nothing and the directory beneath it never exists. A "to" of "   " was refused before this as "an
    /// existing directory in the repository", which was true only because Win32 had trimmed it to the
    /// repository root; it is now refused for what it is.
    /// </summary>
    [Theory]
    [InlineData(".../x.md", "...")]
    [InlineData("   /x.md", "   ")]
    [InlineData(" /x.md", " ")]
    [InlineData(".. ./x.md", ".. .")]
    [InlineData("....../x.md", "......")]
    [InlineData("docs/...", "...")]
    [InlineData("docs/   ", "   ")]
    [InlineData("   ", "   ")]
    public async Task A_component_of_only_spaces_and_dots_is_refused_before_the_write(string to, string component)
    {
        if (!OperatingSystem.IsWindows()) return;

        await Rejects(
            $$"""{"files":[{"from":"source.md","to":"{{to}}"}]}""",
            $": entry 0 writes \"{to}\", whose component \"{component}\" is only spaces and dots, "
                + "which this platform trims to nothing.");
    }

    /// <summary>
    /// The other side of that rule, and the reason it excludes three spellings: Path.GetFullPath resolves an
    /// empty segment and the two navigation segments away rather than trimming them to nothing, so all three
    /// install. A rule that refused them would refuse manifests that work.
    /// </summary>
    [Theory]
    [InlineData("docs//x.md")]
    [InlineData("./docs/x.md")]
    [InlineData("docs/./x.md")]
    [InlineData("a/../docs/x.md")]
    public Task A_segment_the_platform_resolves_away_is_not_a_component_trimmed_to_nothing(string to) =>
        Installs(to);

    /// <summary>
    /// The half of Win32's trimming the rule above cannot reach: a component that trims to something rather
    /// than to nothing. Directory.CreateDirectory takes trailing spaces and dots off the last component it is
    /// given, so WriteFile made "repo\docs" and then asked for a file inside "repo\docs " — which is why
    /// "docs /x.md" crashed with an entry already on disk. The check is of the resolved path, so "docs..."
    /// survives GetFullPath and is refused here.
    /// </summary>
    [Theory]
    [InlineData("docs /x.md", "docs ")]
    [InlineData("a /b /x.md", "b ")]
    [InlineData("docs.../x.md", "docs...")]
    [InlineData("docs ../x.md", "docs ..")]
    [InlineData("a/docs /x.md", "docs ")]
    public async Task A_parent_directory_the_platform_trims_is_refused_before_the_write(string to, string parent)
    {
        // Win32's own trimming: elsewhere "docs " is an ordinary directory and refusing it would invent a rule.
        if (!OperatingSystem.IsWindows()) return;

        await Rejects(
            $$"""{"files":[{"from":"source.md","to":"{{to}}"}]}""",
            $": entry 0 writes \"{to}\", whose parent directory \"{parent}\" ends in a space or a dot, "
                + "which this platform cannot create.");
    }

    /// <summary>
    /// The two measurements that make that rule the immediate parent, asked after resolution, rather than
    /// every component asked of the manifest's spelling. "docs " is created and then trimmed away only when it
    /// is the last directory CreateDirectory is handed, so as an intermediate component it installs; and a
    /// single trailing dot is gone before the check ever sees it.
    /// </summary>
    [Theory]
    // The over-refusal a rule over all components would cause: "docs " is fine on the way to "y".
    [InlineData("docs /y/x.md")]
    [InlineData("a /b /c/x.md")]
    // GetFullPath normalises one trailing dot away, so the parent on disk is "docs".
    [InlineData("docs./x.md")]
    public Task A_trimmed_component_that_is_not_the_files_own_parent_still_installs(string to) =>
        OperatingSystem.IsWindows() ? Installs(to) : Task.CompletedTask;

    /// <summary>
    /// Rule 21. A lone surrogate is a legal JSON escape that System.Text.Json parses and then refuses to hand
    /// back, so it threw one statement after the ValueKind check had said "string" — the thing that exists to
    /// turn a bad manifest into a sentence was itself the thing that crashed.
    /// </summary>
    [Theory]
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x\ud800.md"}]}""", "to")]
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x\udc00.md"}]}""", "to")]
    [InlineData("""{"files":[{"from":"so\ud800urce.md","to":"docs/x.md"}]}""", "from")]
    [InlineData("""{"files":[{"from":"so\udc00urce.md","to":"docs/x.md"}]}""", "from")]
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x.md","mode":"\ud800"}]}""", "mode")]
    [InlineData("""{"files":[{"from":"source.md","to":"docs/x.md","mode":"\udc00"}]}""", "mode")]
    public async Task A_string_the_reader_parses_but_will_not_hand_back_is_refused_rather_than_thrown(string manifest, string field)
    {
        var (exit, code, message) = await Rejected(manifest);

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.StartsWith($"{ManifestPath}: entry 0 has a \"{field}\" that is not readable text: ", message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Repository));
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    /// <summary>
    /// The last-resort guard. Three rounds of enumeration each found one more family, so the whole body of
    /// TryReadManifest is wrapped: it names the exception type without assuming what failed or why.
    /// Reached here by handing the
    /// validator a repository path the platform cannot resolve, which no manifest can do and `Install` never
    /// does — a shape that reaches this guard through a manifest is a missing rule, not a pass.
    /// </summary>
    [Fact]
    public void An_exception_no_rule_names_still_leaves_a_sentence_and_the_type_that_caused_it()
    {
        File.WriteAllText(ManifestPath, """{"files":[{"from":"source.md","to":"docs/x.md"}]}""");

        Assert.False(KitCommands.TryReadManifest(ManifestPath, HarnessDir, Kit, "\0", out var entries, out var problem));

        Assert.Empty(entries);
        Assert.StartsWith($"{ManifestPath} could not be validated: ArgumentException: ", problem, StringComparison.Ordinal);
        Assert.EndsWith("The repository was not changed.", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("defect", problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("report", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_locked_read_is_refused_without_writes_and_installs_after_release(bool lockManifest)
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = Path.Combine(HarnessDir, "second.md");
        File.WriteAllText(source, "second procedure");
        File.WriteAllText(ManifestPath, """
            {"files":[
              {"from":"source.md","to":"docs/first.md"},
              {"from":"second.md","to":"new/second.md"}
            ]}
            """);
        Directory.CreateDirectory(Path.Combine(Repository, "docs"));
        Directory.CreateDirectory(Path.Combine(Repository, "empty"));
        File.WriteAllBytes(Path.Combine(Repository, "sentinel"), [0, 255, 13, 10]);
        File.WriteAllText(Path.Combine(Repository, "docs", "first.md"), "original\r\n");
        File.WriteAllText(Path.Combine(Repository, ".gitignore"), "local/\r\n");
        File.WriteAllText(Path.Combine(Repository, ProjectContext.FileName), "{\"key\":\"existing\"}\r\n");
        var before = Directory.GetFiles(Repository, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var directories = Directory.GetDirectories(Repository, "*", SearchOption.AllDirectories).Order().ToArray();
        var prefix = lockManifest
            ? $"{ManifestPath} could not be read: IOException: "
            : $"{source} could not be read while validating {ManifestPath}: IOException: ";

        using (var handle = new FileStream(lockManifest ? ManifestPath : source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var stdout = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(stdout);
            try
            {
                var (exit, code, message) = await Rejected();
                Assert.Equal(ExitCodes.RuleViolation, exit);
                Assert.Equal("invalid_manifest", code);
                Assert.StartsWith(prefix, message, StringComparison.Ordinal);
                Assert.EndsWith("The repository was not changed. It is safe to re-run the command after resolving the read failure.", message, StringComparison.Ordinal);
                Assert.DoesNotContain("defect", message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("report", message, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(stdout.ToString());
                Assert.False(KitCommands.TryReadManifest(ManifestPath, HarnessDir, Kit, Repository, out var entries, out var problem));
                Assert.Empty(entries);
                Assert.Equal(message, problem);
            }
            finally
            {
                Console.SetOut(previous);
            }
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(Repository, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(directories, Directory.GetDirectories(Repository, "*", SearchOption.AllDirectories).Order());
        }

        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", Harness, "--repo", Repository));
        Assert.Equal("a procedure", File.ReadAllText(Path.Combine(Repository, "docs", "first.md")));
        Assert.Equal("second procedure", File.ReadAllText(Path.Combine(Repository, "new", "second.md")));
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
