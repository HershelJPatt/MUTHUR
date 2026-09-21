using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Muthur.Launch;

namespace Muthur.Launch.Tests;

public sealed class VerificationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "muthur-cli-tests", Guid.NewGuid().ToString("N"));
    public VerificationTests() => Directory.CreateDirectory(root);
    public void Dispose() => VerificationFiles.DeleteOwned(root, Path.GetDirectoryName(root)!);

    [Fact]
    public async Task Committed_spec_archive_preserves_exact_bytes_and_digest()
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "specs"));
        var bytes = Encoding.UTF8.GetBytes("\ufeff# Spécification 日本語\r\nfirst\nlast\r\n\n");
        var spec = Path.Combine(repo, "specs", "T-107.md");
        File.WriteAllBytes(spec, bytes);
        File.WriteAllText(Path.Combine(repo, "muthur.project.json"), "{\"verification\":" +
            JsonSerializer.Serialize(VerificationRecipes.Muthur, VerificationJsonContext.Default.VerificationRecipe) + "}");
        var processes = new ProcessRunner();
        foreach (var args in new[] {
            new[] { "init", "--quiet" },
            new[] { "-c", "core.autocrlf=false", "add", "." },
            new[] { "-c", "user.name=Verification Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture" } })
        {
            var command = await processes.RunAsync("git", args, repo);
            Assert.True(command.Ok, command.Message);
        }
        var committed = await processes.RunAsync("git", ["rev-parse", "HEAD:specs/T-107.md"], repo);
        Assert.True(committed.Ok, committed.Message);
        var blob = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0").Concat(bytes).ToArray();
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(blob)), committed.StdOut.Trim());
        File.WriteAllText(spec, "uncommitted replacement");
        var output = Path.Combine(root, "output");
        var runner = new VerificationRunner(new GitFixtureRunner(), inputProbe: (_, _) => Task.CompletedTask);
        var result = await runner.RunAsync(new(repo, "HEAD", "specs/T-107.md", "muthur", output, Path.Combine(root, "cache")));
        Assert.Equal(1, result.ExitCode); // Stop at the fake build after capturing the real committed input.
        var evidence = JsonSerializer.Deserialize(File.ReadAllText(result.EvidencePath!), VerificationJsonContext.Default.VerificationEvidence)!;
        Assert.Contains(evidence.Stages, stage => stage.Name == "build" && stage.Status == "failed");
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, "spec.bin")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), evidence.SpecSha256);
        Assert.NotEqual(VerificationFiles.Hash(""), evidence.SpecSha256);
        Assert.False(File.Exists(Path.Combine(output, "spec.zip")));
        Assert.True(evidence.CleanupSucceeded);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("directory")]
    [InlineData("symlink")]
    public async Task Invalid_spec_archive_is_refused_without_extracting_paths(string defect)
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        var output = Path.Combine(root, "output");
        var result = await new VerificationRunner(new RecipeRunner(repo, "archive-" + defect)).RunAsync(
            new(repo, "HEAD", "specs/T-107.md", "muthur", output, Path.Combine(root, "cache")));
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("exactly one matching regular file", result.Error);
        Assert.False(File.Exists(Path.Combine(output, "spec.bin")));
        Assert.False(File.Exists(Path.Combine(output, "spec.zip")));
        Assert.False(File.Exists(Path.Combine(root, "escape")));
    }

    [Fact]
    public void Owned_nested_read_only_git_objects_are_deleted()
    {
        var owned = Path.Combine(root, "owned");
        var objects = Path.Combine(owned, "nested", ".git", "objects", "ab");
        Directory.CreateDirectory(objects);
        var file = Path.Combine(objects, "cdef");
        File.WriteAllText(file, "git object fixture");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly | FileAttributes.Archive);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => VerificationFiles.DeleteOwned(owned, root, cancel.Token));
        Assert.True((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0);
        VerificationFiles.DeleteOwned(owned, root);
        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void Processor_architecture_is_inherited_and_hashed_as_toolchain_identity()
    {
        Assert.Contains("PROCESSOR_ARCHITECTURE", VerificationEnvironment.Allowed);
        var environment = VerificationEnvironment.Create(root, "http://127.0.0.1:12345");
        var inherited = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
        Assert.Equal(string.IsNullOrEmpty(inherited) ? null : inherited, environment.GetValueOrDefault("PROCESSOR_ARCHITECTURE"));
        var identity = VerificationEnvironment.Identity(environment);
        Assert.Equal(VerificationFiles.Hash(inherited is { Length: > 0 } ? inherited : "<absent>"), identity["environment/PROCESSOR_ARCHITECTURE"]);
        environment["PROCESSOR_ARCHITECTURE"] = "different-architecture";
        Assert.NotEqual(VerificationCache.Key(identity), VerificationCache.Key(VerificationEnvironment.Identity(environment)));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    [InlineData("C:relative")]
    [InlineData("a//b")]
    [InlineData("a./file")]
    public void Configured_paths_refuse_traversal_and_aliases(string path) => Assert.Throws<ArgumentException>(() => VerificationFiles.Relative(path));

    [Fact]
    public void Path_boundaries_are_directory_boundaries()
    {
        Assert.True(VerificationFiles.Within(Path.Combine(root, "a", "b"), Path.Combine(root, "a")));
        Assert.False(VerificationFiles.Intersects(Path.Combine(root, "a"), Path.Combine(root, "ab")));
        Assert.Throws<IOException>(() => VerificationFiles.DeleteOwned(root, Path.Combine(root, "child")));
    }

    [Fact]
    public void Recipe_requires_exact_commands_and_all_fields()
    {
        var recipe = JsonSerializer.Serialize(VerificationRecipes.Muthur, VerificationJsonContext.Default.VerificationRecipe);
        Assert.Equal("muthur", VerificationRecipes.Read("{\"verification\":" + recipe + "}").Name);
        Assert.Throws<ArgumentException>(() => VerificationRecipes.Read("{}"));
        Assert.Throws<ArgumentException>(() => VerificationRecipes.Read("{\"verification\":" + recipe.Replace("dotnet", "other") + "}"));
        Assert.Throws<ArgumentException>(() => VerificationRecipes.Read("{\"verification\":" + recipe.Replace("\"version\": 1", "\"version\": 2") + "}"));
        Assert.Throws<JsonException>(() => VerificationRecipes.Read("{\"verification\":{" + recipe[1..^1] + ",\"extra\":true}}"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("spec")]
    [InlineData("recipe")]
    [InlineData("toolchain")]
    public void Every_declared_input_change_misses(string changed)
    {
        var a = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["source"] = "a", ["spec"] = "b", ["recipe"] = "c", ["toolchain"] = "d" };
        var before = VerificationCache.Key(a);
        a[changed] += "changed";
        Assert.NotEqual(before, VerificationCache.Key(a));
    }

    [Fact]
    public void Environment_has_only_allowed_inputs_and_owned_mutable_homes()
    {
        var environment = VerificationEnvironment.Create(root, "http://127.0.0.1:12345");
        Assert.All(environment.Keys, key => Assert.True(VerificationEnvironment.Allowed.Contains(key) ||
            new[] { "HOME", "USERPROFILE", "DOTNET_CLI_HOME", "TEMP", "TMP", "MUTHUR_HOME", "MUTHUR_URL", "APPDATA", "LOCALAPPDATA" }.Contains(key)));
        Assert.DoesNotContain("MUTHUR_TOKEN", environment.Keys);
        Assert.DoesNotContain("MUTHUR_AGENT", environment.Keys);
        foreach (var variable in new[] { "HOME", "USERPROFILE", "DOTNET_CLI_HOME", "TEMP", "TMP", "MUTHUR_HOME", "APPDATA", "LOCALAPPDATA" })
            Assert.True(VerificationFiles.Within(environment[variable], root));
        Assert.Equal("http://127.0.0.1:12345", environment["MUTHUR_URL"]);
    }

    [Fact]
    public void Run_owned_paths_have_stable_identity_but_inherited_toolchain_changes_miss()
    {
        var first = VerificationEnvironment.Create(Path.Combine(root, "first"), "http://127.0.0.1:1");
        var second = VerificationEnvironment.Create(Path.Combine(root, "second"), "http://127.0.0.1:2");
        Assert.Equal(VerificationCache.Key(VerificationEnvironment.Identity(first)), VerificationCache.Key(VerificationEnvironment.Identity(second)));
        second["VCToolsVersion"] = "changed";
        Assert.NotEqual(VerificationCache.Key(VerificationEnvironment.Identity(first)), VerificationCache.Key(VerificationEnvironment.Identity(second)));
    }

    [Theory]
    [InlineData("hub.db")]
    [InlineData("founder.token")]
    [InlineData("private.pem")]
    [InlineData("muthur.log")]
    public void Mutable_or_credential_files_are_never_cached(string name)
    {
        File.WriteAllText(Path.Combine(root, name), "fixture");
        Assert.Throws<IOException>(() => VerificationFiles.Inventory(root));
    }

    [Fact]
    public async Task Cache_round_trip_copies_and_reverifies_without_executing_in_cache()
    {
        var evidence = Evidence();
        var cache = await Publish(evidence);
        var destination = Path.Combine(root, "restored");
        var read = await cache.RestoreAsync(evidence.Key!, evidence.Inputs, destination, default);
        Assert.Equal("hit", read.Status);
        Assert.Equal(evidence.RunId, read.Manifest!.RunId);
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(destination, "muthur.exe")));
        File.WriteAllText(Path.Combine(destination, "muthur.exe"), "changed only in run");
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(root, "cache", evidence.Key!, "install", "muthur.exe")));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("manifest")]
    [InlineData("schema")]
    public async Task Invalid_entries_are_rejected_and_cannot_be_executed(string corruption)
    {
        var evidence = Evidence();
        var cache = await Publish(evidence);
        var entry = Path.Combine(root, "cache", evidence.Key!);
        var artifact = Path.Combine(entry, "install", "muthur.exe");
        switch (corruption)
        {
            case "tampered": File.WriteAllText(artifact, "evil"); break;
            case "extra": File.WriteAllText(Path.Combine(entry, "install", "extra"), "extra"); break;
            case "missing": File.Delete(artifact); break;
            case "manifest": File.AppendAllText(Path.Combine(entry, "manifest.json"), " "); break;
            case "schema":
                var path = Path.Combine(entry, "manifest.json");
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"version\": 1", "\"version\": 2"));
                File.WriteAllText(Path.Combine(entry, "manifest.sha256"), VerificationFiles.HashFile(path));
                break;
        }
        var destination = Path.Combine(root, "restored");
        var result = await cache.RestoreAsync(evidence.Key!, evidence.Inputs, destination, default);
        Assert.Equal("rejected", result.Status);
        Assert.NotNull(result.Reason);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "failed")]
    [InlineData(true, "running")]
    public async Task Failed_or_unclean_runs_cannot_publish(bool cleaned, string status)
    {
        var evidence = Evidence();
        var install = Install();
        var cache = new VerificationCache(Path.Combine(root, "cache"));
        var stage = await cache.StageAsync(evidence, install, default);
        evidence.CleanupSucceeded = cleaned;
        evidence.Status = status;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.PublishAsync(stage, evidence, default));
        Assert.False(Directory.Exists(Path.Combine(root, "cache", evidence.Key!)));
    }

    [Fact]
    public async Task Os_lock_is_exclusive_and_released_on_dispose()
    {
        var evidence = Evidence();
        var cache = new VerificationCache(Path.Combine(root, "cache"));
        using (await cache.LockAsync(evidence.Key!, default))
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(root, "cache", evidence.Key + ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        using var released = await cache.LockAsync(evidence.Key!, default);
        Assert.NotNull(released);
    }

    [Fact]
    public async Task Lock_cancellation_is_bounded_and_failed_staging_leaves_no_owned_stage()
    {
        var evidence = Evidence();
        var cache = new VerificationCache(Path.Combine(root, "cache"));
        using (await cache.LockAsync(evidence.Key!, default))
        {
            using var cancel = new CancellationTokenSource();
            var waiting = cache.LockAsync(evidence.Key!, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        var install = Install();
        File.WriteAllText(Path.Combine(install, "hub.db"), "forbidden");
        await Assert.ThrowsAsync<IOException>(() => cache.StageAsync(evidence, install, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "cache"), "*.stage-*"));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("commit")]
    [InlineData("spec")]
    [InlineData("recipe")]
    [InlineData("time")]
    public async Task Recomputed_checksum_does_not_hide_invalid_producer_provenance(string field)
    {
        var evidence = Evidence();
        var cache = await Publish(evidence);
        var entry = Path.Combine(root, "cache", evidence.Key!);
        var path = Path.Combine(entry, "manifest.json");
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), VerificationJsonContext.Default.VerificationCacheManifest)!;
        manifest = field switch
        {
            "run" => manifest with { RunId = Guid.Empty },
            "commit" => manifest with { Commit = new string('f', 40) },
            "spec" => manifest with { SpecSha256 = new string('f', 64) },
            "recipe" => manifest with { RecipeSha256 = new string('f', 64) },
            _ => manifest with { CompletedUtc = default },
        };
        VerificationFiles.Atomic(path, manifest, VerificationJsonContext.Default.VerificationCacheManifest);
        File.WriteAllText(Path.Combine(entry, "manifest.sha256"), VerificationFiles.HashFile(path));
        Assert.Equal("rejected", (await cache.RestoreAsync(evidence.Key!, evidence.Inputs, Path.Combine(root, "restored"), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dead_staging_owner_recovers_before_and_after_directory_creation(bool directoryCreated)
    {
        var evidence = Evidence();
        var cacheRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(cacheRoot);
        var stage = Path.Combine(cacheRoot, evidence.Key + ".stage-" + evidence.RunId.ToString("N"));
        VerificationFiles.Atomic(stage + ".owner", new VerificationCacheOwner(1, evidence.Key!, evidence.RunId, int.MaxValue, 1), VerificationJsonContext.Default.VerificationCacheOwner);
        if (directoryCreated) Directory.CreateDirectory(stage);
        var cache = new VerificationCache(cacheRoot);
        Assert.Equal("miss", (await cache.RestoreAsync(evidence.Key!, evidence.Inputs, Path.Combine(root, "restored"), default)).Status);
        Assert.False(Directory.Exists(stage));
        Assert.False(File.Exists(stage + ".owner"));
    }

    [Fact]
    public async Task Concurrent_publishers_keep_one_complete_entry()
    {
        var evidence = Evidence();
        var other = Evidence();
        var install = Install();
        var cache = new VerificationCache(Path.Combine(root, "cache"));
        var first = await cache.StageAsync(evidence, install, default);
        var second = await cache.StageAsync(other, install, default);
        await Task.WhenAll(cache.PublishAsync(first, evidence, default), cache.PublishAsync(second, other, default));
        var read = await cache.RestoreAsync(evidence.Key!, evidence.Inputs, Path.Combine(root, "restored"), default);
        Assert.Equal("hit", read.Status);
        Assert.True(read.Manifest!.RunId == evidence.RunId || read.Manifest.RunId == other.RunId);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "cache"), "*.stage-*"));
    }

    [Fact]
    public async Task Eviction_retains_three_completed_entries()
    {
        for (var i = 0; i < 4; i++) await Publish(Evidence(i.ToString()));
        Assert.Equal(3, Directory.EnumerateDirectories(Path.Combine(root, "cache")).Count());
    }

    [Fact]
    public async Task Invalid_input_does_not_start_any_process()
    {
        var runner = new NeverRunner();
        var result = await new VerificationRunner(runner).RunAsync(new(root, "HEAD", "specs/T-107.md", "unknown", Path.Combine(root, "out"), Path.Combine(root, "cache")));
        Assert.Equal(2, result.ExitCode);
        Assert.NotEqual(Guid.Empty, result.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_launch_failure_or_prelaunch_cancellation_never_reports_started(bool cancelBeforeLaunch)
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        using var cancel = new CancellationTokenSource();
        var runner = new VerificationRunner(new RecipeRunner(repo, "test-launch"), beforeStage: (name, token) =>
        {
            if (name == "test" && cancelBeforeLaunch) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        });
        var result = await runner.RunAsync(new(repo, "HEAD", "specs/T-107.md", "muthur", Path.Combine(root, "output"), Path.Combine(root, "cache")), cancel.Token);
        var evidence = JsonSerializer.Deserialize(File.ReadAllText(result.EvidencePath!), VerificationJsonContext.Default.VerificationEvidence)!;
        Assert.False(evidence.TestsStarted);
        Assert.False(evidence.TestsCompleted);
        Assert.Equal(cancelBeforeLaunch ? 130 : 1, result.ExitCode);
        Assert.True(evidence.CleanupSucceeded);
    }

    [Fact]
    public async Task Publication_wait_observes_cancellation_and_does_not_create_a_success_entry()
    {
        var evidence = Evidence();
        var cache = new VerificationCache(Path.Combine(root, "cache"));
        var stage = await cache.StageAsync(evidence, Install(), default);
        using (await cache.LockAsync(evidence.Key!, default))
        {
            using var cancel = new CancellationTokenSource();
            var publication = cache.PublishAsync(stage, evidence, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
        }
        Assert.False(Directory.Exists(Path.Combine(root, "cache", evidence.Key!)));
        await cache.DiscardAsync(stage, evidence, default);
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public async Task Junction_ancestors_are_refused_for_writable_paths_and_installations()
    {
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        var script = Path.Combine(root, "junction.ps1");
        File.WriteAllText(script, "param($Link, $Target) New-Item -ItemType Junction -Path $Link -Target $Target | Out-Null");
        var result = await new ProcessRunner().RunAsync("pwsh", ["-NoProfile", "-File", script, link, target], root);
        Assert.True(result.Ok, result.Message);
        try
        {
            Assert.Throws<IOException>(() => VerificationFiles.PlainPath(Path.Combine(link, "new-output")));
            Assert.Throws<IOException>(() => VerificationFiles.Inventory(root));
            var readOnly = Path.Combine(target, "keep");
            File.WriteAllText(readOnly, "outside owned junction");
            File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
            Assert.Throws<IOException>(() => VerificationFiles.DeleteOwned(root, Path.GetDirectoryName(root)!));
            Assert.True((File.GetAttributes(readOnly) & FileAttributes.ReadOnly) != 0);
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("build", false, false)]
    [InlineData("test", true, true)]
    [InlineData("preparation", true, true)]
    public async Task Unknown_provenance_runs_fresh_checks_and_failure_never_publishes(string failure, bool started, bool completed)
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        var output = Path.Combine(root, "output");
        var cache = Path.Combine(root, "cache");
        var fake = new RecipeRunner(repo, failure);
        var result = await new VerificationRunner(fake).RunAsync(new(repo, "HEAD", "specs/T-107.md", "muthur", output, cache));
        Assert.Equal(1, result.ExitCode);
        var evidence = JsonSerializer.Deserialize(File.ReadAllText(result.EvidencePath!), VerificationJsonContext.Default.VerificationEvidence)!;
        Assert.Equal("failed", evidence.Status);
        Assert.Equal("disabled", evidence.CacheStatus);
        Assert.Null(evidence.Key);
        Assert.Equal(started, evidence.TestsStarted);
        Assert.Equal(completed, evidence.TestsCompleted);
        Assert.True(evidence.CleanupSucceeded);
        Assert.False(Directory.Exists(cache));
        Assert.Empty(Directory.EnumerateDirectories(output, "scratch-*"));
        Assert.All(fake.Environments, values => Assert.DoesNotContain("MUTHUR_TOKEN", values.Keys));
    }

    [Fact]
    public async Task Verified_hit_still_runs_full_tests_and_smoke_failure_is_not_success()
    {
        var producer = Evidence();
        await Publish(producer);
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        var runner = new VerificationRunner(new RecipeRunner(repo, "up"), inputProbe: (evidence, _) =>
        {
            evidence.Inputs = producer.Inputs;
            evidence.Key = producer.Key;
            return Task.CompletedTask;
        });
        var result = await runner.RunAsync(new(repo, "HEAD", "specs/T-107.md", "muthur", Path.Combine(root, "output"), Path.Combine(root, "cache")));
        var evidence = JsonSerializer.Deserialize(File.ReadAllText(result.EvidencePath!), VerificationJsonContext.Default.VerificationEvidence)!;
        Assert.Equal("hit", evidence.CacheStatus);
        Assert.True(evidence.TestsExecuted);
        Assert.Contains(evidence.Stages, s => s.Name == "build" && s.Status == "success");
        Assert.Contains(evidence.Stages, s => s.Name == "test" && s.Status == "success");
        Assert.Contains(evidence.Stages, s => s.Name == "up" && s.Status == "failed");
        Assert.DoesNotContain(evidence.Stages, s => s.Name == "preparation");
        Assert.Equal("failed", evidence.Status);
        Assert.True(Directory.Exists(Path.Combine(root, "cache", producer.Key!)));
    }

    [Fact]
    public async Task Malformed_recovery_never_deletes_scratch()
    {
        File.WriteAllText(Path.Combine(root, "ownership.json"), "{}");
        var marker = Path.Combine(root, "keep");
        File.WriteAllText(marker, "owned elsewhere");
        await Assert.ThrowsAsync<JsonException>(() => new VerificationRunner(new NeverRunner()).CleanupAsync(root));
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_recovery_is_idempotent_even_after_scratch_was_already_cleaned(bool alreadyCleaned)
    {
        var evidence = Evidence();
        evidence.Status = "running";
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        var scratch = Path.Combine(output, "scratch-" + evidence.RunId.ToString("N"));
        if (!alreadyCleaned) Directory.CreateDirectory(scratch);
        var owner = new VerificationOwnership
        {
            RunId = evidence.RunId, Output = output, Repository = root, Scratch = scratch,
            Checkout = Path.Combine(scratch, "checkout"), Commit = evidence.Commit,
            Runner = new(int.MaxValue, 1, "absent-process"), Url = "http://127.0.0.1:1", Cleaned = alreadyCleaned,
        };
        VerificationFiles.Atomic(Path.Combine(output, "ownership.json"), owner, VerificationJsonContext.Default.VerificationOwnership);
        VerificationFiles.Atomic(Path.Combine(output, "evidence.json"), evidence, VerificationJsonContext.Default.VerificationEvidence);
        var runner = new VerificationRunner(new NeverRunner());
        Assert.Equal("interrupted", (await runner.CleanupAsync(output)).Status);
        Assert.Equal("interrupted", (await runner.CleanupAsync(output)).Status);
        Assert.False(Directory.Exists(scratch));
        Assert.True(File.Exists(Path.Combine(output, "evidence.json")));
    }

    [Fact]
    public async Task Recovery_refuses_a_live_runner_before_deleting_anything()
    {
        var evidence = Evidence();
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        var scratch = Path.Combine(output, "scratch-" + evidence.RunId.ToString("N"));
        Directory.CreateDirectory(scratch);
        using var current = Process.GetCurrentProcess();
        var owner = new VerificationOwnership
        {
            RunId = evidence.RunId, Output = output, Repository = root, Scratch = scratch,
            Checkout = Path.Combine(scratch, "checkout"), Commit = evidence.Commit, Url = "http://127.0.0.1:1",
            Runner = new(current.Id, current.StartTime.ToUniversalTime().Ticks, current.MainModule!.FileName),
        };
        VerificationFiles.Atomic(Path.Combine(output, "ownership.json"), owner, VerificationJsonContext.Default.VerificationOwnership);
        await Assert.ThrowsAsync<IOException>(() => new VerificationRunner(new NeverRunner()).CleanupAsync(output));
        Assert.True(Directory.Exists(scratch));
    }

    [Fact]
    public async Task Cancellation_preserves_output_and_waits_for_child_exit()
    {
        var name = "muthur-verification-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var script = Path.Combine(root, "barrier.ps1");
        var child = Path.Combine(root, "child.ps1");
        File.WriteAllText(script, "param($Name)\n[Console]::WriteLine('partial output'); [Console]::Out.Flush()\n" +
            "$info = [Diagnostics.ProcessStartInfo]::new('pwsh'); $info.UseShellExecute = $false; $info.CreateNoWindow = $true\n" +
            "foreach ($a in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'child.ps1'), $Name)) { $info.ArgumentList.Add($a) }\n" +
            "$child = [Diagnostics.Process]::Start($info); $child.WaitForExit()\n");
        File.WriteAllText(child, "param($Name)\n" +
            "$p = [IO.Pipes.NamedPipeClientStream]::new('.', $Name, [IO.Pipes.PipeDirection]::InOut)\n$p.Connect()\n" +
            "$w = [IO.StreamWriter]::new($p); $w.AutoFlush = $true; $w.WriteLine($PID)\n$r = [IO.StreamReader]::new($p); $r.ReadLine() | Out-Null\n");
        using var cancel = new CancellationTokenSource();
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var rootPid = 0;
        var running = new ProcessRunner().RunObservedAsync("pwsh", ["-NoProfile", "-File", script, name], root,
            ct: cancel.Token, started: process => rootPid = process.Id);
        await pipe.WaitForConnectionAsync(hang.Token);
        using var reader = new StreamReader(pipe);
        var pid = int.Parse((await reader.ReadLineAsync(hang.Token))!);
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<ProcessCancelledException>(() => running);
        Assert.Contains("partial output", error.Result.StdOut);
        Assert.Equal(130, error.Result.ExitCode);
        foreach (var id in new[] { pid, rootPid })
        {
            try { using var process = Process.GetProcessById(id); Assert.True(process.HasExited); }
            catch (ArgumentException) { }
        }
    }

    private VerificationEvidence Evidence(string value = "same")
    {
        var commit = VerificationFiles.Hash(value)[..40];
        var spec = VerificationFiles.Hash("spec");
        var recipe = VerificationFiles.Hash("recipe");
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["commit"] = commit, ["spec"] = spec, ["recipe"] = recipe };
        return new()
        {
            RunId = Guid.NewGuid(), Repository = root, GitDirectory = Path.Combine(root, ".git"), Commit = commit,
            Tree = "tree", SpecPath = "specs/T-107.md", SpecSha256 = spec, RecipeSha256 = recipe,
            Recipe = VerificationRecipes.Muthur, StartedUtc = DateTimeOffset.UnixEpoch, Inputs = inputs,
            Key = VerificationCache.Key(inputs), Status = "success", CleanupSucceeded = true,
        };
    }

    private string Install()
    {
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(install, "server"));
        Directory.CreateDirectory(Path.Combine(install, "kit"));
        File.WriteAllText(Path.Combine(install, "muthur.exe"), "fixture");
        File.WriteAllText(Path.Combine(install, "server", "Muthur.Server.dll"), "fixture");
        File.WriteAllText(Path.Combine(install, "kit", "fixture.md"), "fixture");
        return install;
    }

    private async Task<VerificationCache> Publish(VerificationEvidence evidence)
    {
        var cache = new VerificationCache(Path.Combine(root, "cache"));
        var stage = await cache.StageAsync(evidence, Install(), default);
        await cache.PublishAsync(stage, evidence, default);
        return cache;
    }

    private sealed class NeverRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null) =>
            throw new InvalidOperationException("Unexpected child process.");
    }

    private sealed class GitFixtureRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null) =>
            fileName == "git"
                ? new ProcessRunner().RunAsync(fileName, arguments, workingDirectory, stdin, timeout, ct, scrubEnvironment, environment)
                : Task.FromResult(new ProcessResult(1, "", "fixture stops before build"));
    }

    private sealed class RecipeRunner(string repo, string failure) : IProcessRunner
    {
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Assert.NotNull(environment);
            Assert.NotNull(scrubEnvironment);
            Environments.Add(environment);
            string output = "";
            var code = 0;
            if (fileName == "git")
            {
                if (arguments.SequenceEqual(new[] { "rev-parse", "--show-toplevel" })) output = repo;
                else if (arguments.SequenceEqual(new[] { "rev-parse", "--git-common-dir" })) output = Path.Combine(repo, ".git");
                else if (arguments[0] == "rev-parse") output = new string('a', 40);
                else if (arguments[0] == "ls-tree") output = "100644 blob " + new string('b', 40) + "\t" + arguments[^1];
                else if (arguments[0] == "archive")
                {
                    using var archive = ZipFile.Open(arguments[2][9..], ZipArchiveMode.Create);
                    archive.CreateEntry("../escape");
                    if (failure != "archive-missing")
                    {
                        var entry = archive.CreateEntry(arguments[^1]);
                        if (failure == "archive-directory") entry.ExternalAttributes = (int)FileAttributes.Directory;
                        if (failure == "archive-symlink") entry.ExternalAttributes = 0xa000 << 16;
                        using var writer = new StreamWriter(entry.Open());
                        writer.Write("frozen spec");
                    }
                    if (failure == "archive-duplicate") archive.CreateEntry(arguments[^1]);
                }
                else if (arguments[0] == "show") output = "{\"verification\":" + JsonSerializer.Serialize(VerificationRecipes.Muthur, VerificationJsonContext.Default.VerificationRecipe) + "}";
                else if (arguments[0] == "worktree" && arguments[1] == "add") Directory.CreateDirectory(arguments[3]);
                else if (arguments[0] == "worktree" && arguments[1] == "remove") Directory.Delete(arguments[3], true);
            }
            else if (arguments.Contains("--info") || arguments.Contains("--version")) code = 127;
            else
            {
                var stage = arguments[0] is "build" or "test" or "up" ? arguments[0] : "preparation";
                if (stage == "test" && failure == "test-launch") return Task.FromResult(new ProcessResult(127, "", "launch failed") { Started = false });
                code = stage == failure ? 1 : 0;
            }
            return Task.FromResult(new ProcessResult(code, output, code == 0 ? "" : "fixture failure"));
        }
    }
}
