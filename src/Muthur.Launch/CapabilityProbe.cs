using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Launch;

public sealed record CapabilityProbeResult(string? Code, int ProbeStarts, long DurationMilliseconds,
    IReadOnlyList<CapabilityObservation> Observations, string Detail);

/// <summary>One explicit admitted experiment. Cleanup completes inside the reservation callback.</summary>
public sealed class CapabilityProbe(IProcessRunner processes, IProbeAdmissionClient admission, TimeProvider? timeProvider = null,
    Func<string, (string FileName, IReadOnlyList<string> Prefix)?>? resolve = null, Func<string, IHarnessAdapter?>? adapterFor = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly string[] Tested = ["shell", "worktree-base", "build", "test", "commit"];

    public async Task<CapabilityProbeResult> RunAsync(string task, HarnessCandidate candidate, WorkerRequest request,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var context = request.Capabilities ?? throw new ArgumentException("Capability context is required.", nameof(request));
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (context.LaunchPath != "worker-run") return new("capability_probe_unsupported", 0, 0, [], "Only worker-run has a bounded probe engine.");
        if (processes is not ICapabilityProcessRunner)
            return new("capability_probe_ownership_unavailable", 0, 0, [], "Probe process exit cannot be confirmed by this runner; no admission requested.");
        if (string.IsNullOrWhiteSpace(task) || string.IsNullOrWhiteSpace(candidate.Account))
            return new("capability_probe_candidate_unavailable", 0, 0, [], "An exact task and catalog account are required.");
        string[] redirected = ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"];
        if (redirected.Any(k => Environment.GetEnvironmentVariable(k) is not null || request.GitEnvironment?.ContainsKey(k) == true))
            return new("capability_identity_unknown", 0, 0, [], "Git repository redirection prevents an isolated probe; no model started.");
        var reservation = new ProbeAdmissionRequest(task, "implementer", candidate.Harness, candidate.Model, candidate.Account, Guid.NewGuid().ToString("N"));
        // Admission/replay/release errors propagate intact. Only the shared runner's MayExecute callback can launch.
        return await new ProbeReservationRunner(admission).RunAsync(reservation,
            token => ExecuteAsync(candidate, request, timeout, token), ct);
    }

    private async Task<CapabilityProbeResult> ExecuteAsync(HarnessCandidate candidate, WorkerRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var startedAt = _clock.GetTimestamp();
        var context = request.Capabilities!;
        var probeRoot = Path.Combine(request.ScratchDirectory, "capability-" + Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(probeRoot, "worktree");
        var fixture = Path.Combine(worktree, ".muthur-capability");
        var nonce = Guid.NewGuid().ToString("n");
        CapabilityIdentity? identity = null;
        var observations = new List<CapabilityObservation>();
        var starts = 0;
        string? code = null;
        var detail = CapabilityFixture.Limitation;
        var addAttempted = false;
        var processCleanupUncertain = false;
        string[] tested = Tested;
        using var budget = new CancellationTokenSource(timeout, _clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            // Check before identity's working-tree diff as well as checkout: clean/process filters can run during diff.
            if (await CapabilityHostGit.CheckFiltersAsync(processes, context.RepositoryRoot, request.GitEnvironment, lifetime.Token) is { } refusal)
                throw new ProbeRefusal(refusal.Code, refusal.Detail);
            var adapter = (adapterFor ?? Harnesses.Find)(candidate.Harness);
            var inspection = await new CapabilityEvaluator(processes, _clock, resolve).InspectAsync(adapter, request, lifetime.Token);
            identity = inspection.Identity;
            if (identity is null || adapter is null)
                throw new ProbeRefusal("capability_identity_unknown", inspection.Diagnostic ?? "Identity unavailable.");
            Directory.CreateDirectory(probeRoot);
            addAttempted = true;
            var add = await processes.RunAsync("git", CapabilityHostGit.Arguments(["worktree", "add", "--detach", worktree, context.BaseCommit]),
                context.RepositoryRoot, timeout: TimeSpan.FromSeconds(10), ct: lifetime.Token, environment: request.GitEnvironment);
            if (!add.Ok) throw new ProbeRefusal("capability_probe_setup_failed", "Disposable pinned worktree could not be created.");
            if (Directory.EnumerateFileSystemEntries(worktree).Any(path => Path.GetFileName(path).Equals(".muthur-capability", StringComparison.OrdinalIgnoreCase)))
                throw new ProbeRefusal("capability_probe_reserved_path", "The pinned checkout contains the reserved .muthur-capability entry; no model started.");
            EnsureOwnedPath(worktree, fixture);
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "fixture.proj"), CapabilityFixture.Project(nonce));
            // A guard MUTHUR enforces itself is proved like the rest: one denied command, refused in the session's own stream.
            var marker = adapter.GuardBlockMarker is { } words && PiGuard.Denies(request.DeniedCommands, CapabilityFixture.DeniedProbeCommand) ? words : null;
            if (marker is not null) tested = [.. Tested, "deny-list"];
            var steps = CapabilityFixture.Steps(fixture, nonce, denyList: marker is not null, shell: adapter.ProbeShell);
            File.WriteAllText(Path.Combine(fixture, "commands.json"), JsonSerializer.Serialize(steps, CapabilityJsonContext.Default.IReadOnlyListCapabilityProbeStep));
            await PrepareCommitAsync(fixture, nonce, lifetime.Token);
            var immutable = new[] { "fixture.proj", "commands.json", "before.txt", Path.Combine("commit", "nonce.txt") }
                .ToDictionary(file => file, file => CapabilityHash.Of(File.ReadAllText(Path.Combine(fixture, file))));
            var attempt = request with
            {
                WorkingDirectory = worktree, ScratchDirectory = fixture,
                GitEnvironment = SessionWorkspace.GitEnvironment(worktree), Prompt = CapabilityFixture.Prompt(steps, adapter.ProbeShell),
            };
            var actual = await new CapabilityEvaluator(processes, _clock, resolve).InspectAsync(adapter, attempt, lifetime.Token);
            if (actual.Identity != identity)
                throw new ProbeRefusal("capability_identity_unknown", "Disposable worktree configuration differs from the requested route; no model started.");
            var invocation = adapter.Build(attempt);
            var executable = CapabilityExecutable.Resolve(invocation.FileName, resolve ?? ExecutableResolver.Resolve);
            if (executable is null) throw new ProbeRefusal("capability_identity_unknown", "Harness executable disappeared before launch.");
            lifetime.Token.ThrowIfCancellationRequested();
            starts = 1;
            var result = await processes.RunAsync(executable.Value.FileName, [.. executable.Value.Prefix, .. invocation.Arguments], worktree,
                invocation.Stdin, timeout, lifetime.Token, ["MUTHUR_AGENT", "MUTHUR_TOKEN"], attempt.GitEnvironment);
            var intact = immutable.All(pair => File.Exists(Path.Combine(fixture, pair.Key)) &&
                new FileInfo(Path.Combine(fixture, pair.Key)).Length < 65_536 && CapabilityHash.Of(File.ReadAllText(Path.Combine(fixture, pair.Key))) == pair.Value);
            foreach (var key in tested)
            {
                var state = CapabilityStates.Unknown;
                var exit = ReadReceipt(Path.Combine(fixture, key + ".receipt.json"), nonce);
                if (result.ExitCode == 124) state = CapabilityStates.TemporarilyFailing;
                else if (key == "deny-list")
                {
                    // The refusal in the stream is the evidence; a receipt the model wrote after it (126) does not undo it,
                    // and any other receipt means the command ran.
                    var refused = result.StdOut.Contains(marker!, StringComparison.Ordinal) &&
                        result.StdOut.Contains(CapabilityFixture.DeniedProbeCommand, StringComparison.Ordinal);
                    state = refused ? CapabilityStates.Available
                        : exit is { } ran && ran != 126 ? CapabilityStates.Unavailable
                        : !result.Ok ? CapabilityStates.TemporarilyFailing : CapabilityStates.Unknown;
                }
                else if (intact && exit is { } status)
                {
                    state = status == 0 ? CapabilityStates.Available : CapabilityStates.Unavailable;
                    if (status == 0 && !await VerifyAsync(key, worktree, fixture, nonce, context.BaseCommit, lifetime.Token)) state = CapabilityStates.Unknown;
                }
                else if (!result.Ok) state = CapabilityStates.TemporarilyFailing;
                observations.Add(Observation(identity, key, state, startedAt));
            }
        }
        catch (ProbeCleanupUncertainException) { processCleanupUncertain = true; throw; }
        catch (ProbeRefusal ex) { code = ex.Code; detail = ex.Message; }
        catch (OperationCanceledException)
        {
            code = ct.IsCancellationRequested ? "capability_probe_cancelled" : "capability_probe_timeout";
            if (identity is not null) observations = tested.Select(k => Observation(identity, k, CapabilityStates.TemporarilyFailing, startedAt)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            code = "capability_probe_transport_failed";
            if (identity is not null) observations = tested.Select(k => Observation(identity, k, CapabilityStates.TemporarilyFailing, startedAt)).ToList();
        }
        finally
        {
            // An uncertain process may still be using the worktree. Preserve it and its reservation for recovery.
            if (!processCleanupUncertain)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (addAttempted)
                    {
                        // Git for Windows can traverse directory junctions while removing a worktree.
                        // Unlink reparse entries first, without enumerating or deleting their targets.
                        RemoveLinks(worktree, cleanup.Token);
                        var removed = await processes.RunAsync("git", CapabilityHostGit.Arguments(["worktree", "remove", "--force", "--force", worktree]), context.RepositoryRoot,
                            timeout: TimeSpan.FromSeconds(8), ct: cleanup.Token, environment: request.GitEnvironment);
                        if (!removed.Ok || Directory.Exists(worktree)) throw new IOException("Disposable worktree removal was not confirmed.");
                    }
                    if (Directory.Exists(probeRoot))
                        await Task.Run(() => Directory.Delete(probeRoot, recursive: true), cleanup.Token).WaitAsync(cleanup.Token);
                }
                catch (Exception ex)
                { throw new ProbeCleanupUncertainException("Probe worktree cleanup could not be confirmed. Preserve scratch and confirm process/worktree cleanup before reservation release.", ex); }
            }
        }
        // Never publish success (or any cache update) after uncertain process/worktree cleanup.
        if (identity is not null && observations.Count > 0) new CapabilityStore(context.CacheDirectory, _clock).Write(identity, observations);
        return new(code, starts, (long)_clock.GetElapsedTime(startedAt).TotalMilliseconds, observations, detail);
    }

    private async Task PrepareCommitAsync(string fixture, string nonce, CancellationToken ct)
    {
        var repository = Path.Combine(fixture, "commit");
        EnsureOwnedPath(Path.GetDirectoryName(fixture)!, repository);
        Directory.CreateDirectory(repository);
        var prefix = new[] { "-c", "core.hooksPath=" + Path.Combine(fixture, "no-hooks"), "-c", "user.name=CapabilityFixture",
            "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false" };
        async Task<ProcessResult> Git(params string[] args) => await processes.RunAsync("git", CapabilityHostGit.Arguments([.. prefix, .. args]), repository,
            timeout: TimeSpan.FromSeconds(5), ct: ct, environment: SessionWorkspace.GitEnvironment(repository));
        var init = await Git("init", "--quiet");
        if (!init.Ok)
            throw new ProbeRefusal("capability_probe_setup_failed", $"Isolated commit fixture git init failed with exit code {init.ExitCode}.");
        var commit = await Git("commit", "--quiet", "--allow-empty", "-m", "fixture baseline");
        if (!commit.Ok)
            throw new ProbeRefusal("capability_probe_setup_failed", $"Isolated commit fixture git commit failed with exit code {commit.ExitCode}.");
        var before = await Git("rev-parse", "HEAD");
        if (!before.Ok) throw new ProbeRefusal("capability_probe_setup_failed", "Isolated baseline HEAD is unreadable.");
        File.WriteAllText(Path.Combine(fixture, "before.txt"), before.StdOut.Trim());
        File.WriteAllText(Path.Combine(repository, "nonce.txt"), nonce);
    }

    private CapabilityObservation Observation(CapabilityIdentity identity, string key, string state, long startedAt)
    {
        var now = _clock.GetUtcNow();
        return new(identity, key, state, now, now.Add(state == CapabilityStates.TemporarilyFailing ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24)),
            CapabilityFixture.Limitation, (long)_clock.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private async Task<bool> VerifyAsync(string key, string worktree, string fixture, string nonce, string basis, CancellationToken ct)
    {
        bool Output(string file, string expected) => File.Exists(Path.Combine(fixture, file)) && new FileInfo(Path.Combine(fixture, file)).Length <= 256 &&
            File.ReadAllText(Path.Combine(fixture, file)).Trim() == expected;
        if (key == "shell") return Output("shell.txt", nonce);
        if (key == "build") return Output("build.txt", nonce);
        if (key == "test") return Output("test.txt", nonce);
        if (key == "worktree-base" && !Output("head.txt", basis)) return false;
        var directory = key == "commit" ? Path.Combine(fixture, "commit") : worktree;
        var args = key == "commit" ? new[] { "show", "HEAD:nonce.txt" } : ["rev-parse", "HEAD"];
        var result = await processes.RunAsync("git", CapabilityHostGit.Arguments(args), directory, timeout: TimeSpan.FromSeconds(5), ct: ct,
            environment: SessionWorkspace.GitEnvironment(directory));
        if (!result.Ok || result.StdOut.Trim() != (key == "commit" ? nonce : basis)) return false;
        if (key != "commit") return true;
        var before = File.ReadAllText(Path.Combine(fixture, "before.txt")).Trim();
        var after = await processes.RunAsync("git", CapabilityHostGit.Arguments(["rev-parse", "HEAD"]), directory, timeout: TimeSpan.FromSeconds(5), ct: ct,
            environment: SessionWorkspace.GitEnvironment(directory));
        if (before.Length != 40 || !before.All(char.IsAsciiHexDigit) || !after.Ok || after.StdOut.Trim().Length != 40 || before == after.StdOut.Trim()) return false;
        var ancestor = await processes.RunAsync("git", CapabilityHostGit.Arguments(["merge-base", "--is-ancestor", before, "HEAD"]), directory,
            timeout: TimeSpan.FromSeconds(5), ct: ct, environment: SessionWorkspace.GitEnvironment(directory));
        return ancestor.Ok;
    }

    private static int? ReadReceipt(string path, string nonce)
    {
        try
        {
            if (new FileInfo(path).Length > 8192) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.GetProperty("nonce").GetString() != nonce) return null;
            return document.RootElement.GetProperty("exitCode").TryGetInt32(out var exit) ? exit : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private static void EnsureOwnedPath(string worktree, string path)
    {
        var root = Path.GetFullPath(worktree);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ProbeRefusal("capability_probe_reserved_path", "Fixture path is outside the owned worktree.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ProbeRefusal("capability_probe_reserved_path", "Fixture path traverses a link or reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (current == root) break;
        }
    }

    private static void RemoveLinks(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
        }
        else if ((attributes & FileAttributes.Directory) != 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) RemoveLinks(entry, ct);
    }

    private sealed class ProbeRefusal(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}

internal sealed class CapabilityFilterException(string code, string message) : IOException(message)
{
    internal string Code { get; } = code;
}

internal static class CapabilityHostGit
{
    internal static async Task<(string Code, string Detail)?> CheckFiltersAsync(IProcessRunner runner,
        string directory, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        var filters = await runner.RunAsync("git", ["config", "--null", "--get-regexp", "^filter\\..*\\.(clean|smudge|process)$"],
            directory, timeout: TimeSpan.FromSeconds(5), ct: ct,
            scrubEnvironment: ["MUTHUR_AGENT", "MUTHUR_TOKEN"], environment: environment);
        if (filters.StdOut.Length > 1_048_576 || (!filters.Ok && filters.ExitCode != 1) ||
            (filters.ExitCode == 1 && filters.StdOut.Length != 0))
            return ("capability_probe_setup_failed", "Checkout filter configuration could not be read completely.");
        if (filters.Ok && filters.StdOut.Split('\0').Any(entry => entry.IndexOf('\n') is var index && index >= 0 && entry[(index + 1)..].Length > 0))
            return ("capability_probe_checkout_filter", "Configured external checkout filters are unsupported; no diff, checkout or model started.");
        return null;
    }

    // Host metadata/setup only. Never apply these overrides to the measured harness or its config identity.
    internal static string[] Arguments(IReadOnlyList<string> arguments) =>
        ["-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
         "-c", "core.fsmonitor=false", "-c", "submodule.recurse=false", "-c", "diff.external=", .. arguments];
}
