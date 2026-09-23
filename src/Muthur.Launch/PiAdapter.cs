using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Muthur.Launch;

/// <summary>
/// The pi coding agent in print mode, one JSON record per event on stdout. It carries almost no harness of its
/// own — a system prompt and eight tools, about 1,400 tokens on a local model where Codex's local provider spends
/// 8,900 for the same one-word answer — and no sandbox at all, so the boundary a session has is the prompt's rules
/// and the worktree it is started in. A local model (provider <c>ollama</c>, the default when the catalog model
/// names no provider) runs from a scratch agent directory whose model catalog names only that model; a hosted
/// provider keeps the user's own directory, where its credentials live. The session's deny list is a
/// <see cref="PiGuard"/> extension, loaded by path because <c>--no-extensions</c> turns off only discovered ones.
/// pi's default system prompt (its own tool list, rules and documentation paths) is replaced by <see cref="SystemPrompt"/>;
/// the project's AGENTS.md and CLAUDE.md still load, since only <c>--no-context-files</c> turns them off.
/// </summary>
public sealed class PiAdapter : IHarnessAdapter
{
    public string Name => "pi";

    public string? WorkerNote => null;

    public string? CapabilityExecutable => "pi";

    public string? GuardBlockMarker => PiGuard.BlockMarker;

    /// <summary>pi's bash tool is Git Bash on Windows and sh everywhere else; it has no PowerShell unless asked for one.</summary>
    public CapabilityShell ProbeShell => CapabilityShell.Posix;

    /// <summary>The environment variable pi reads its agent directory (models, settings, extensions) from.</summary>
    public const string AgentDirectoryVariable = "PI_CODING_AGENT_DIR";

    /// <summary>The Ollama endpoint the scratch catalog points at; overridable the way Ollama's own clients are.</summary>
    public static string OllamaBaseUrl =>
        (Environment.GetEnvironmentVariable("OLLAMA_HOST") is { Length: > 0 } host ? (host.Contains("://") ? host : "http://" + host) : "http://localhost:11434").TrimEnd('/') + "/v1";

