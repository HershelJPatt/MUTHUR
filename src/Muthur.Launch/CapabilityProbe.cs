using System.Text.Json;

namespace Muthur.Launch;

/// <summary>Admission must reserve the existing catalog/account/budget/capacity gates before returning a lease.</summary>
public interface ICapabilityProbeAdmission
{
    Task<ICapabilityProbeLease?> AdmitAsync(HarnessCandidate candidate, WorkerRequest request, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Disposal must stop and reap every probe descendant before releasing the reservation.</summary>
public interface ICapabilityProbeLease : IAsyncDisposable
{
    IProcessRunner Processes { get; }
}

public sealed class CapabilityProbeAdmissionUnavailable : ICapabilityProbeAdmission
{
    public Task<ICapabilityProbeLease?> AdmitAsync(HarnessCandidate candidate, WorkerRequest request, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult<ICapabilityProbeLease?>(null);
}

public sealed record CapabilityProbeResult(string? Code, int ProbeStarts, long DurationMilliseconds,
    IReadOnlyList<CapabilityObservation> Observations, string Detail);

/// <summary>One explicit admitted experiment. It never retries or infers interaction/native capabilities.</summary>
public sealed class CapabilityProbe(IProcessRunner processes, ICapabilityProbeAdmission admission, TimeProvider? timeProvider = null,
    Func<string, (string FileName, IReadOnlyList<string> Prefix)?>? resolve = null, Func<string, IHarnessAdapter?>? adapterFor = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly string[] Tested = ["shell", "worktree-base", "build", "test", "commit"];
    private const string Limitation = "Fixture v1: shell nonce; git rev-parse HEAD; dotnet msbuild fixture.proj /t:Build and /t:Test; isolated git commit. Proves tool execution and SDK route, not arbitrary project correctness.";

    public async Task<CapabilityProbeResult> RunAsync(HarnessCandidate candidate, WorkerRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        var startedAt = _clock.GetTimestamp();
        var context = request.Capabilities ?? throw new ArgumentException("Capability context is required.", nameof(request));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (context.LaunchPath != "worker-run") return new("capability_probe_unsupported", 0, 0, [], "Only worker-run has a bounded probe engine.");
        string[] redirected = ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"];
        if (redirected.Any(k => Environment.GetEnvironmentVariable(k) is not null || request.GitEnvironment?.ContainsKey(k) == true))
            return new("capability_identity_unknown", 0, 0, [], "Git repository redirection prevents an isolated probe; no model started.");
        var lease = await admission.AdmitAsync(candidate, request, timeout, ct);
        if (lease is null) return new("capability_probe_admission_unavailable", 0, 0, [], "Shared worker budget/capacity admission is unavailable. No model started.");

        var probeRoot = Path.Combine(request.ScratchDirectory, "capability-" + Guid.NewGuid().ToString("n"));
        var worktree = Path.Combine(probeRoot, "worktree");
        var fixture = Path.Combine(worktree, ".muthur-capability");
        var nonce = Guid.NewGuid().ToString("n");
        CapabilityIdentity? identity = null;
        var observations = new List<CapabilityObservation>();
        var starts = 0;
        string? code = null;
        var detail = Limitation;
        var addAttempted = false;
        using var budget = new CancellationTokenSource(timeout, _clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            var adapter = (adapterFor ?? Harnesses.Find)(candidate.Harness);
            var inspection = await new CapabilityEvaluator(processes, _clock, resolve).InspectAsync(adapter, request, lifetime.Token);
            identity = inspection.Identity;
            if (identity is null || adapter is null)
                throw new ProbeRefusal("capability_identity_unknown", inspection.Diagnostic ?? "Identity unavailable.");
            Directory.CreateDirectory(probeRoot);
            addAttempted = true;
            var add = await processes.RunAsync("git", ["worktree", "add", "--detach", worktree, context.BaseCommit],
                context.RepositoryRoot, timeout: TimeSpan.FromSeconds(10), ct: lifetime.Token, environment: request.GitEnvironment);
            if (!add.Ok) throw new ProbeRefusal("capability_probe_setup_failed", "Disposable pinned worktree could not be created.");
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "fixture.proj"), Project(nonce));
            File.WriteAllText(Path.Combine(fixture, "probe.ps1"), Script(nonce, context.BaseCommit));
            var attempt = request with
            {
                WorkingDirectory = worktree,
                ScratchDirectory = fixture,
                GitEnvironment = SessionWorkspace.GitEnvironment(worktree),
                Prompt = "Execute exactly once through your shell tool: pwsh -NoProfile -File .muthur-capability/probe.ps1 . " +
                    "Do not edit the script, project, receipts or outputs. Do not request new permissions. If denied, stop. " + Limitation,
            };
            var actual = await new CapabilityEvaluator(processes, _clock, resolve).InspectAsync(adapter, attempt, lifetime.Token);
            if (actual.Identity != identity)
                throw new ProbeRefusal("capability_identity_unknown", "Disposable worktree configuration differs from the requested route; no model started.");
            var invocation = adapter.Build(attempt);
            var executable = (resolve ?? ExecutableResolver.Resolve)(invocation.FileName);
            if (executable is null) throw new ProbeRefusal("capability_identity_unknown", "Harness executable disappeared before launch.");
            starts = 1;
            var result = await lease.Processes.RunAsync(executable.Value.FileName, [.. executable.Value.Prefix, .. invocation.Arguments], worktree,
                invocation.Stdin, timeout, lifetime.Token, ["MUTHUR_AGENT", "MUTHUR_TOKEN"], attempt.GitEnvironment);
            var receipts = ReadReceipts(Path.Combine(fixture, "receipts.json"), nonce);
            foreach (var key in Tested)
            {
                var state = CapabilityStates.Unknown;
                if (result.ExitCode == 124) state = CapabilityStates.TemporarilyFailing;
                else if (receipts.TryGetValue(key, out var exit))
                {
                    state = exit == 0 ? CapabilityStates.Available : CapabilityStates.Unavailable;
                    if (exit == 0 && !await VerifyAsync(key, worktree, fixture, nonce, context.BaseCommit, lifetime.Token)) state = CapabilityStates.Unknown;
                }
                else if (!result.Ok) state = CapabilityStates.TemporarilyFailing;
                observations.Add(Observation(identity, key, state, startedAt));
            }
        }
        catch (ProbeRefusal ex) { code = ex.Code; detail = ex.Message; }
        catch (OperationCanceledException)
        {
            code = ct.IsCancellationRequested ? "capability_probe_cancelled" : "capability_probe_timeout";
            if (identity is not null) observations = Tested.Select(k => Observation(identity, k, CapabilityStates.TemporarilyFailing, startedAt)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            code = "capability_probe_transport_failed";
            if (identity is not null) observations = Tested.Select(k => Observation(identity, k, CapabilityStates.TemporarilyFailing, startedAt)).ToList();
        }
        finally
        {
            try { await lease.DisposeAsync(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            { code = "capability_probe_cleanup_failed"; detail += " Descendant cleanup failed."; }
            finally
            {
                if (addAttempted)
                {
                    try
                    {
                        var removed = await processes.RunAsync("git", ["worktree", "remove", "--force", worktree], context.RepositoryRoot,
                            timeout: TimeSpan.FromSeconds(10), ct: CancellationToken.None, environment: request.GitEnvironment);
                        if (!removed.Ok && Directory.Exists(worktree))
                        { code = "capability_probe_cleanup_failed"; detail += " Disposable worktree cleanup failed: " + worktree; }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                    { code = "capability_probe_cleanup_failed"; detail += " Disposable worktree cleanup failed."; }
                }
                try
                {
                    if (Directory.Exists(probeRoot) && !Directory.Exists(worktree)) Directory.Delete(probeRoot, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { code = "capability_probe_cleanup_failed"; detail += " Scratch cleanup failed."; }
            }
        }
        if (code == "capability_probe_cleanup_failed")
            observations = observations.Select(o => o with { State = CapabilityStates.TemporarilyFailing, ExpiresAt = o.ObservedAt.AddMinutes(5) }).ToList();
        if (identity is not null && observations.Count > 0)
            new CapabilityStore(context.CacheDirectory, _clock).Write(identity, observations);
        return new(code, starts, (long)_clock.GetElapsedTime(startedAt).TotalMilliseconds, observations, detail);
    }

    private CapabilityObservation Observation(CapabilityIdentity identity, string key, string state, long startedAt)
    {
        var now = _clock.GetUtcNow();
        return new(identity, key, state, now, now.Add(state == CapabilityStates.TemporarilyFailing ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24)),
            Limitation, (long)_clock.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private async Task<bool> VerifyAsync(string key, string worktree, string fixture, string nonce, string basis, CancellationToken ct)
    {
        bool Output(string file) => File.Exists(Path.Combine(fixture, file)) && new FileInfo(Path.Combine(fixture, file)).Length <= 256 &&
            File.ReadAllText(Path.Combine(fixture, file)).Trim() == nonce;
        if (key == "shell") return Output("shell.txt");
        if (key == "build") return Output("build.txt");
        if (key == "test") return Output("test.txt");
        var directory = key == "commit" ? Path.Combine(fixture, "commit") : worktree;
        var args = key == "commit" ? new[] { "show", "HEAD:nonce.txt" } : ["rev-parse", "HEAD"];
        var result = await processes.RunAsync("git", args, directory, timeout: TimeSpan.FromSeconds(5), ct: ct,
            environment: SessionWorkspace.GitEnvironment(directory));
        if (!result.Ok || result.StdOut.Trim() != (key == "commit" ? nonce : basis)) return false;
        if (key != "commit") return true;
        var before = File.ReadAllText(Path.Combine(fixture, "before.txt")).Trim();
        var after = await processes.RunAsync("git", ["rev-parse", "HEAD"], directory, timeout: TimeSpan.FromSeconds(5), ct: ct,
            environment: SessionWorkspace.GitEnvironment(directory));
        if (before.Length != 40 || !before.All(char.IsAsciiHexDigit) || !after.Ok || after.StdOut.Trim().Length != 40 || before == after.StdOut.Trim()) return false;
        var ancestor = await processes.RunAsync("git", ["merge-base", "--is-ancestor", before, "HEAD"], directory,
            timeout: TimeSpan.FromSeconds(5), ct: ct, environment: SessionWorkspace.GitEnvironment(directory));
        return ancestor.Ok;
    }

    private static Dictionary<string, int> ReadReceipts(string path, string nonce)
    {
        try
        {
            if (new FileInfo(path).Length > 8192) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.GetProperty("nonce").GetString() != nonce) return [];
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.GetProperty("steps").EnumerateObject())
                if (!Tested.Contains(property.Name, StringComparer.Ordinal) || !property.Value.TryGetInt32(out var exit) || !result.TryAdd(property.Name, exit)) return [];
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return []; }
    }

    private static string Project(string nonce) => $$"""
        <Project>
          <Target Name="Build">
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)build.txt" Lines="{{nonce}}" Overwrite="true" />
          </Target>
          <Target Name="Test">
            <Error Condition="!Exists('$(MSBuildThisFileDirectory)build.txt')" Text="Build output missing" />
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)test.txt" Lines="{{nonce}}" Overwrite="true" />
          </Target>
        </Project>
        """;

    private sealed class ProbeRefusal(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private static string Script(string nonce, string basis) => $$"""
        $ErrorActionPreference = 'Stop'
        $PSNativeCommandUseErrorActionPreference = $false
        $fixture = $PSScriptRoot
        $steps = [ordered]@{}
        function Step([string]$name, [scriptblock]$body) {
            try { & $body; $steps[$name] = 0 } catch { $steps[$name] = 1 }
            @{ nonce = '{{nonce}}'; steps = $steps } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'receipts.json')
        }
        Step 'shell' { Set-Content -LiteralPath (Join-Path $fixture 'shell.txt') '{{nonce}}' }
        Step 'worktree-base' {
            $head = & git rev-parse HEAD
            if ($LASTEXITCODE -ne 0 -or "$head".Trim() -ne '{{basis}}') { throw 'Base mismatch' }
        }
        Step 'build' {
            & dotnet msbuild (Join-Path $fixture 'fixture.proj') /t:Build /nologo /noautoresponse
            if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
        }
        Step 'test' {
            & dotnet msbuild (Join-Path $fixture 'fixture.proj') /t:Test /nologo /noautoresponse
            if ($LASTEXITCODE -ne 0) { throw 'Test failed' }
        }
        Step 'commit' {
            $commit = Join-Path $fixture 'commit'
            New-Item -ItemType Directory -Path $commit | Out-Null
            $gitArgs = @("--git-dir=$(Join-Path $commit '.git')", "--work-tree=$commit", '-c', "core.hooksPath=$(Join-Path $fixture 'no-hooks')", '-c', 'user.name=CapabilityFixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false')
            & git @gitArgs init --quiet
            if ($LASTEXITCODE -ne 0) { throw 'Init failed' }
            & git @gitArgs commit --quiet --allow-empty -m 'fixture baseline'
            if ($LASTEXITCODE -ne 0) { throw 'Baseline failed' }
            & git @gitArgs rev-parse HEAD | Set-Content -LiteralPath (Join-Path $fixture 'before.txt')
            if ($LASTEXITCODE -ne 0) { throw 'Baseline HEAD failed' }
            Set-Content -LiteralPath (Join-Path $commit 'nonce.txt') '{{nonce}}'
            & git @gitArgs add -- nonce.txt
            if ($LASTEXITCODE -ne 0) { throw 'Add failed' }
            & git @gitArgs commit --quiet -m 'capability fixture'
            if ($LASTEXITCODE -ne 0) { throw 'Commit failed' }
        }
        """;
}
