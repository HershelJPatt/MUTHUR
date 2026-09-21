using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muthur.Launch;

public sealed record VerificationRequest(string Repository, string Ref, string Spec, string Recipe, string Output,
    string Cache, string? Task = null, string? ParentRun = null, string? Subject = null, int TimeoutMinutes = 60, bool NoCache = false);
public sealed record VerificationResult(Guid RunId, string Status, string? EvidencePath, string CacheStatus, int ExitCode, string? Error = null);
public sealed record VerificationCommand(string File, string[] Arguments);
public sealed record VerificationRecipe(int Version, string Name, string[] Inputs, string BuildConfiguration,
    string PreparationConfiguration, int TimeoutMinutes, VerificationCommand Preparation, VerificationCommand Build,
    VerificationCommand Test, VerificationCommand Up, string ReadinessPath, VerificationCommand Status,
    VerificationCommand Down, string[] Outputs);
public sealed record VerificationFile(string Path, long Size, string Sha256);
public sealed record VerificationProcess(int Pid, long StartUtcTicks, string Executable);
public sealed class VerificationStage
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public required string[] Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public string EnvironmentPolicy { get; init; } = "minimal-toolchain-allowlist-v1";
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; set; }
    public double DurationMilliseconds { get; set; }
    public string Status { get; set; } = "running";
    public int? ExitCode { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
}
public sealed class VerificationEvidence
{
    public int Version { get; init; } = 1;
    public required Guid RunId { get; init; }
    public string ReferenceAuthority { get; init; } = "caller-provided; not hub-verified approval";
    public string? Task { get; init; }
    public string? ParentRun { get; init; }
    public string? Subject { get; init; }
    public required string Repository { get; init; }
    public required string GitDirectory { get; init; }
    public required string Commit { get; init; }
    public required string Tree { get; init; }
    public required string SpecPath { get; init; }
    public required string SpecSha256 { get; init; }
    public required string RecipeSha256 { get; init; }
    public required VerificationRecipe Recipe { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; set; }
    public string Status { get; set; } = "running";
    public string CacheStatus { get; set; } = "miss";
    public string? CacheReason { get; set; }
    public Guid? CacheProducer { get; set; }
    public string? Key { get; set; }
    public SortedDictionary<string, string> Inputs { get; set; } = new(StringComparer.Ordinal);
    public List<VerificationStage> Stages { get; init; } = [];
    public bool TestsStarted { get; set; }
    public bool TestsCompleted { get; set; }
    public bool TestsExecuted => TestsStarted && TestsCompleted;
    public List<VerificationFile> OutputFiles { get; set; } = [];
    public bool CleanupSucceeded { get; set; }
    public string? CleanupError { get; set; }
    public string? Error { get; set; }
}
public sealed class VerificationOwnership
{
    public int Version { get; init; } = 1;
    public required Guid RunId { get; init; }
    public required string Output { get; init; }
    public required string Repository { get; init; }
    public required string Scratch { get; init; }
    public required string Checkout { get; init; }
    public required string Commit { get; init; }
    public required VerificationProcess Runner { get; init; }
    public required string Url { get; init; }
    public string? Containment { get; init; }
    public bool CommandRunning { get; set; }
    public VerificationProcess? ActiveCommand { get; set; }
    public VerificationProcess? Server { get; set; }
    public bool ServerLaunchStarted { get; set; }
    public bool Cleaned { get; set; }
}
public sealed record VerificationCacheManifest(int Version, string Key, SortedDictionary<string, string> Inputs,
    Guid RunId, string Commit, string SpecSha256, string RecipeSha256, DateTimeOffset CompletedUtc,
    string Status, List<VerificationFile> Files);
public sealed record VerificationCacheOwner(int Version, string Key, Guid RunId, int Pid, long StartUtcTicks);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(VerificationResult))]
[JsonSerializable(typeof(VerificationRecipe))]
[JsonSerializable(typeof(VerificationEvidence))]
[JsonSerializable(typeof(VerificationOwnership))]
[JsonSerializable(typeof(VerificationCacheManifest))]
[JsonSerializable(typeof(VerificationCacheOwner))]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
public partial class VerificationJsonContext : JsonSerializerContext;

public static class VerificationRecipes
{
    public static VerificationRecipe Muthur { get; } = new(1, "muthur", ["**"], "Debug", "Release", 60,
        new("pwsh", ["-NoProfile", "-File", "scripts/install.ps1", "-Destination", "{install}"]),
        new("dotnet", ["build"]), new("dotnet", ["test", "-v", "n"]), new("{install}/muthur.exe", ["up"]),
        "/", new("{install}/muthur.exe", ["status"]), new("{install}/muthur.exe", ["down"]),
        ["muthur.exe", "server/Muthur.Server.dll", "kit"]);

    public static VerificationRecipe Read(string project)
    {
        using var doc = JsonDocument.Parse(project);
        if (!doc.RootElement.TryGetProperty("verification", out var value))
            throw new ArgumentException("Committed project has no verification recipe.");
        var recipe = value.Deserialize(VerificationJsonContext.Default.VerificationRecipe)
            ?? throw new ArgumentException("Missing verification recipe.");
        if (recipe.TimeoutMinutes is < 1 or > 60 ||
            JsonSerializer.Serialize(recipe with { TimeoutMinutes = 60 }, VerificationJsonContext.Default.VerificationRecipe) !=
            JsonSerializer.Serialize(Muthur, VerificationJsonContext.Default.VerificationRecipe))
            throw new ArgumentException("Only the exact version 1 MUTHUR recipe is supported; timeout may only shorten 60 minutes.");
        return recipe;
    }
}
