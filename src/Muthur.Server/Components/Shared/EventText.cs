using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Server.Components.Shared;

/// <summary>Turns a ledger event into the single line the stream and a task's history show.</summary>
public static class EventText
{
    private const int MaxLength = 160;

    /// <summary>"key: value · key: value" of the payload's non-null scalar properties, in payload order.</summary>
    public static string Describe(EventDto e)
    {
        if (e.Payload.ValueKind != JsonValueKind.Object) return "";

        var pairs = new List<string>();
        foreach (var property in e.Payload.EnumerateObject())
        {
            if (Value(property.Value) is { Length: > 0 } value) pairs.Add($"{property.Name}: {value}");
        }

        var text = string.Join(" · ", pairs);
        return text.Length > MaxLength ? string.Concat(text.AsSpan(0, MaxLength - 1), "…") : text;
    }

    /// <summary>"fail" | "warn" | "" — tone of an event type.</summary>
    public static string Tone(string type) =>
        Ends(type, ".failed") || Ends(type, ".expired") || Ends(type, ".rejected") || type == "task.claim_expired" ? "fail"
        : Ends(type, ".limited") || Ends(type, ".blocked") || Ends(type, ".cancelled") || Ends(type, ".released") ? "warn"
        : "";

    private static bool Ends(string type, string suffix) => type.EndsWith(suffix, StringComparison.Ordinal);

    /// <summary>A property's rendering: an array becomes its scalar items, anything unusable becomes null.</summary>
    private static string? Value(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? string.Join(", ", value.EnumerateArray().Select(Scalar).Where(item => item is { Length: > 0 }))
        : Scalar(value);

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null,
    };
}
