using System.Text.Json;

namespace Muthur.Server.Services;

/// <summary>
/// A verdict's evidence, cut down to something a notification can carry. A founder's message says which validator
/// said what and roughly why; the evidence in full is one command away and belongs there, not in a message body.
/// </summary>
internal static class Evidence
{
    private const int Limit = 140;

    /// <summary>The first non-empty line of the evidence, without its markdown heading marks, capped.</summary>
    internal static string FirstLine(string? evidence)
    {
        var line = (evidence ?? "").ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim().TrimStart('#').Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(line)) return "(no evidence)";
        return line.Length <= Limit ? line : line[..(Limit - 1)].TrimEnd() + "…";
    }

    /// <summary>The same line, read out of a recorded verdict event's payload.</summary>
    internal static string FirstLineOfEvidence(string payloadJson)
    {
        string? evidence = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("evidence", out var value) && value.ValueKind == JsonValueKind.String)
                evidence = value.GetString();
        }
        catch (JsonException) { }

        return FirstLine(evidence);
    }
}
