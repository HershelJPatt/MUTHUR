using System.Text.Json;

namespace Muthur.Launch;

public sealed class CapabilityStore(string directory, TimeProvider? timeProvider = null)
{
    public const int MaximumBytes = 131_072;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public string PathFor(CapabilityIdentity identity) => Path.Combine(directory, CapabilityHash.Identity(identity) + ".json");

    public (CapabilityObservation[] Observations, string? Diagnostic) Read(CapabilityIdentity identity)
    {
        var result = ReadFile(PathFor(identity));
        return result.Observations.Any(o => o.Identity != identity)
            ? ([], "Cache identity does not match; no observations reused.") : result;
    }

    public (CapabilityObservation[] Observations, string? Diagnostic) ReadFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > MaximumBytes) return ([], "Capability cache exceeds the read limit.");
            var bytes = new byte[MaximumBytes + 1];
            var count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > MaximumBytes) return ([], "Capability cache exceeds the read limit.");
            var observations = JsonSerializer.Deserialize(bytes.AsSpan(0, count), CapabilityJsonContext.Default.CapabilityObservationArray);
            if (observations is null || observations.Length > 128 || observations.Any(o => !Valid(o)) ||
                observations.Select(o => o.Capability).Distinct(StringComparer.Ordinal).Count() != observations.Length)
                return ([], "Malformed capability cache; explicit re-probe required.");
            return (observations, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return ([], "Capability cache missing, unreadable or malformed; use capability inspect/probe.");
        }
    }

    private static bool Valid(CapabilityObservation? o) => o is not null && o.Identity is not null &&
        !string.IsNullOrWhiteSpace(o.Identity.Machine) && !string.IsNullOrWhiteSpace(o.Identity.Harness) &&
        o.Identity.HarnessVersion is { Length: > 0 and <= 256 } &&
        o.Identity.ConfigurationHash is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit) &&
        o.Identity.RepositoryRoot is { Length: > 0 } root && Path.IsPathFullyQualified(root) &&
        o.Identity.BaseCommit is { Length: 40 } basis && basis.All(char.IsAsciiHexDigit) &&
        o.Identity.LaunchPath is "worker-run" or "conductor-validator" or "conductor-orchestrator" or "native-subagent" &&
        CapabilityRequirements.IsKey(o.Capability ?? "") && CapabilityStates.IsValid(o.State) &&
        o.Evidence is { Length: <= 2048 } && o.DurationMilliseconds >= 0 && o.ExpiresAt >= o.ObservedAt &&
        o.ExpiresAt - o.ObservedAt <= (o.State == CapabilityStates.TemporarilyFailing ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24));

    public void Write(CapabilityIdentity identity, IReadOnlyList<CapabilityObservation> observations)
    {
        if (observations.Any(o => !Valid(o) || o.Identity != identity) || observations.Count > 128 ||
            observations.Select(o => o.Capability).Distinct(StringComparer.Ordinal).Count() != observations.Count)
            throw new ArgumentException("Invalid capability observations.", nameof(observations));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(observations.ToArray(), CapabilityJsonContext.Default.CapabilityObservationArray);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Capability observations exceed the cache limit.", nameof(observations));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, PathFor(identity), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public CapabilityMatch Match(CapabilityIdentity identity, IReadOnlyList<string> requirements,
        IReadOnlyList<CapabilityObservation> observations, string? diagnostic = null)
    {
        var missing = new List<CapabilityMissing>();
        foreach (var requirement in requirements)
        {
            var observation = observations.SingleOrDefault(o => o.Capability == requirement && o.Identity == identity);
            var state = observation?.State ?? CapabilityStates.Unknown;
            var reason = observation?.Evidence ?? diagnostic ?? "No observation for this exact identity.";
            if (observation is not null && (observation.ExpiresAt <= _clock.GetUtcNow() || observation.ObservedAt > _clock.GetUtcNow()))
            {
                state = CapabilityStates.Stale;
                reason = "Observation expired or has a future timestamp; explicit authorized re-probe required.";
            }
            if (requirement == "native-agent-tools" && identity.LaunchPath != "native-subagent")
            {
                state = CapabilityStates.Unavailable;
                reason = "Requires native-subagent evidence (T-67/T-97).";
            }
            if (state != CapabilityStates.Available) missing.Add(new(requirement, state, reason));
        }
        return new(missing.Count == 0, missing);
    }
}
