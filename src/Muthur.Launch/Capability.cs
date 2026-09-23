using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muthur.Launch;

public sealed record CapabilityIdentity(string Machine, string Harness, string HarnessVersion, string LaunchPath,
    string ConfigurationHash, string RepositoryRoot, string BaseCommit);

public sealed record CapabilityObservation(CapabilityIdentity Identity, string Capability, string State,
    DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, string Evidence, long DurationMilliseconds);

public sealed record CapabilityMissing(string Capability, string State, string Reason);

public sealed record CapabilityMatch(bool Allowed, IReadOnlyList<CapabilityMissing> Missing,
    string? AuthorizedAlternative = null);

public sealed record CapabilityContext(IReadOnlyList<string> Requirements, string LaunchPath, string CacheDirectory,
    string BaseCommit, string RepositoryRoot);

public sealed record CapabilityInspection(CapabilityIdentity? Identity, IReadOnlyList<string> Requirements,
    IReadOnlyList<CapabilityObservation> Observations, CapabilityMatch Match, string? Diagnostic);

public static class CapabilityRequirements
{
    private static readonly string[] Keys = ["shell", "build", "test", "worktree-base", "commit", "deny-list",
        "headless-interaction", "connector-interaction", "native-agent-tools"];

    public static bool IsKey(string key) => Keys.Contains(key, StringComparer.Ordinal) ||
        key.StartsWith("platform:", StringComparison.Ordinal) && key.Length > 9 &&
        key[9..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static IReadOnlyList<string> Parse(string markdown)
    {
        if (markdown.Length > 1_048_576)
            throw new WorkerDispatchException("invalid_capabilities", "The frozen spec exceeds the 1 MiB limit.");
        var requirements = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in markdown.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("capabilities:", StringComparison.Ordinal)) continue;
            foreach (var key in text[13..].Split(',').Select(k => k.Trim()))
            {
                if (!IsKey(key))
                    throw new WorkerDispatchException("invalid_capabilities",
                        $"Unknown or malformed capability '{key}'. Use comma-separated shell, build, test, worktree-base, commit, deny-list, headless-interaction, connector-interaction, native-agent-tools or platform:<name>.");
                requirements.Add(key);
            }
        }
        return requirements.ToArray();
    }
}

public static class CapabilityStates
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
    public const string Unknown = "unknown";
    public const string Stale = "stale";
    public const string TemporarilyFailing = "temporarily-failing";

    public static bool IsValid(string state) => state is Available or Unavailable or Unknown or Stale or TemporarilyFailing;
}

public static class CapabilityHash
{
    public static string Of(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Identity(CapabilityIdentity identity) =>
        Of(JsonSerializer.Serialize(identity, CapabilityJsonContext.Default.CapabilityIdentity));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityIdentity))]
[JsonSerializable(typeof(CapabilityObservation[]))]
[JsonSerializable(typeof(CapabilityInspection))]
[JsonSerializable(typeof(CapabilityMatch))]
[JsonSerializable(typeof(CapabilityProbeResult))]
[JsonSerializable(typeof(IReadOnlyList<CapabilityProbeStep>))]
public partial class CapabilityJsonContext : JsonSerializerContext;