    /// <summary>
    /// A hosted provider reads the user's agent directory; a local one reads its own scratch directory instead. Both
    /// read the project's <c>.pi/settings.json</c>. The guard is hashed in, so a changed deny list is a different
    /// configuration.
    /// </summary>
    public string? CapabilitySettings(WorkerRequest request)
    {
        var project = ("project", Path.Combine(request.WorkingDirectory, ".pi", "settings.json"));
        var inherited = Launch.CapabilitySettings.HashFiles(Split(request.Model).Provider == "ollama" ? [project] : [project,
            ("user", Path.Combine(Environment.GetEnvironmentVariable(AgentDirectoryVariable) is { Length: > 0 } directory ? directory :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent"), "settings.json"))]);
        return inherited is null ? null : CapabilityHash.Of(inherited + "\n" + PiGuard.Extension(request));
    }

    public const string SystemPromptFileName = "pi-system.md";

    /// <summary>The context window a local model is given when the catalog names none.</summary>
    public const int DefaultContextWindow = 32768;

    /// <summary>A validator judges the work and does not change it, so it gets no tool that edits a file.</summary>
    public static string Tools(string role) =>
        role == SessionRoles.Validator ? "read,bash,grep,find,ls" : "read,bash,edit,write,grep,find,ls";

    /// <summary>
    /// What the model is told before the assignment: its seat, where it works, what the launcher already checked and
    /// what the guard will refuse. The assignment itself arrives on stdin.
    /// </summary>
    public static string SystemPrompt(WorkerRequest request)
    {
        var text = new StringBuilder();
        text.Append($"You are a MUTHUR {request.Role}, running headless in pi with the tools {Tools(request.Role).Replace(",", ", ")}. ");
        text.Append(request.Role switch
        {
            SessionRoles.Orchestrator => "You take one task through MUTHUR as its orchestrator: spec, dispatch, review, hand to validation.",
            SessionRoles.Validator => "You validate one implemented task end to end and record a verdict with evidence; you do not change the work you judge.",
            SessionRoles.Overseer => "You oversee the organization through the muthur CLI.",
            _ => "You implement one unit of a frozen spec on your own branch, and nothing else.",
        });
        text.Append("\n\n");
        text.Append($"- Working directory: `{request.WorkingDirectory.Replace('\\', '/')}`. Work only inside it.\n");
        text.Append("- The bash tool runs a POSIX shell (Git Bash on Windows): write commands for sh, not PowerShell.\n");
        if (WorkerPrompt.VerifiedBlock(request.Prompt) is { } verified) text.Append(verified).Append('\n');
        var denied = request.DeniedCommands.Where(c => c.Trim().Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (denied.Length > 0)
            text.Append($"- You may not run {string.Join(", ", denied.Select(d => $"`{d}`"))}, alone or inside a chain; a guard refuses them. " +
                "When one is refused, do not retry it in another form: finish without it and say so in your report.\n");
        text.Append("- Files under `.git/` are changed through git commands, never edited.\n");
        text.Append("- The user message is your whole assignment. Follow it exactly, be brief, and end with the report it asks for.\n");
        return text.ToString();
    }

    private static string OutputFile(WorkerRequest request) => Path.Combine(request.ScratchDirectory, "pi-events.jsonl");

    /// <summary>"provider/id" as pi wants it; a bare id is a local Ollama model.</summary>
    internal static (string Provider, string Id) Split(string model)
    {
        var slash = model.IndexOf('/');
        return slash > 0 ? (model[..slash], model[(slash + 1)..]) : ("ollama", model);
    }

    public HarnessInvocation Build(WorkerRequest request)
    {
        var (provider, id) = Split(request.Model);
        var arguments = new List<string>
        {
            "--print", "--mode", "json", "--no-session", "--no-approve", "--offline",
            "--no-extensions", "--no-skills", "--no-prompt-templates",
            "--provider", provider, "--model", $"{provider}/{id}",
            "--tools", Tools(request.Role),
        };
        var system = Path.Combine(request.ScratchDirectory, SystemPromptFileName);
        File.WriteAllText(system, SystemPrompt(request));
        arguments.Add("--system-prompt");
        arguments.Add(system);
        var guard = Path.Combine(request.ScratchDirectory, PiGuard.FileName);
        File.WriteAllText(guard, PiGuard.Extension(request));
        arguments.Add("-e");
        arguments.Add(guard);
        // pi's levels are Codex's plus the ends of the scale; the catalog's word is passed as it is, and off means
        // the model's own default when nothing was asked for.
        arguments.Add("--thinking");
        arguments.Add(request.ReasoningEffort is { Length: > 0 } effort ? effort : "off");
        Dictionary<string, string>? environment = null;
        if (provider == "ollama")
        {
            var home = Path.Combine(request.ScratchDirectory, "pi-home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "models.json"), ScratchCatalog(id, request.ContextWindow ?? DefaultContextWindow));
            environment = new Dictionary<string, string> { [AgentDirectoryVariable] = home };
        }
        return new HarnessInvocation("pi", arguments, request.Prompt, environment);
    }

    /// <summary>A catalog with one provider and one model: the run cannot drift onto anything the catalog did not name.</summary>
    internal static string ScratchCatalog(string modelId, int contextWindow = DefaultContextWindow)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartObject("providers");
            json.WriteStartObject("ollama");
            json.WriteString("baseUrl", OllamaBaseUrl);
            json.WriteString("api", "openai-completions");
            json.WriteString("apiKey", "ollama");
            json.WriteStartObject("compat");
            json.WriteBoolean("supportsDeveloperRole", false);
            json.WriteBoolean("supportsReasoningEffort", false);
            json.WriteEndObject();
            json.WriteStartArray("models");
            json.WriteStartObject();
            json.WriteString("id", modelId);
            json.WriteBoolean("reasoning", false);
            json.WriteNumber("contextWindow", contextWindow);
            json.WriteNumber("maxTokens", 4096);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result)
    {
        var parsed = Parse(result.StdOut);
        var text = parsed.FinalText ?? result.Message;
        var failed = !result.Ok || parsed.StopReason is "error" or "aborted";
        var rateLimited = failed && Harnesses.LooksRateLimited(text + result.StdErr);
        return new WorkerOutcome(!failed, text, rateLimited, parsed.Cost, null,
            parsed.Input, parsed.Output, parsed.CacheRead);
    }

    internal sealed record Parsed(string? FinalText, string? StopReason, int? Input, int? Output, int? CacheRead, decimal? Cost);

    /// <summary>
    /// Reads pi's event stream: the last assistant message's text and stop reason, and the usage of every assistant
    /// message summed, since a tool-using run has one per turn. Lines that are not JSON records are skipped: the
    /// model's own output never reaches stdout in this mode, but a stray warning might.
    /// </summary>
    internal static Parsed Parse(string stdout)
    {
        string? finalText = null, stopReason = null;
        int input = 0, output = 0, cacheRead = 0; var any = false; decimal cost = 0; var anyCost = false;
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
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "message_end") continue;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!(message.TryGetProperty("role", out var role) && role.GetString() == "assistant")) continue;
                if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    any = true;
                    input += Count(usage, "input"); output += Count(usage, "output"); cacheRead += Count(usage, "cacheRead");
                    if (usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Object &&
                        c.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number)
                    { cost += total.GetDecimal(); anyCost = true; }
                }
                if (message.TryGetProperty("stopReason", out var stop) && stop.ValueKind == JsonValueKind.String) stopReason = stop.GetString();
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var text = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var kind) && kind.GetString() == "text" && part.TryGetProperty("text", out var t))
                            text.Append(t.GetString());
                    if (text.Length > 0) finalText = text.ToString().Trim();
                }
            }
        }
        return new Parsed(finalText, stopReason, any ? input : null, any ? output : null, any ? cacheRead : null, anyCost && cost > 0 ? cost : null);
    }

    private static int Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
