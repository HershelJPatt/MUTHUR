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
            var ignore = Path.Combine(repo, ".gitignore");
            var ignored = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
            if (!ignored.Split('\n').Any(line => line.Trim() is ".worktrees/" or ".worktrees"))
            {
                File.WriteAllText(ignore, (ignored.Length > 0 ? ignored.TrimEnd() + "\n" : "") + ".worktrees/\n");
                json.WriteStartObject();
                json.WriteString("path", ".gitignore");
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
    internal static bool TryReadManifest(
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
            var index = 0;
            var validated = new List<KitEntry>();
            foreach (var file in files.EnumerateArray())
            {
                if (!TryReadEntry(manifestPath, harnessDir, kitRoot, repoRoot, file, index++, out var entry, out problem))
                    return false;
                validated.Add(entry);
            }
            entries = validated;
        }
        return true;
    }

    /// <summary>One entry of <c>files</c>, checked field by field so the message can name the field that is wrong.</summary>
    private static bool TryReadEntry(
        string manifestPath, string harnessDir, string kitRoot, string repoRoot,
        JsonElement file, int index, out KitEntry entry, out string problem)
    {
        entry = default;
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

        var from = fromField.GetString()!;
        var to = toField.GetString()!;

        var mode = "replace";
        if (file.TryGetProperty("mode", out var modeField))
        {
            if (modeField.ValueKind is not JsonValueKind.String)
            {
                problem = $"{manifestPath}: entry {index} ({to}) has a \"mode\" that is {Kind(modeField)}, not a string.";
                return false;
            }
            mode = modeField.GetString()!;
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

        // Path.Combine hands back an absolute 'from' unchanged and follows '..' when it is relative, so a
        // manifest picks its own source unless the resolved path is required to be under the kit.
        var source = Path.GetFullPath(Path.Combine(harnessDir, from));
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

        // The same for the destination, plus the rooted case: Path.Combine(repo, to) is just 'to' when 'to' is
        // absolute, which is how a manifest can write to C:\Windows while --repo says otherwise.
        if (Path.IsPathRooted(to) || !IsInside(Path.GetFullPath(Path.Combine(repoRoot, to)), repoRoot))
        {
            problem = $"{manifestPath}: entry {index} writes \"{to}\", which is outside the repository.";
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

    /// <summary>Containment by full path, which a string test on the manifest's own spelling cannot give.</summary>
    private static bool IsInside(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
