using System.Text;
using System.Text.Json;

namespace Muthur.Launch;

/// <summary>Claude Code in print mode. Permissions go through a settings file so no pattern ever needs shell quoting.</summary>
public sealed class ClaudeAdapter : IHarnessAdapter
{
    public string Name => "claude";

    public string? WorkerNote => null;

    public string? CapabilityExecutable => "claude";

    public string? CapabilitySettings(WorkerRequest request)
    {
        var inherited = Launch.CapabilitySettings.HashFiles([
            ("user", Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json")),
            ("project", Path.Combine(request.WorkingDirectory, ".claude", "settings.json")),
            ("project-local", Path.Combine(request.WorkingDirectory, ".claude", "settings.local.json"))]);
        return inherited is null ? null : CapabilityHash.Of("acceptEdits\n" + inherited + "\n" + SettingsJson(request));
    }

    public HarnessInvocation Build(WorkerRequest request)
    {
        var settings = Path.Combine(request.ScratchDirectory, "claude-settings.json");
        File.WriteAllText(settings, SettingsJson(request));

        var arguments = new List<string> { "--print", "--output-format", "json", "--permission-mode", "acceptEdits", "--settings", settings };
        if (request.Model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }
        if (request.ReasoningEffort is { Length: > 0 } effort)
        {
            arguments.Add("--effort");
            arguments.Add(effort);
        }
        return new HarnessInvocation("claude", arguments, request.Prompt);
    }

    public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : result.Message;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
            var session = root.TryGetProperty("session_id", out var s) ? s.GetString() : null;
            var failed = isError || !result.Ok;
            var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
            return new WorkerOutcome(!failed, text, failed && Harnesses.LooksRateLimited(text + result.StdErr), cost, session,
                Count(usage, "input_tokens"), Count(usage, "output_tokens"), Count(usage, "cache_read_input_tokens"));
        }
        catch (JsonException)
        {
            return new WorkerOutcome(false, result.Message, Harnesses.LooksRateLimited(result.StdOut + result.StdErr));
        }
    }

    private static int? Count(JsonElement? usage, string name) =>
        usage is { } u && u.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count) ? count : null;

    private static string SettingsJson(WorkerRequest request)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteBoolean("fastMode", false);
            json.WriteStartObject("permissions");
            json.WriteStartArray("allow");
            foreach (var command in request.AllowedCommands) json.WriteStringValue($"Bash({command})");
            json.WriteEndArray();
            json.WriteStartArray("deny");
            foreach (var command in request.DeniedCommands) json.WriteStringValue($"Bash({command})");
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
