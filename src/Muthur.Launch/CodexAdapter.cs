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

    /// <summary>
    /// Workspace-write keeps .git read-only and relies on the model asking for escalation, which the hosted models
    /// do and the guardian reviewer grants — 112 specs were attached that way on the live hub. A local model reports
    /// the denial and stops, so a session that has to write .git runs unsandboxed on the local provider; the deny
    /// list it does not have is the prompt's rules, and the catalog opts into that by naming a local mastermind.
    /// </summary>
    private string Sandbox(WorkerRequest request) =>
        openSourceProvider is not null && request.RepositoryWrites ? "danger-full-access" : "workspace-write";

    private static string LastMessageFile(WorkerRequest request) => Path.Combine(request.ScratchDirectory, "codex-last-message.txt");

    public HarnessInvocation Build(WorkerRequest request)
    {
        var arguments = new List<string>
        {
            "exec",
            "--cd", request.WorkingDirectory,
            "--sandbox", Sandbox(request),
            "--output-last-message", LastMessageFile(request),
            "--color", "never",
            "--json",
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
        // approval never: exec mode has nobody to approve, so on-request means every command is refused. The Windows
        // sandbox setting is the one the user's home carries too; without it commands are read-only on Windows.
        File.WriteAllText(Path.Combine(home, "config.toml"),
            "sandbox_mode = \"workspace-write\"\napproval_policy = \"never\"\n\n[windows]\nsandbox = \"elevated\"\n\n[features]\nhooks = false\n");
        return new Dictionary<string, string> { ["CODEX_HOME"] = home };
    }

    public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result)
    {
        var file = LastMessageFile(request);
        var report = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        var usage = ParseEvents(result.StdOut);
        var total = usage.Input is null ? TokensUsed(result.StdOut) ?? TokensUsed(result.StdErr) : null;
        if (result.Ok && report.Length > 0)
            return new WorkerOutcome(true, report, RateLimited: false, InputTokens: usage.Input, OutputTokens: usage.Output,
                CacheReadTokens: usage.CachedInput, TotalTokens: total);

        var said = usage.Error ?? result.Message;
        var text = result.Ok ? said : $"Process exited with code {result.ExitCode}: {said}";
        if (report.Length > 0) text += "\n\nFinal report from this attempt:\n" + report;
        return new WorkerOutcome(false, text, Harnesses.LooksRateLimited(result.StdErr + result.StdOut), InputTokens: usage.Input,
            OutputTokens: usage.Output, CacheReadTokens: usage.CachedInput, TotalTokens: total);
    }

    /// <param name="Input">Input that was not read from the cache; null, like the other counts, when the stream reported no usage.</param>
    internal sealed record Usage(int? Input, int? CachedInput, int? Output, string? Error);

    /// <summary>
    /// Reads the <c>--json</c> event stream: every <c>turn.completed</c>'s usage summed, or when there is none (an
    /// older CLI, a rollout replayed) the last <c>token_count</c>'s running total. Codex counts cached input inside
    /// <c>input_tokens</c>; the ledger's input is the part that was not cached, as the other harnesses report it.
    /// The error is the last <c>turn.failed</c> or <c>error</c> message, since stdout is no longer prose to quote.
    /// </summary>
    internal static Usage ParseEvents(string stdout)
    {
        int input = 0, cached = 0, output = 0; var turns = false;
        (int Input, int Cached, int Output)? running = null;
        string? error = null;
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(trimmed); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                // A rollout wraps its events in a payload; the exec stream does not.
                var body = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload : root;
                switch (body.TryGetProperty("type", out var type) ? type.GetString() : null)
                {
                    case "turn.completed" when body.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object:
                        turns = true;
                        input += Count(usage, "input_tokens"); cached += Count(usage, "cached_input_tokens"); output += Count(usage, "output_tokens");
                        break;
                    case "token_count" when body.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                        info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object:
                        running = (Count(total, "input_tokens"), Count(total, "cached_input_tokens"), Count(total, "output_tokens"));
                        break;
                    case "turn.failed" when body.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object &&
                        failure.TryGetProperty("message", out var said) && said.ValueKind == JsonValueKind.String:
                        error = said.GetString();
                        break;
                    case "error" when body.TryGetProperty("message", out var said) && said.ValueKind == JsonValueKind.String:
                        error = said.GetString();
                        break;
                }
            }
        }
        if (turns) return new(input - Math.Min(cached, input), cached, output, error);
        if (running is { } r) return new(r.Input - Math.Min(r.Cached, r.Input), r.Cached, r.Output, error);
        return new(null, null, null, error);
    }

    private static int Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>
    /// Codex without <c>--json</c> prints "tokens used" and the count on exit, on one line or the next: one number,
    /// cached input included. Read only when the event stream carried no usage. The last occurrence wins: a resumed
    /// session prints one per turn.
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
