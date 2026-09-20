using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muthur.Server.Services;

public sealed partial class CensusSource(MuthurOptions options, ICensusCommandRunner runner) : IInboundSource
{
    public string Scheme => "census";

    public async Task<SourceFetch> FetchAsync(string location, string? cursor, CancellationToken ct = default)
    {
        var check = Configuration(location);
        var (next, previous) = ReadCursor(cursor);
        Muthur.Launch.ProcessResult result;
        try
        {
            result = await runner.RunAsync(check.FileName, check.Arguments, check.WorkingDirectory,
                TimeSpan.FromSeconds(check.TimeoutSeconds), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            throw new InvalidOperationException("Census execution failed.");
        }
        if (!result.Ok)
            throw new InvalidOperationException($"Census command failed (exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).");

        var findings = ReadSnapshot(result.StdOut);
        var active = new Dictionary<string, long>(StringComparer.Ordinal);
        var items = new List<IncomingItem>();
        try
        {
            foreach (var finding in findings.OrderBy(f => f.Id, StringComparer.Ordinal))
            {
                if (!previous.TryGetValue(finding.Id, out var sequence))
                {
                    sequence = next;
                    next = checked(next + 1);
                }
                active.Add(finding.Id, sequence);
                items.Add(new IncomingItem($"{location}/{finding.Id}/{sequence.ToString(CultureInfo.InvariantCulture)}",
                    finding.Title, finding.Body, finding.Url, $"census:{location}"));
            }
        }
        catch (OverflowException) { throw new InvalidOperationException("Census incident sequence exhausted."); }
        return new SourceFetch(items, JsonSerializer.Serialize(new { version = 1, next, active }));
    }

    public Task ProbeAsync(string location, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var check = Configuration(location);
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException(CensusCommandRunner.PlatformError);
        try
        {
            if (!ExecutableExists(check.FileName)) throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Census executable unavailable.");
        }
        return Task.CompletedTask;
    }

    private CensusCheckOptions Configuration(string key)
    {
        // Do not rely on the dictionary's comparer: configuration binding can supply a different one.
        var check = options.CensusChecks?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal)).Value;
        if (!CheckKey().IsMatch(key) || check is null || string.IsNullOrWhiteSpace(check.FileName) ||
            string.IsNullOrWhiteSpace(check.WorkingDirectory) || !Path.IsPathFullyQualified(check.WorkingDirectory) ||
            !Directory.Exists(check.WorkingDirectory) || check.Arguments is null || check.Arguments.Any(a => a is null) ||
            check.TimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Invalid census configuration.");
        return check;
    }

    private static bool ExecutableExists(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName)) return File.Exists(fileName);
        if (fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) return false;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD").Split(';')
            : [];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate) || extensions.Any(e => File.Exists(candidate + e))) return true;
        }
        return false;
    }

    private static (long Next, Dictionary<string, long> Active) ReadCursor(string? cursor)
    {
        if (cursor is null) return (1, new Dictionary<string, long>(StringComparer.Ordinal));
        try
        {
            using var doc = JsonDocument.Parse(cursor);
            var fields = Fields(doc.RootElement, ["version", "next", "active"]);
            if (fields.Count != 3 || !fields["version"].TryGetInt32(out var version) || version != 1 ||
                !fields["next"].TryGetInt64(out var next) || next <= 0 || fields["active"].ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();
            var active = new Dictionary<string, long>(StringComparer.Ordinal);
            var sequences = new HashSet<long>();
            foreach (var property in fields["active"].EnumerateObject())
            {
                if (!FindingId().IsMatch(property.Name) || !property.Value.TryGetInt64(out var sequence) ||
                    sequence <= 0 || sequence >= next || !sequences.Add(sequence) || !active.TryAdd(property.Name, sequence))
                    throw new InvalidOperationException();
            }
            if (active.Count > 1000) throw new InvalidOperationException();
            return (next, active);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Invalid census cursor.");
        }
    }

    private sealed record Finding(string Id, string Title, string Body, string? Url);

    private static List<Finding> ReadSnapshot(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > 1000)
                throw new InvalidOperationException();
            var findings = new List<Finding>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var fields = Fields(element, ["id", "title", "body", "url"]);
                var id = Text(fields["id"], 128);
                var title = Text(fields["title"], 500);
                var body = fields.TryGetValue("body", out var b) ? Text(b, 16000) : "";
                var url = fields.TryGetValue("url", out var u) ? Text(u, 2048) : null;
                if (!FindingId().IsMatch(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(title) ||
                    (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")))
                    throw new InvalidOperationException();
                findings.Add(new Finding(id, title, body, url));
            }
            return findings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Invalid census snapshot.");
        }
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.TryAdd(property.Name, property.Value))
                throw new InvalidOperationException();
        return fields;
    }

    private static string Text(JsonElement value, int limit)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > limit) throw new InvalidOperationException();
        return value.GetString()!;
    }

    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CheckKey();

    [GeneratedRegex("\\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FindingId();
}
