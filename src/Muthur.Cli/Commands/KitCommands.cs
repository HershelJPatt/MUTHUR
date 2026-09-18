using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

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

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("harness", harness);
            json.WriteString("repo", repo);
            var briefs = new List<string>();
            json.WriteStartArray("files");
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var from = Path.GetFullPath(Path.Combine(harnessDir, file.GetProperty("from").GetString()!));
                var to = file.GetProperty("to").GetString()!;
                var mode = file.TryGetProperty("mode", out var m) ? m.GetString() : "replace";
                var content = Expand(File.ReadAllText(from), kitDir);
                var status = WriteKitFile(Path.Combine(repo, to), content, mode);
                if (to.StartsWith(BriefDirectory, StringComparison.Ordinal) && to.EndsWith(".md", StringComparison.Ordinal))
                    briefs.Add(to);
                json.WriteStartObject();
                json.WriteString("path", to);
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
            foreach (var brief in briefs.Where(b => File.Exists(Path.Combine(repo, b))))
            {
                var role = Path.GetFileNameWithoutExtension(brief);
                json.WriteStartObject();
                json.WriteString("role", role);
                json.WriteString("brief", brief);
                json.WriteString("define", $"muthur role define {role} --brief-file {brief} --founder");
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Output.Emit(parse, new ApiResult(200, Encoding.UTF8.GetString(stream.ToArray())));
    }

    private static string Expand(string template, string kitDir) =>
        IncludePattern().Replace(template, match => File.ReadAllText(Path.Combine(kitDir, "core", match.Groups[1].Value)).Trim());

    /// <summary>Where starter briefs land in the target repository; their filenames are the role keys.</summary>
    internal const string BriefDirectory = "briefs/";

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
