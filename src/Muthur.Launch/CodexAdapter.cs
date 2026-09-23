using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muthur.Launch;

/// <summary>
/// Codex CLI in exec mode; with an open-source provider it drives a local model (Ollama) through the same harness.
/// Codex has no per-command allow list: its boundary is the workspace-write sandbox, so command lists are not used here.
/// </summary>
public sealed partial class CodexAdapter(string name, string? openSourceProvider) : IHarnessAdapter
{
    public string Name => name;

    public string? CapabilityExecutable => "codex";

    public string? CapabilitySettings(WorkerRequest request)
    {
        var inherited = Launch.CapabilitySettings.HashFiles([
            ("user", Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "config.toml")),
            ("project", Path.Combine(request.WorkingDirectory, ".codex", "config.toml"))]);
        if (inherited is null) return null;
        var environment = new Dictionary<string, string>(request.GitEnvironment ?? new Dictionary<string, string>());
        if (environment.TryGetValue("GIT_CONFIG_COUNT", out var count) && int.TryParse(count, out var number) && number > 0 &&
            environment.GetValueOrDefault($"GIT_CONFIG_KEY_{number - 1}") == "safe.directory" &&
            environment.GetValueOrDefault($"GIT_CONFIG_VALUE_{number - 1}") == Path.GetFullPath(request.WorkingDirectory).Replace('\\', '/'))
            environment[$"GIT_CONFIG_VALUE_{number - 1}"] = "{allocated-worktree}";
        var invocation = Build(request with { WorkingDirectory = "{allocated-worktree}", ScratchDirectory = "{adapter-scratch}", GitEnvironment = environment });
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            json.WriteStringValue(inherited);
            foreach (var argument in invocation.Arguments) json.WriteStringValue(argument);
            json.WriteEndArray();
        }
        return CapabilityHash.Of(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>The workspace-write sandbox keeps .git read-only, so a Codex worker cannot commit; the launcher does it.</summary>
    public string? WorkerNote =>
        "Your sandbox does not allow writing to .git, so `git add`/`git commit` will be refused. That is expected and is NOT a blocker: " +
        "leave your finished work uncommitted in the worktree and the launcher commits it for you. Report `COMMITS: pending (launcher)` and STATUS by the state of the work itself.";

    private static string LastMessageFile(WorkerRequest request) => Path.Combine(request.ScratchDirectory, "codex-last-message.txt");

    public HarnessInvocation Build(WorkerRequest request)
    {
        var arguments = new List<string>
        {
            "exec",
            "--cd", request.WorkingDirectory,
            "--sandbox", "workspace-write",
            "--output-last-message", LastMessageFile(request),
            "--color", "never",
            "-c", "service_tier=\"default\"",
            "-c", "features.fast_mode=false",
        };
        if (!request.RequireRepository) arguments.Add("--skip-git-repo-check");
        foreach (var (key, value) in request.GitEnvironment ?? new Dictionary<string, string>())
        {
            // Only the appended trust entry belongs on the command line. Inherited Git settings may contain
            // credentials; they stay in the child environment and are never copied to arguments or logs.
            var last = int.Parse(request.GitEnvironment!["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture) - 1;
            if (key != "GIT_CONFIG_COUNT" && key != $"GIT_CONFIG_KEY_{last}" && key != $"GIT_CONFIG_VALUE_{last}") continue;
            arguments.Add("-c");
            arguments.Add($"shell_environment_policy.set.{key}=\"{JsonEncodedText.Encode(value)}\"");
        }
        if (request.GitCommonDirectory is { Length: > 0 } git)
        {
            // A worktree's index and refs live in the main repository's .git; without this the worker cannot commit.
            arguments.Add("--add-dir");
            arguments.Add(git);
        }
        if (openSourceProvider is not null)
        {
            arguments.Add("--oss");
            arguments.Add("--local-provider");
            arguments.Add(openSourceProvider);
        }
        if (request.Model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }
        if (request.ReasoningEffort is { Length: > 0 } effort)
        {
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort=\"{effort}\"");
        }
        arguments.Add("-"); // prompt on stdin
        return new HarnessInvocation("codex", arguments, request.Prompt, openSourceProvider is null ? null : BareHome(request));
    }

    /// <summary>
    /// A local model runs from a Codex home of its own, with nothing in it. The user's home carries plugins,
    /// hooks and marketplaces, and every one of them is a tool definition the model reads before the prompt:
    /// measured on gemma4:26b, one word of answer cost 16,393 tokens and 27 s from the user's home and 8,871
    /// tokens and 5 s from an empty one — and from the full home the model answered a question about Google Drive
    /// that nobody asked. The hosted Codex harness keeps the user's home, because that is where its login lives.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BareHome(WorkerRequest request)
    {
        var home = Path.Combine(request.ScratchDirectory, "codex-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), "sandbox_mode = \"workspace-write\"\n\n[features]\nhooks = false\n");
        return new Dictionary<string, string> { ["CODEX_HOME"] = home };
    }

    public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result)
    {
        var file = LastMessageFile(request);
        var report = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        var tokens = TokensUsed(result.StdOut) ?? TokensUsed(result.StdErr);
        if (result.Ok && report.Length > 0) return new WorkerOutcome(true, report, RateLimited: false, TotalTokens: tokens);

        var text = result.Ok ? result.Message : $"Process exited with code {result.ExitCode}: {result.Message}";
        if (report.Length > 0) text += "\n\nFinal report from this attempt:\n" + report;
        return new WorkerOutcome(false, text, Harnesses.LooksRateLimited(result.StdErr + result.StdOut), TotalTokens: tokens);
    }

    /// <summary>
    /// Codex prints "tokens used" and the count on exit, on one line or the next; it is the only usage figure the CLI
    /// gives a headless caller, so it is the one the ledger gets. The last occurrence wins: a resumed session prints one per turn.
    /// </summary>
    public static int? TokensUsed(string text)
    {
        int? found = null;
        foreach (Match m in TokensUsedLine().Matches(text))
            if (int.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                found = n;
        return found;
    }

    [GeneratedRegex(@"tokens used[:\s]*\r?\n?\s*([\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TokensUsedLine();
}
