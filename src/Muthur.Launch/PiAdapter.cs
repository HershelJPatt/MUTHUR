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
/// provider keeps the user's own directory, where its credentials live.
/// </summary>
public sealed class PiAdapter : IHarnessAdapter
{
    public string Name => "pi";

    public string? WorkerNote => null;

    public string? CapabilityExecutable => "pi";

    /// <summary>The environment variable pi reads its agent directory (models, settings, extensions) from.</summary>
    public const string AgentDirectoryVariable = "PI_CODING_AGENT_DIR";

    /// <summary>The Ollama endpoint the scratch catalog points at; overridable the way Ollama's own clients are.</summary>
    public static string OllamaBaseUrl =>
        (Environment.GetEnvironmentVariable("OLLAMA_HOST") is { Length: > 0 } host ? (host.Contains("://") ? host : "http://" + host) : "http://localhost:11434").TrimEnd('/') + "/v1";

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
        };
        // pi's levels are Codex's plus the ends of the scale; the catalog's word is passed as it is, and off means
        // the model's own default when nothing was asked for.
        arguments.Add("--thinking");
        arguments.Add(request.ReasoningEffort is { Length: > 0 } effort ? effort : "off");
        Dictionary<string, string>? environment = null;
        if (provider == "ollama")
        {
            var home = Path.Combine(request.ScratchDirectory, "pi-home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "models.json"), ScratchCatalog(id));
            environment = new Dictionary<string, string> { [AgentDirectoryVariable] = home };
        }
        return new HarnessInvocation("pi", arguments, request.Prompt, environment);
    }

    /// <summary>A catalog with one provider and one model: the run cannot drift onto anything the catalog did not name.</summary>
    internal static string ScratchCatalog(string modelId)
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
            json.WriteNumber("contextWindow", 32768);
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
