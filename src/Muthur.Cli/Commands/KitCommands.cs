using System.Buffers;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

/// <summary>One manifest entry, after validation: every field present, typed, and inside its directory.</summary>
internal readonly record struct KitEntry(string From, string To, string Mode, bool Validator);

/// <summary>Installs the agent kit (role procedures, agent definitions, spec template) into a repository.</summary>
public static partial class KitCommands
{
    public const string KitVariable = "MUTHUR_KIT";

    /// <summary>Written by every install, named here because rule 20 has to know the installer writes it.</summary>
    private const string IgnoreFile = ".gitignore";

    [GeneratedRegex(@"\{\{core:([A-Za-z0-9._-]+)\}\}")]
    private static partial Regex IncludePattern();

    public static void AddTo(RootCommand root)
    {
        var kit = new Command("kit", "The agent kit: procedures and agent definitions for a harness.");
        root.Subcommands.Add(kit);

        var harness = new Option<string>("--harness") { Description = "claude | codex | generic", DefaultValueFactory = _ => "claude" };
        var repo = new Option<string>("--repo") { Description = "Repository to install into.", DefaultValueFactory = _ => "." };
        var key = new Option<string?>("--project-key") { Description = $"Project key for a new {ProjectContext.FileName} (default: directory name)." };
        var install = new Command("install", "Write or update the kit files in a repository. Safe to re-run; MUTHUR owns the files it writes.") { harness, repo, key };
        install.SetAction(parse => Install(parse, parse.GetValue(harness)!, Path.GetFullPath(parse.GetValue(repo)!), parse.GetValue(key)));
        kit.Subcommands.Add(install);

        var list = new Command("list", "List the harnesses the kit supports.");
        list.SetAction(parse =>
        {
            if (LocateKit() is not { } dir) return KitMissing();
            var names = Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "kit.json"))).Select(d => Path.GetFileName(d)!);
            Console.Out.WriteLine("[" + string.Join(",", names.Select(n => $"\"{n}\"")) + "]");
            return ExitCodes.Ok;
        });
        kit.Subcommands.Add(list);
    }

    internal static string? LocateKit()
    {
        if (Environment.GetEnvironmentVariable(KitVariable) is { Length: > 0 } configured)
            return Directory.Exists(configured) ? configured : null;
        var bundled = Path.Combine(AppContext.BaseDirectory, "kit");
        return Directory.Exists(bundled) ? bundled : null;
    }

    internal static int KitMissing() =>
        Output.Error("kit_not_found", $"The kit directory was not found next to the CLI (kit/) and ${KitVariable} is not set.");

    private static int Install(ParseResult parse, string harness, string repo, string? projectKey)
    {
        if (LocateKit() is not { } kitDir) return KitMissing();
        var harnessDir = Path.Combine(kitDir, harness);
        var manifestPath = Path.Combine(harnessDir, "kit.json");
        if (!File.Exists(manifestPath))
            return Output.Error("unknown_harness", $"No kit for harness '{harness}'. Try: muthur kit list", ExitCodes.RuleViolation);
        if (!Directory.Exists(repo))
            return Output.Error("repo_not_found", $"'{repo}' is not a directory.", ExitCodes.NotFound);

        if (!TryReadManifest(manifestPath, harnessDir, kitDir, repo, out var entries, out var problem))
            return Output.Error("invalid_manifest", problem, ExitCodes.RuleViolation);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("harness", harness);
            json.WriteString("repo", repo);
            var briefs = new List<(string Path, bool Validator)>();
            json.WriteStartArray("files");
            foreach (var entry in entries)
            {
                var from = Path.GetFullPath(Path.Combine(harnessDir, entry.From));
                var content = Expand(File.ReadAllText(from), kitDir);
                var status = WriteKitFile(Path.Combine(repo, entry.To), content, entry.Mode);
                if (entry.To.StartsWith(BriefDirectory, StringComparison.Ordinal) && entry.To.EndsWith(".md", StringComparison.Ordinal))
                    briefs.Add((entry.To, entry.Validator));
                json.WriteStartObject();
                json.WriteString("path", entry.To);
                json.WriteString("status", status);
                json.WriteEndObject();
            }

            // Worker and validator worktrees live under .worktrees/ and must never show up as untracked files.
            var ignore = Path.Combine(repo, IgnoreFile);
            var ignored = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
            if (!ignored.Split('\n').Any(line => line.Trim() is ".worktrees/" or ".worktrees"))
            {
                File.WriteAllText(ignore, (ignored.Length > 0 ? ignored.TrimEnd() + "\n" : "") + ".worktrees/\n");
                json.WriteStartObject();
                json.WriteString("path", IgnoreFile);
                json.WriteString("status", "updated");
                json.WriteEndObject();
            }

            var projectFile = Path.Combine(repo, ProjectContext.FileName);
            if (!File.Exists(projectFile))
            {
                var k = (projectKey ?? new DirectoryInfo(repo).Name).Trim().ToLowerInvariant();
                File.WriteAllText(projectFile, $$"""
                    {
                      "key": "{{k}}",
                      "build": "",
                      "test": "",
                      "run": "",
                      "notes": "How to build, test and launch this project unattended. Orchestrators and validators read this."
                    }

                    """);
                json.WriteStartObject();
                json.WriteString("path", ProjectContext.FileName);
                json.WriteString("status", "created");
                json.WriteEndObject();
            }
            json.WriteEndArray();

            // A brief on disk does nothing until a role exists, and only the founder may create one.
            json.WriteStartArray("roles");
            foreach (var (brief, validator) in briefs.Where(b => File.Exists(Path.Combine(repo, b.Path))))
            {
                json.WriteStartObject();
                json.WriteString("role", Path.GetFileNameWithoutExtension(brief));
                json.WriteString("brief", brief);
                json.WriteBoolean("validator", validator);
                json.WriteString("define", RoleDefineCommand(brief, validator));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Output.Emit(parse, new ApiResult(200, Encoding.UTF8.GetString(stream.ToArray())));
    }

    /// <summary>
    /// The manifest's entries, or the error to return. Everything a bad manifest can do is decided here, before
    /// a single file is written: an entry that fails leaves the repository untouched rather than half-installed.
    /// </summary>
    /// <remarks>
    /// The last-resort guard lives here, outside every rule, because three rounds of enumeration each found one
    /// more family and the Goal's sentence — no unhandled exception, no address stack — is a claim a list of
    /// rules cannot keep. It is not the widening the rules themselves refuse: it never shortens a specific
    /// message into a generic one, it names the exception type and says the reader is at fault, and it bounds
    /// the message rather than the filesystem. A shape that reaches it is a missing rule, to be filed.
    /// </remarks>
    internal static bool TryReadManifest(
        string manifestPath, string harnessDir, string kitDir, string repo,
        out List<KitEntry> entries, out string problem)
    {
        entries = [];
        try
        {
            return ReadManifest(manifestPath, harnessDir, kitDir, repo, out entries, out problem);
        }
        catch (Exception ex)
        {
            entries = [];
            problem = $"{manifestPath} could not be read: {ex.GetType().Name}: {ex.Message}. This is a defect in "
                + "MUTHUR's manifest reader, not necessarily in your manifest — please report it.";
            return false;
        }
    }

    /// <summary>The rules themselves, in the order this task's spec numbers them.</summary>
    private static bool ReadManifest(
        string manifestPath, string harnessDir, string kitDir, string repo,
        out List<KitEntry> entries, out string problem)
    {
        entries = [];
        problem = "";

        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{manifestPath} could not be read: {ex.Message}";
            return false;
        }

        JsonDocument manifest;
        try
        {
            manifest = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            problem = $"{manifestPath} is not valid JSON: {ex.Message}";
            return false;
        }

        using (manifest)
        {
            if (manifest.RootElement.ValueKind is not JsonValueKind.Object)
            {
                problem = $"{manifestPath} must contain a JSON object.";
                return false;
            }
            if (!manifest.RootElement.TryGetProperty("files", out var files))
            {
                problem = $"{manifestPath} has no \"files\" array.";
                return false;
            }
            if (files.ValueKind is not JsonValueKind.Array)
            {
                problem = $"{manifestPath}: \"files\" must be an array, not {Kind(files)}.";
                return false;
            }

            var kitRoot = Path.GetFullPath(kitDir);
            var repoRoot = Path.GetFullPath(repo);
            var validated = new List<KitEntry>();
            var destinations = new List<Destination>();
            foreach (var file in files.EnumerateArray())
            {
                var index = validated.Count;
                if (!TryReadEntry(manifestPath, harnessDir, kitRoot, repoRoot, file, index, out var entry, out var destination, out problem))
                    return false;
                validated.Add(entry);
                destinations.Add(new Destination(index, entry.To, destination));
            }

            // Seeded with the files Install writes whether or not the manifest asks for them: an entry writing
            // under one turns it into a directory, and no entry names it, so no comparison of entries sees it.
            foreach (var own in InstallerFiles)
                destinations.Add(new Destination(null, own, Path.Combine(repoRoot, own)));

            if (Collision(destinations) is { } collision)
            {
                problem = Collided(manifestPath, collision.A, collision.B);
                return false;
            }

            // The same pass, asked what the repository actually looks like. A manifest can be faultless and
            // still be unwritable here, and every one of these reaches File.WriteAllText or
            // Directory.CreateDirectory and throws - which in an AOT build with StackTraceSupport=false is an
            // address dump rather than a sentence. Checked before anything is written, not caught after: a
            // try/catch round the write loop turns the crash into a sentence and leaves the half-installed
            // repository, which is the worse half.
            if (Unwritable(manifestPath, repoRoot, destinations, validated) is { } blocked)
            {
                problem = blocked;
                return false;
            }

            entries = validated;
        }
        return true;
    }

    /// <summary>A resolved destination: an entry's, or one of the files <c>Install</c> writes of its own accord.</summary>
    /// <param name="Entry">The entry's index in <c>files</c>, or null for one of the installer's own files.</param>
    private readonly record struct Destination(int? Entry, string Spelled, string Resolved);

    /// <summary>The files <c>Install</c> writes whatever the manifest says, which rule 20 is seeded with.</summary>
    private static readonly string[] InstallerFiles = [IgnoreFile, ProjectContext.FileName];

    /// <summary>
    /// Rule 20, and the first rule here that reads the manifest as a whole: each entry below is valid on its
    /// own, and it is the pair that cannot be honoured. Two entries naming the same path are not a collision —
    /// that is last-one-wins, which the format has always allowed — so only file-versus-directory counts.
    /// </summary>
    /// <summary>
    /// Why the repository cannot take these writes, or null when it can. Three questions the manifest cannot
    /// answer, in the order a founder would meet them: something in the way of a path, then a path that is a
    /// directory, then one that will not be overwritten.
    /// </summary>
    private static string? Unwritable(string manifestPath, string repoRoot, List<Destination> destinations, List<KitEntry> entries)
    {
        foreach (var d in destinations)
        {
            // F2. The installer's own two files are written whatever the manifest says, so a directory at
            // either path fails every install of every kit - with no entry to blame it on.
            if (Directory.Exists(d.Resolved))
                return d.Entry is { } directoryEntry
                    ? $"{manifestPath}: entry {directoryEntry} writes \"{d.Spelled}\", which is an existing directory in the repository."
                    : $"kit install writes \"{d.Spelled}\", but it is a directory in the repository.";

            // F1. WriteFile creates the parent, and Directory.CreateDirectory throws when any ancestor is a
            // file. The mirror of the rule above, with the answer the other way round.
            if (AncestorFile(repoRoot, d.Resolved) is { } ancestor)
                return d.Entry is { } ancestorEntry
                    ? $"{manifestPath}: entry {ancestorEntry} writes \"{d.Spelled}\", but \"{ancestor}\" is a file in the repository, so its parent directory cannot be created."
                    : $"kit install writes \"{d.Spelled}\", but \"{ancestor}\" is a file in the repository, so its parent directory cannot be created.";

            // F3. A read-only destination is an ordinary condition rather than a manifest fault, and is here
            // because it crashes rather than reporting, and because this pass answers it for nothing.
            //
            // Only when this install would actually write it. A "create" entry whose destination already
            // exists is kept untouched, and every shipped kit ships briefs/validator.md that way on purpose -
            // so refusing a read-only one would refuse every install into a repository where a founder had
            // protected their own brief, which is the opposite of what create mode is for.
            if (Writes(d, entries) && File.Exists(d.Resolved) && File.GetAttributes(d.Resolved).HasFlag(FileAttributes.ReadOnly))
                return d.Entry is { } readOnlyEntry
                    ? $"{manifestPath}: entry {readOnlyEntry} writes \"{d.Spelled}\", which is read-only in the repository."
                    : $"kit install writes \"{d.Spelled}\", which is read-only in the repository.";
        }
        return null;
    }

    /// <summary>
    /// Whether this install would put bytes into that path, which is a different question from whether the
    /// path is named. A "create" entry keeps an existing file; <c>muthur.project.json</c> is written only
    /// when absent; <c>.gitignore</c> only when it does not already ignore the worktrees directory.
    /// </summary>
    private static bool Writes(Destination d, List<KitEntry> entries)
    {
        if (d.Entry is { } index)
            return entries[index].Mode != "create" || !File.Exists(d.Resolved);
        if (!File.Exists(d.Resolved)) return true;
        if (string.Equals(d.Spelled, ProjectContext.FileName, StringComparison.Ordinal)) return false;
        var ignored = File.ReadAllText(d.Resolved);
        return !ignored.Split('\n').Any(line => line.Trim() is ".worktrees/" or ".worktrees");
    }

    /// <summary>The first component between the repository root and this path that exists as a file, if any.</summary>
    private static string? AncestorFile(string repoRoot, string resolved)
    {
        var parent = Path.GetDirectoryName(resolved);
        var walked = new List<string>();
        while (parent is { Length: > 0 } && IsInside(parent, repoRoot))
        {
            walked.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }
        walked.Reverse();   // outermost first, so the message names the component a reader would look at
        foreach (var directory in walked)
            if (File.Exists(directory))
                return Path.GetRelativePath(repoRoot, directory).Replace('\\', '/');
        return null;
    }

    private static (Destination A, Destination B)? Collision(List<Destination> destinations)
    {
        for (var a = 0; a < destinations.Count; a++)
            for (var b = a + 1; b < destinations.Count; b++)
                if (IsInside(destinations[a].Resolved, destinations[b].Resolved)
                    || IsInside(destinations[b].Resolved, destinations[a].Resolved))
                    return (destinations[a], destinations[b]);
        return null;
    }

    /// <summary>
    /// Rule 20's message. A collision with one of the installer's own files is named as such rather than as
    /// "entry <c>n</c>": the founder cannot find the other half of it by reading their manifest.
    /// </summary>
    private static string Collided(string manifestPath, Destination a, Destination b)
    {
        if (a.Entry is { } first && b.Entry is { } second)
            return $"{manifestPath}: entry {first} writes \"{a.Spelled}\" and entry {second} writes "
                + $"\"{b.Spelled}\"; one cannot be both a file and a directory.";

        var (entry, installer) = a.Entry is null ? (b, a) : (a, b);
        return $"{manifestPath}: entry {entry.Entry} writes \"{entry.Spelled}\", which collides with "
            + $"\"{installer.Spelled}\", a file kit install writes itself.";
    }

    /// <summary>One entry of <c>files</c>, checked field by field so the message can name the field that is wrong.</summary>
    /// <param name="destination">The resolved <c>to</c>, which rule 20 needs to compare entries against each other.</param>
    private static bool TryReadEntry(
        string manifestPath, string harnessDir, string kitRoot, string repoRoot,
        JsonElement file, int index, out KitEntry entry, out string destination, out string problem)
    {
        entry = default;
        destination = "";
        problem = "";

        if (file.ValueKind is not JsonValueKind.Object)
        {
            problem = $"{manifestPath}: entry {index} must be an object, not {Kind(file)}.";
            return false;
        }
        if (!file.TryGetProperty("from", out var fromField))
        {
            problem = $"{manifestPath}: entry {index} has no \"from\".";
            return false;
        }
        if (fromField.ValueKind is not JsonValueKind.String)
        {
            problem = $"{manifestPath}: entry {index} has a \"from\" that is {Kind(fromField)}, not a string.";
            return false;
        }
        if (!file.TryGetProperty("to", out var toField))
        {
            problem = $"{manifestPath}: entry {index} has no \"to\".";
            return false;
        }
        if (toField.ValueKind is not JsonValueKind.String)
        {
            problem = $"{manifestPath}: entry {index} has a \"to\" that is {Kind(toField)}, not a string.";
            return false;
        }

        if (!TryReadText(fromField, out var from, out var unreadable))
        {
            problem = $"{manifestPath}: entry {index} has a \"from\" that is not readable text: {unreadable}";
            return false;
        }
        if (!TryReadText(toField, out var to, out unreadable))
        {
            problem = $"{manifestPath}: entry {index} has a \"to\" that is not readable text: {unreadable}";
            return false;
        }

        // Named before the containment rules reach it: Path.Combine(repo, "") is the repository itself, and
        // being told an empty string is outside the repository helps nobody.
        if (to.Length == 0)
        {
            problem = $"{manifestPath}: entry {index} has an empty \"to\".";
            return false;
        }

        var mode = "replace";
        if (file.TryGetProperty("mode", out var modeField))
        {
            if (modeField.ValueKind is not JsonValueKind.String)
            {
                problem = $"{manifestPath}: entry {index} ({to}) has a \"mode\" that is {Kind(modeField)}, not a string.";
                return false;
            }
            if (!TryReadText(modeField, out mode, out unreadable))
            {
                problem = $"{manifestPath}: entry {index} has a \"mode\" that is not readable text: {unreadable}";
                return false;
            }
        }

        var validator = false;
        if (file.TryGetProperty("validator", out var validatorField))
        {
            if (validatorField.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problem = $"{manifestPath}: entry {index} ({to}) has a \"validator\" that is {Kind(validatorField)}, not a boolean.";
                return false;
            }
            validator = validatorField.GetBoolean();
        }

        // A component longer than the filesystem allows resolves happily and then kills Directory.CreateDirectory
        // in the write phase, which is the one shape that used to escape validation altogether.
        if (TooLongComponent(from) is { } longFrom)
        {
            problem = $"{manifestPath}: entry {index} reads \"{from}\", {Overlong(longFrom)}";
            return false;
        }

        // Path.Combine hands back an absolute 'from' unchanged and follows '..' when it is relative, so a
        // manifest picks its own source unless the resolved path is required to be under the kit.
        if (!TryResolve(harnessDir, from, out var source, out var refusal))
        {
            problem = $"{manifestPath}: entry {index} has a \"from\" that is not a usable path: {refusal}";
            return false;
        }
        if (!IsInside(source, kitRoot))
        {
            problem = $"{manifestPath}: entry {index} ({to}) reads \"{from}\", which is outside the kit directory.";
            return false;
        }
        if (!File.Exists(source))
        {
            problem = $"{manifestPath}: entry {index} ({to}) reads \"{from}\", which does not exist.";
            return false;
        }

        if (TooLongComponent(to) is { } longTo)
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", {Overlong(longTo)}";
            return false;
        }

        // The same for the destination, plus the rooted case: Path.Combine(repo, to) is just 'to' when 'to' is
        // absolute, which is how a manifest can write to C:\Windows while --repo says otherwise.
        if (!TryResolve(repoRoot, to, out destination, out refusal))
        {
            problem = $"{manifestPath}: entry {index} has a \"to\" that is not a usable path: {refusal}";
            return false;
        }
        if (Path.IsPathRooted(to) || !IsInside(destination, repoRoot))
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", which is outside the repository.";
            return false;
        }
        if (UnusableCharacter(to) is { } forbidden)
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", which contains \\u{(int)forbidden:x4}, "
                + "a character this platform does not allow in a file name.";
            return false;
        }

        // Rule 19. A last component that is empty, "." or ".." names the directory above it and not a file in
        // it. WriteFile creates that directory and then hands File.WriteAllText a path with nothing on the end
        // of it, which threw after the directory existed: half an install for no entry at all.
        if (Components(to)[^1] is "" or "." or "..")
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", which names a directory, not a file.";
            return false;
        }
        if (ReservedComponent(to) is { } reserved)
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", whose component \"{reserved}\" "
                + "is a reserved device name on this platform.";
            return false;
        }
        if (TrimmedAwayComponent(to) is { } trimmed)
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", whose component \"{trimmed}\" is only "
                + "spaces and dots, which this platform trims to nothing.";
            return false;
        }
        if (UncreatableParent(destination) is { } parent)
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", whose parent directory \"{parent}\" "
                + "ends in a space or a dot, which this platform cannot create.";
            return false;
        }

        // Decided by the repository rather than by the manifest, which is the question rule 13 already asks one
        // directory over: it calls File.Exists on the source. WriteFile would ask File.WriteAllText to overwrite
        // a directory, and it throws — reachable by re-running an install after a "to" changed from a file to a
        // directory name.
        if (Directory.Exists(destination))
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", which is an existing directory in the repository.";
            return false;
        }

        // Expand() reads whatever a {{core:...}} token names, so the token is checked here rather than
        // discovered halfway through the write loop.
        foreach (var name in IncludePattern().Matches(File.ReadAllText(source)).Select(m => m.Groups[1].Value))
            if (!File.Exists(Path.Combine(kitRoot, "core", name)))
            {
                problem = $"{manifestPath}: entry {index} ({to}) includes \"{{{{core:{name}}}}}\", which does not exist.";
                return false;
            }

        entry = new KitEntry(from, to, mode, validator);
        return true;
    }

    /// <summary>The kind as the founder wrote it in the file: <c>number</c>, <c>string</c>, <c>null</c>.</summary>
    private static string Kind(JsonElement element) => element.ValueKind.ToString().ToLowerInvariant();

    /// <summary>
    /// Rule 21. Saying an element is a string is not the same as being able to read it: a lone surrogate escape
    /// is legal JSON that <c>System.Text.Json</c> parses and then refuses to materialise, so the check that was
    /// meant to turn a bad manifest into a sentence threw one statement after passing. The catch is around the
    /// call alone — the kind has already been established, so nothing else here can raise it.
    /// </summary>
    private static bool TryReadText(JsonElement element, out string text, out string refusal)
    {
        try
        {
            text = element.GetString()!;
        }
        catch (InvalidOperationException ex)
        {
            text = "";
            refusal = ex.Message;
            return false;
        }
        refusal = "";
        return true;
    }

    /// <summary>Containment by full path, which a string test on the manifest's own spelling cannot give.</summary>
    private static bool IsInside(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rule 16. Resolution is where manifest text meets the platform's own idea of a path, and a string the
    /// platform will not accept at all — a NUL, a shape it has no syntax for — must refuse here rather than
    /// escape as an unhandled exception. The three types named are the class of "this is not a path"; catching
    /// <c>Exception</c> instead would also swallow a bug in the validator, which is the opposite of the point.
    /// </summary>
    private static bool TryResolve(string baseDir, string value, out string resolved, out string refusal)
    {
        try
        {
            resolved = Path.GetFullPath(Path.Combine(baseDir, value));
            refusal = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            resolved = "";
            refusal = ex.Message;
            return false;
        }
    }

    /// <summary>NTFS's per-component limit, and most Linux filesystems'. Longer than this and the path resolves
    /// but cannot be created, so the manifest is refused here instead of halfway through the write loop.</summary>
    private const int MaxPathComponent = 255;

    /// <summary>The path as the platform separates it; both separators, because a manifest may spell it either way.</summary>
    private static string[] Components(string value) =>
        value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Rule 17. The first component of <paramref name="value"/> that is over the limit, or null.</summary>
    private static string? TooLongComponent(string value) =>
        Components(value).FirstOrDefault(part => part.Length > MaxPathComponent);

    /// <summary>The tail of rule 17's message: the component is by definition too long to print whole.</summary>
    private static string Overlong(string component) =>
        $"whose path component \"{component[..40]}…\" is {component.Length} characters; the limit is {MaxPathComponent}.";

    /// <summary>Which characters a name may not hold is the platform's business, not this validator's.</summary>
    private static readonly SearchValues<char> NotInAFileName = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Rule 18. Windows refuses a control character in a file name where <c>Path.GetFullPath</c> accepts it, so
    /// a "to" carrying one used to pass validation whole and die in the write phase — having already created
    /// the directory above it. Separators are on the platform's list and are legitimate between components,
    /// which is why each component is tested rather than the string; a NUL is rule 16's, one step earlier.
    /// Only "to" needs this: a "from" the platform cannot name is a file that cannot exist, and rule 13 says so.
    /// </summary>
    private static char? UnusableCharacter(string value)
    {
        foreach (var part in Components(value))
        {
            var at = part.AsSpan().IndexOfAny(NotInAFileName);
            if (at >= 0) return part[at];
        }
        return null;
    }

    /// <summary>
    /// The one device name a directory component may not be. The whole Windows device namespace was refused
    /// first, on an assertion; Directory.CreateDirectory was then measured on each name, and CON, PRN, AUX,
    /// COM0, COM1 and LPT1 all create ordinary directories. Only NUL is followed into the device namespace,
    /// so only NUL is refused: the rest install cleanly, and refusing them would both refuse working manifests
    /// and contradict the measurement that left the same names alone as file names.
    /// </summary>
    private const string NullDevice = "NUL";

    /// <summary>
    /// The null device used as a *directory* component, which is one level above rule 18: the name holds no
    /// forbidden character and resolves cleanly, and then Windows follows it into the device namespace instead
    /// of creating a directory. As a file name it is an ordinary file inside the repository, so only the
    /// components before the last are asked about. Trailing dots and spaces come off first, because Win32
    /// takes them off before it looks the name up: "NUL " and "NUL." are the same device.
    /// </summary>
    private static string? ReservedComponent(string value)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var components = Components(value);
        for (var i = 0; i < components.Length - 1; i++)
            if (string.Equals(components[i].TrimEnd(' ', '.'), NullDevice, StringComparison.OrdinalIgnoreCase))
                return components[i];
        return null;
    }

    /// <summary>
    /// The same class as rules 17, 18 and 19 — a component that resolves but cannot be created — said by what
    /// Win32 takes off a name. It trims trailing spaces and dots, so a component made only of those trims to
    /// nothing and the directory beneath it never exists: "   /x.md" and ".../x.md" passed every other rule
    /// and died in the write phase. The empty component and the two navigation segments are excluded because
    /// Path.GetFullPath resolves them away rather than trimming them: "docs//x.md", "./docs/x.md" and
    /// "a/../docs/x.md" all install, and refusing them would refuse manifests that work.
    /// </summary>
    private static string? TrimmedAwayComponent(string value)
    {
        if (!OperatingSystem.IsWindows()) return null;

        return Components(value).FirstOrDefault(part =>
            part is not ("" or "." or "..") && part.TrimEnd(' ', '.').Length == 0);
    }

    /// <summary>
    /// The other half of what Win32 trims, and the half the sibling rule above cannot reach: a component that
    /// trims to something rather than to nothing. Directory.CreateDirectory takes trailing spaces and dots off
    /// the last component it is handed, so WriteFile creates "repo\docs" and then asks File.WriteAllText for a
    /// file in "repo\docs ", which does not exist. Only the file's *immediate parent* is asked about, because
    /// that is the only component CreateDirectory trims: "docs /y/x.md" creates "docs " happily on the way to
    /// "y" and installs, so a rule over every component would refuse a manifest that works. Asked of the
    /// resolved path rather than of the manifest's spelling, which is what lets "docs./x.md" through — the
    /// single trailing dot is already gone — while "docs.../x.md" is refused, because those dots are kept.
    /// </summary>
    private static string? UncreatableParent(string destination)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var parent = Path.GetFileName(Path.GetDirectoryName(destination));
        return parent is [.., ' ' or '.'] ? parent : null;
    }

    private static string Expand(string template, string kitDir) =>
        IncludePattern().Replace(template, match => File.ReadAllText(Path.Combine(kitDir, "core", match.Groups[1].Value)).Trim());

    /// <summary>Where starter briefs land in the target repository; their filenames are the role keys.</summary>
    internal const string BriefDirectory = "briefs/";

    /// <summary>
    /// The command that turns a brief into a role. The hub infers the validator flag from a '-validator' suffix
    /// (<c>RoleService</c>), so a brief named otherwise has to ask for the flag itself or the founder ends up with
    /// a validator role that says it is not one.
    /// </summary>
    internal static string RoleDefineCommand(string brief, bool validator)
    {
        var role = Path.GetFileNameWithoutExtension(brief);
        var flag = validator && !role.EndsWith("-validator", StringComparison.Ordinal) ? " --validator" : "";
        return $"muthur role define {role} --brief-file {brief}{flag} --founder";
    }

    /// <summary>
    /// Applies one manifest entry. <c>create</c> exists because a brief stops being MUTHUR's the moment the
    /// founder edits it, and re-running the install must never take that edit away.
    /// </summary>
    internal static string WriteKitFile(string path, string content, string? mode) => mode switch
    {
        "section" => WriteSection(path, content),
        "create" => File.Exists(path) ? "kept" : WriteFile(path, content),
        _ => WriteFile(path, content),
    };

    private static string WriteFile(string path, string content)
    {
        content = content.ReplaceLineEndings("\n");
        var existed = File.Exists(path);
        if (existed && File.ReadAllText(path).ReplaceLineEndings("\n") == content) return "unchanged";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return existed ? "updated" : "created";
    }

    /// <summary>For files the repository also owns (AGENTS.md): maintain only a marked MUTHUR section inside them.</summary>
    private static string WriteSection(string path, string content)
    {
        const string begin = "<!-- BEGIN MUTHUR -->";
        const string end = "<!-- END MUTHUR -->";
        var section = $"{begin}\n{content.ReplaceLineEndings("\n").Trim()}\n{end}\n";
        if (!File.Exists(path)) return WriteFile(path, section);

        var existing = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = existing.IndexOf(begin, StringComparison.Ordinal);
        var stop = existing.IndexOf(end, StringComparison.Ordinal);
        var updated = start >= 0 && stop > start
            ? existing[..start] + section + existing[(stop + end.Length)..].TrimStart('\n')
            : existing.TrimEnd('\n') + "\n\n" + section;
        return WriteFile(path, updated);
    }
}
