using System.Text.Json;
using System.Diagnostics;

namespace Muthur.Launch;

public sealed record VerificationCacheRead(string Status, string? Reason, VerificationCacheManifest? Manifest);

public sealed class VerificationCache(string root, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly string root = VerificationFiles.PlainPath(root);

    public static string Key(SortedDictionary<string, string> inputs) => VerificationFiles.Hash(
        JsonSerializer.Serialize(inputs, VerificationJsonContext.Default.SortedDictionaryStringString));

    private string Entry(string key)
    {
        if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid cache key.");
        return VerificationFiles.PlainPath(Path.Combine(root, key));
    }

    public async Task<FileStream> LockAsync(string key, CancellationToken ct)
    {
        _ = Entry(key);
        Directory.CreateDirectory(root);
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(VerificationFiles.PlainPath(Path.Combine(root, key + ".lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (clock.GetUtcNow() < deadline)
            { await Task.Delay(TimeSpan.FromMilliseconds(100), clock, ct); }
        }
    }

    public async Task<VerificationCacheRead> RestoreAsync(string key, SortedDictionary<string, string> inputs, string install, CancellationToken ct)
    {
        using var gate = await LockAsync(key, ct);
        RemoveAbandonedStages(key);
        var entry = Entry(key);
        if (!Directory.Exists(entry)) return new("miss", "No completed entry.", null);
        try
        {
            var manifest = Read(entry, key, inputs, ct);
            VerificationFiles.Copy(Path.Combine(entry, "install"), install, manifest.Files, ct);
            return new("hit", null, manifest);
        }
        catch (OperationCanceledException)
        {
            if (Directory.Exists(install)) VerificationFiles.DeleteOwned(install, Path.GetDirectoryName(install)!);
            throw;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            VerificationFiles.DeleteOwned(entry, root);
            if (Directory.Exists(install)) VerificationFiles.DeleteOwned(install, Path.GetDirectoryName(install)!);
            return new("rejected", ex.Message, null);
        }
    }

    private VerificationCacheManifest Read(string entry, string key, SortedDictionary<string, string>? inputs = null, CancellationToken ct = default)
    {
        VerificationFiles.PlainPath(entry);
        var manifestPath = VerificationFiles.PlainPath(Path.Combine(entry, "manifest.json"));
        var digestPath = VerificationFiles.PlainPath(Path.Combine(entry, "manifest.sha256"));
        if (File.ReadAllText(digestPath) != VerificationFiles.HashFile(manifestPath, ct)) throw new IOException("Cache manifest digest mismatch.");
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), VerificationJsonContext.Default.VerificationCacheManifest)
            ?? throw new IOException("Cache manifest missing.");
        if (manifest.Version != 1 || manifest.Status != "success" || manifest.Key != key || manifest.Inputs is null || Key(manifest.Inputs) != key ||
            (inputs is not null && !manifest.Inputs.SequenceEqual(inputs))) throw new IOException("Cache manifest identity mismatch.");
        if (manifest.RunId == Guid.Empty || !Digest(manifest.Commit, 40) || !Digest(manifest.SpecSha256, 64) || !Digest(manifest.RecipeSha256, 64) ||
            manifest.Inputs.GetValueOrDefault("commit") != manifest.Commit || manifest.Inputs.GetValueOrDefault("spec") != manifest.SpecSha256 ||
            manifest.Inputs.GetValueOrDefault("recipe") != manifest.RecipeSha256 ||
            manifest.CompletedUtc <= DateTimeOffset.UnixEpoch || manifest.CompletedUtc > clock.GetUtcNow())
            throw new IOException("Cache producer provenance is incomplete or mismatched.");
        var owner = JsonSerializer.Deserialize(File.ReadAllText(VerificationFiles.PlainPath(Path.Combine(entry, "owner"))), VerificationJsonContext.Default.VerificationCacheOwner);
        if (owner is null || owner.Version != 1 || owner.RunId != manifest.RunId || owner.Key != key || owner.Pid <= 0 || owner.StartUtcTicks <= 0)
            throw new IOException("Cache producer ownership mismatch.");
        string[] expected = ["install", "manifest.json", "manifest.sha256", "owner"];
        if (!Directory.EnumerateFileSystemEntries(entry).Select(Path.GetFileName).Order().SequenceEqual(expected))
            throw new IOException("Unexpected cache entry files.");
        if (!VerificationFiles.Inventory(Path.Combine(entry, "install"), ct).SequenceEqual(manifest.Files)) throw new IOException("Cache installation inventory mismatch.");
        return manifest;
    }

    private static bool Digest(string value, int length) => value is not null && value.Length == length && value.All(char.IsAsciiHexDigit);

    // Staging is private to a live run; its owner is persisted before copying any executable bytes.
    public async Task<string> StageAsync(VerificationEvidence evidence, string install, CancellationToken ct)
    {
        var key = evidence.Key ?? throw new ArgumentException("No complete cache identity.");
        using var gate = await LockAsync(key, ct);
        RemoveAbandonedStages(key);
        var stage = VerificationFiles.PlainPath(Path.Combine(root, key + ".stage-" + evidence.RunId.ToString("N")));
        using var current = Process.GetCurrentProcess();
        var owner = new VerificationCacheOwner(1, key, evidence.RunId, current.Id, current.StartTime.ToUniversalTime().Ticks);
        if (Directory.Exists(stage) || File.Exists(stage + ".owner")) throw new IOException("Staging identity already exists.");
        VerificationFiles.Atomic(stage + ".owner", owner, VerificationJsonContext.Default.VerificationCacheOwner);
        try
        {
            Directory.CreateDirectory(stage);
            VerificationFiles.Atomic(Path.Combine(stage, "owner"), owner, VerificationJsonContext.Default.VerificationCacheOwner);
            var manifest = new VerificationCacheManifest(1, key, evidence.Inputs, evidence.RunId, evidence.Commit,
                evidence.SpecSha256, evidence.RecipeSha256, clock.GetUtcNow(), "success", VerificationFiles.Inventory(install, ct));
            VerificationFiles.Copy(install, Path.Combine(stage, "install"), manifest.Files, ct);
            VerificationFiles.Atomic(Path.Combine(stage, "manifest.json"), manifest, VerificationJsonContext.Default.VerificationCacheManifest);
            File.WriteAllText(Path.Combine(stage, "manifest.sha256"), VerificationFiles.HashFile(Path.Combine(stage, "manifest.json")));
            _ = Read(stage, key, evidence.Inputs, ct);
            ct.ThrowIfCancellationRequested();
            return stage;
        }
        catch { Discard(stage, evidence.RunId); throw; }
    }

    public async Task PublishAsync(string stage, VerificationEvidence evidence, CancellationToken ct)
    {
        if (!evidence.CleanupSucceeded || evidence.Status != "success") throw new InvalidOperationException("Only a fully successful, cleaned run may publish.");
        var key = evidence.Key!;
        using (await LockAsync(key, ct))
        {
            var expected = Path.Combine(root, key + ".stage-" + evidence.RunId.ToString("N"));
            var owner = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(stage, "owner")), VerificationJsonContext.Default.VerificationCacheOwner);
            if (!string.Equals(stage, expected, StringComparison.OrdinalIgnoreCase) || owner?.RunId != evidence.RunId || owner.Key != key || owner.Version != 1)
                throw new IOException("Staging ownership mismatch.");
            var completed = Read(stage, key, evidence.Inputs, ct) with { CompletedUtc = clock.GetUtcNow() };
            VerificationFiles.Atomic(Path.Combine(stage, "manifest.json"), completed, VerificationJsonContext.Default.VerificationCacheManifest);
            File.WriteAllText(Path.Combine(stage, "manifest.sha256"), VerificationFiles.HashFile(Path.Combine(stage, "manifest.json")));
            var destination = Entry(key);
            if (Directory.Exists(destination))
            {
                _ = Read(destination, key, evidence.Inputs, ct);
                VerificationFiles.DeleteOwned(stage, root);
            }
            else
            {
                Evict(ct);
                ct.ThrowIfCancellationRequested();
                Directory.Move(stage, destination);
            }
        }
        // Rename is the final commit point. Keep the sidecar until dead-owner recovery; no fallible
        // maintenance follows publication, and readers accept the complete directory immediately.
    }

    public void Discard(string stage, Guid runId)
    {
        VerificationFiles.PlainPath(stage);
        if (!VerificationFiles.Within(stage, root) || !stage.EndsWith(".stage-" + runId.ToString("N"), StringComparison.Ordinal))
            throw new IOException("Staging ownership mismatch.");
        var ownerPath = VerificationFiles.PlainPath(stage + ".owner");
        if (!Directory.Exists(stage) && !File.Exists(ownerPath)) return;
        var owner = JsonSerializer.Deserialize(File.ReadAllText(ownerPath), VerificationJsonContext.Default.VerificationCacheOwner);
        if (owner?.Version != 1 || owner.RunId != runId || Path.GetFileName(stage) != owner.Key + ".stage-" + runId.ToString("N"))
            throw new IOException("Staging sidecar ownership mismatch.");
        VerificationFiles.DeleteOwned(stage, root);
        File.Delete(ownerPath);
    }

    public async Task DiscardAsync(string stage, VerificationEvidence evidence, CancellationToken ct)
    {
        using var gate = await LockAsync(evidence.Key!, ct);
        ct.ThrowIfCancellationRequested();
        Discard(stage, evidence.RunId);
    }

    public async Task RevokeAsync(VerificationEvidence evidence, CancellationToken ct)
    {
        using var gate = await LockAsync(evidence.Key!, ct);
        var entry = Entry(evidence.Key!);
        if (Directory.Exists(entry) && Read(entry, evidence.Key!, evidence.Inputs, ct).RunId == evidence.RunId)
            VerificationFiles.DeleteOwned(entry, root, ct);
    }

    private void RemoveAbandonedStages(string key)
    {
        foreach (var sidecar in Directory.EnumerateFiles(root, key + ".stage-*.owner"))
        {
            var stage = sidecar[..^6];
            VerificationFiles.PlainPath(stage);
            var ownerPath = VerificationFiles.PlainPath(sidecar);
            if (!File.Exists(ownerPath)) continue; // Unknown ownership is never guessed.
            VerificationCacheOwner? owner;
            try { owner = JsonSerializer.Deserialize(File.ReadAllText(ownerPath), VerificationJsonContext.Default.VerificationCacheOwner); }
            catch (JsonException) { continue; }
            if (owner is null || owner.Version != 1 || owner.Key != key || owner.RunId == Guid.Empty ||
                Path.GetFileName(stage) != key + ".stage-" + owner.RunId.ToString("N") || owner.Pid <= 0 || owner.StartUtcTicks <= 0) continue;
            try
            {
                using var process = Process.GetProcessById(owner.Pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.StartUtcTicks) continue;
            }
            catch (ArgumentException) { }
            VerificationFiles.DeleteOwned(stage, root);
            File.Delete(ownerPath);
        }
    }

    private void Evict(CancellationToken ct)
    {
        var entries = new List<(string Path, DateTimeOffset Completed)>();
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var key = Path.GetFileName(path);
            if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) continue;
            try
            {
                using var gate = new FileStream(VerificationFiles.PlainPath(Path.Combine(root, key + ".lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                entries.Add((path, Read(path, key).CompletedUtc));
            }
            catch (IOException) { }
        }
        foreach (var entry in entries.OrderByDescending(e => e.Completed).Skip(2))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var gate = new FileStream(Path.Combine(root, Path.GetFileName(entry.Path) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (Read(entry.Path, Path.GetFileName(entry.Path), ct: ct).CompletedUtc == entry.Completed)
                    VerificationFiles.DeleteOwned(entry.Path, root, ct);
            }
            catch (IOException) { }
        }
    }
}
