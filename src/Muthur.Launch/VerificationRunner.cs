using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Muthur.Contracts;

namespace Muthur.Launch;

public sealed class VerificationRunner(IProcessRunner processes, TimeProvider? time = null, HttpClient? http = null,
    Func<VerificationEvidence, CancellationToken, Task>? inputProbe = null,
    Func<string, CancellationToken, Task>? beforeStage = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly HttpClient http = http ?? new HttpClient(new HttpClientHandler { UseProxy = false });

    public async Task<VerificationResult> RunAsync(VerificationRequest request, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid();
        VerificationEvidence? evidence = null;
        VerificationOwnership? ownership = null;
        VerificationCache? cache = null;
        VerificationJob? job = null;
        Dictionary<string, string>? environment = null;
        string? stage = null;
        string? evidencePath = null;
        string? provisionalScratch = null;
        string? earlyError = null;
        var exit = 1;
        var started = clock.GetUtcNow();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (request.TimeoutMinutes is < 1 or > 60 || request.Recipe != "muthur" ||
                (request.Subject is not null && !Guid.TryParse(request.Subject, out _)))
                throw new ArgumentException("Recipe must be muthur, timeout 1..60 minutes, and subject a GUID.");
            VerificationFiles.Relative(request.Spec);
            budget.CancelAfter(TimeSpan.FromMinutes(request.TimeoutMinutes));
            var repo = VerificationFiles.PlainPath(request.Repository);
            var output = VerificationFiles.PlainPath(request.Output);
            var cacheRoot = VerificationFiles.PlainPath(request.Cache);
            if (!Directory.Exists(repo) || Directory.Exists(output) || File.Exists(output) || VerificationFiles.Intersects(output, cacheRoot))
                throw new ArgumentException("Repository must exist; output must be new and disjoint from cache.");
            foreach (var path in new[] { output, cacheRoot })
                if (VerificationFiles.Intersects(path, repo) && !VerificationFiles.Within(path, Path.Combine(repo, "artifacts")))
                    throw new ArgumentException("Output/cache intersect source repository.");
            Directory.CreateDirectory(output);
            var scratch = Path.Combine(output, "scratch-" + runId.ToString("N"));
            Directory.CreateDirectory(scratch);
            provisionalScratch = scratch;
            var url = LoopbackUrl();
            environment = VerificationEnvironment.Create(scratch, url);
            if (processes is ProcessRunner)
            {
                File.WriteAllText(Path.Combine(output, "containment.txt"), VerificationJob.Name(runId));
                job = new VerificationJob(runId);
            }
            var root = (await Git(repo, ["rev-parse", "--show-toplevel"], budget.Token, environment)).Trim();
            repo = VerificationFiles.PlainPath(root);
            foreach (var path in new[] { output, cacheRoot })
            {
                if (!VerificationFiles.Intersects(path, repo)) continue;
                var artifacts = Path.Combine(repo, "artifacts");
                if (!VerificationFiles.Within(path, artifacts)) throw new ArgumentException("Output/cache intersect repository outside ignored artifacts.");
                var ignored = await processes.RunAsync("git", ["check-ignore", "--quiet", "--", Path.GetRelativePath(repo, path)], repo, ct: budget.Token,
                    scrubEnvironment: VerificationEnvironment.Scrub(), environment: environment);
                if (!ignored.Ok) throw new ArgumentException("In-repository output/cache must be ignored.");
            }
            var common = VerificationFiles.PlainPath(Path.GetFullPath((await Git(repo, ["rev-parse", "--git-common-dir"], budget.Token, environment)).Trim(), repo));
            if (VerificationFiles.Intersects(output, common) || VerificationFiles.Intersects(cacheRoot, common))
                throw new ArgumentException("Output/cache intersect Git metadata.");
            var commit = (await Git(repo, ["rev-parse", "--verify", "--end-of-options", request.Ref + "^{commit}"], budget.Token, environment)).Trim();
            var tree = (await Git(repo, ["rev-parse", commit + "^{tree}"], budget.Token, environment)).Trim();
            foreach (var file in new[] { request.Spec, "muthur.project.json" })
            {
                var entry = await Git(repo, ["ls-tree", commit, "--", file], budget.Token, environment);
                if (!(entry.StartsWith("100644 blob ", StringComparison.Ordinal) || entry.StartsWith("100755 blob ", StringComparison.Ordinal)))
                    throw new ArgumentException($"{file} must be a regular committed file.");
            }
            var project = await Git(repo, ["show", commit + ":muthur.project.json"], budget.Token, environment);
            var recipe = VerificationRecipes.Read(project);
            var remaining = TimeSpan.FromMinutes(Math.Min(request.TimeoutMinutes, recipe.TimeoutMinutes)) - (clock.GetUtcNow() - started);
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
            budget.CancelAfter(remaining);
            var checkout = Path.Combine(scratch, "checkout");
            var install = Path.Combine(scratch, "install");
            ownership = new()
            {
                RunId = runId, Output = output, Repository = repo, Scratch = scratch, Checkout = checkout,
                Commit = commit, Runner = Identity(Process.GetCurrentProcess()), Url = url,
                Containment = job is null ? null : VerificationJob.Name(runId),
            };
            SaveOwnership(ownership);
            var frozenSpec = Path.Combine(output, "spec.bin");
            var specArchive = Path.Combine(output, "spec.zip");
            try
            {
                await Git(repo, ["archive", "--format=zip", "--output=" + specArchive, commit, request.Spec], budget.Token, environment);
                using var archive = ZipFile.OpenRead(specArchive);
                var entries = archive.Entries.Where(entry => entry.FullName == request.Spec).ToArray();
                if (entries.Length != 1 || entries[0].Name.Length == 0 ||
                    (entries[0].ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    ((entries[0].ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000))
                    throw new IOException("Spec archive must contain exactly one matching regular file.");
                using var input = entries[0].Open();
                await using var destination = new FileStream(frozenSpec, FileMode.CreateNew);
                await input.CopyToAsync(destination, budget.Token);
            }
            finally { if (File.Exists(specArchive)) File.Delete(specArchive); }
            evidencePath = Path.Combine(output, "evidence.json");
            evidence = new()
            {
                RunId = runId, Task = request.Task, ParentRun = request.ParentRun, Subject = request.Subject,
                Repository = repo, GitDirectory = common, Commit = commit, Tree = tree, SpecPath = request.Spec,
                SpecSha256 = VerificationFiles.HashFile(frozenSpec), Recipe = recipe,
                RecipeSha256 = RecipeDigest(project),
                StartedUtc = clock.GetUtcNow(), CacheStatus = request.NoCache ? "bypassed" : "miss",
            };
            Save(evidence, output);
            await Command(evidence, output, "checkout", new("git", ["worktree", "add", "--detach", checkout, commit]), repo, environment, budget.Token);
            if ((await Git(checkout, ["rev-parse", "HEAD"], budget.Token, environment)).Trim() != commit ||
                (await Git(checkout, ["status", "--porcelain"], budget.Token, environment)).Length != 0)
                throw new IOException("Detached checkout is not the captured clean commit.");
            if (inputProbe is null) await Provenance(evidence, output, checkout, environment, budget.Token);
            else await inputProbe(evidence, budget.Token);
            await Command(evidence, output, "build", recipe.Build, checkout, environment, budget.Token);
            await Command(evidence, output, "test", recipe.Test, checkout, environment, budget.Token);
            cache = new VerificationCache(cacheRoot, clock);
            if (!request.NoCache && evidence.Key is not null)
            {
                var read = await cache.RestoreAsync(evidence.Key, evidence.Inputs, install, budget.Token);
                evidence.CacheStatus = read.Status;
                evidence.CacheReason = read.Reason;
                evidence.CacheProducer = read.Manifest?.RunId;
                Save(evidence, output);
            }
            if (evidence.CacheStatus != "hit")
                await Command(evidence, output, "preparation", Expand(recipe.Preparation, install), checkout, environment, budget.Token);
            foreach (var expected in recipe.Outputs)
                if (!File.Exists(Path.Combine(install, expected)) && !Directory.Exists(Path.Combine(install, expected)))
                    throw new IOException("Missing installation output: " + expected);
            evidence.OutputFiles = VerificationFiles.Inventory(install);
            ownership.ServerLaunchStarted = true;
            SaveOwnership(ownership);
            await Command(evidence, output, "up", Expand(recipe.Up, install), checkout, environment, budget.Token);
            CaptureServer(ownership);
            await Readiness(evidence, output, url + recipe.ReadinessPath, budget.Token);
            await Command(evidence, output, "status", Expand(recipe.Status, install), checkout, environment, budget.Token);
            if (!request.NoCache && evidence.Key is not null && evidence.CacheStatus != "hit") stage = await cache.StageAsync(evidence, install, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
            exit = 0;
        }
        catch (OperationCanceledException) { exit = ct.IsCancellationRequested ? 130 : 124; if (evidence is not null) evidence.Error = exit == 130 ? "Cancelled." : "Global timeout."; }
        catch (VerificationCommandException ex) { exit = ex.ExitCode == 124 ? 124 : 1; if (evidence is not null) evidence.Error = ex.Message; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception)
        {
            exit = evidence is null ? 2 : 1;
            if (evidence is not null) evidence.Error = ex.Message;
            else earlyError = ex.Message;
        }
        finally
        {
            if (ownership is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await Clean(ownership, evidence, environment!, cleanup.Token, job: job);
                    if (evidence is not null) evidence.CleanupSucceeded = true;
                }
                catch (Exception ex)
                {
                    exit = 1;
                    if (evidence is not null) { evidence.CleanupSucceeded = false; evidence.CleanupError = ex.Message; }
                }
            }
            else if (provisionalScratch is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    if (job is not null) await job.StopAsync(cleanup.Token);
                    VerificationFiles.DeleteOwned(provisionalScratch, Path.GetDirectoryName(provisionalScratch)!);
                }
                catch (Exception ex) { exit = 1; earlyError = "Initial cleanup failed: " + ex.Message; }
            }
            if (job is not null)
            {
                try
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await job.StopAsync(stop.Token);
                }
                catch (Exception ex)
                {
                    exit = 1;
                    if (evidence is not null) { evidence.CleanupSucceeded = false; evidence.CleanupError = ex.Message; }
                    else earlyError = ex.Message;
                }
                finally { job.Dispose(); }
            }
        }
        if (evidence is null) return new(runId, exit == 2 ? "invalid" : "failed", evidencePath, "bypassed", exit, earlyError);
        if (exit == 0 && budget.IsCancellationRequested)
        {
            exit = ct.IsCancellationRequested ? 130 : 124;
            evidence.Error = "Run budget expired before successful finalization.";
        }
        evidence.Status = exit switch { 0 => "success", 124 => "timeout", 130 => "cancelled", _ => "failed" };
        evidence.EndedUtc = clock.GetUtcNow();
        try
        {
            if (stage is not null)
            {
                if (exit == 0) await cache!.PublishAsync(stage, evidence, budget.Token);
                else
                {
                    using var discard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await cache!.DiscardAsync(stage, evidence, discard.Token);
                }
            }
        }
        catch (Exception ex)
        {
            exit = 1; evidence.Status = "failed"; evidence.Error = "Cache finalization failed: " + ex.Message;
            if (stage is not null)
            {
                try
                {
                    using var discard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await cache!.DiscardAsync(stage, evidence, discard.Token);
                }
                catch (Exception discard) { evidence.Error += " Discard failed: " + discard.Message; }
            }
        }
        try { Save(evidence, ownership!.Output); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exit = 1; evidence.Status = "failed"; evidence.Error = "Final evidence write failed: " + ex.Message;
            if (stage is not null && evidence.Key is not null)
            {
                try
                {
                    using var revoke = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await cache!.RevokeAsync(evidence, revoke.Token);
                }
                catch (Exception revoke) { evidence.Error += " Cache revocation failed: " + revoke.Message; }
            }
            // If storage remains unwritable, the previous atomic running bundle is still not success.
            try { Save(evidence, ownership!.Output); }
            catch (Exception retry) when (retry is IOException or UnauthorizedAccessException) { }
        }
        return new(runId, evidence.Status, evidencePath, evidence.CacheStatus, exit, evidence.Error ?? evidence.CleanupError);
    }

    private async Task<string> Git(string repo, string[] args, CancellationToken ct, IReadOnlyDictionary<string, string> environment)
    {
        var result = await processes.RunAsync("git", args, repo, timeout: TimeSpan.FromSeconds(30), ct: ct,
            scrubEnvironment: VerificationEnvironment.Scrub(), environment: environment);
        if (!result.Ok) throw new ArgumentException("Git identity/checkout failed: " + result.Message);
        return result.StdOut.TrimEnd('\r', '\n');
    }

    private static string RecipeDigest(string project)
    {
        using var doc = JsonDocument.Parse(project);
        return VerificationFiles.Hash(doc.RootElement.GetProperty("verification").GetRawText());
    }

    private async Task<ProcessResult> Command(VerificationEvidence evidence, string output, string name, VerificationCommand command,
        string cwd, Dictionary<string, string> environment, CancellationToken ct)
    {
        var stage = new VerificationStage
        {
            Name = name, File = command.File, Arguments = command.Arguments,
            WorkingDirectory = Path.GetRelativePath(output, cwd), StartedUtc = clock.GetUtcNow(),
        };
        evidence.Stages.Add(stage);
        Save(evidence, output);
        ProcessResult? result = null;
        VerificationOwnership? commandOwner = null;
        try
        {
            if (beforeStage is not null) await beforeStage(name, ct);
            if (processes is ProcessRunner observed)
            {
                commandOwner = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(output, "ownership.json")), VerificationJsonContext.Default.VerificationOwnership)!;
                commandOwner.CommandRunning = true;
                commandOwner.ActiveCommand = null;
                SaveOwnership(commandOwner);
                result = await observed.RunObservedAsync(command.File, command.Arguments, cwd, ct: ct,
                    scrubEnvironment: VerificationEnvironment.Scrub(), environment: environment, started: process =>
                    {
                        if (name == "test") { evidence.TestsStarted = true; Save(evidence, output); }
                        commandOwner.ActiveCommand = Identity(process);
                        SaveOwnership(commandOwner);
                    });
            }
            else result = await processes.RunAsync(command.File, command.Arguments, cwd, ct: ct,
                    scrubEnvironment: VerificationEnvironment.Scrub(), environment: environment);
            if (name == "test")
            {
                evidence.TestsStarted = result.Started;
                evidence.TestsCompleted = result.Started && result.ExitCode is not (124 or 130);
            }
            stage.ExitCode = result.ExitCode;
            stage.Status = result.Ok ? "success" : "failed";
            if (!result.Ok) throw new VerificationCommandException(name, result.ExitCode);
            return result;
        }
        catch (ProcessCancelledException ex)
        {
            result = ex.Result;
            if (name == "test") { evidence.TestsStarted = result.Started; evidence.TestsCompleted = false; }
            stage.ExitCode = result.ExitCode; stage.Status = "cancelled"; throw;
        }
        catch { stage.Status = "failed"; throw; }
        finally
        {
            if (commandOwner is not null && (result is not null || commandOwner.ActiveCommand is null))
            {
                commandOwner.CommandRunning = false;
                commandOwner.ActiveCommand = null;
                SaveOwnership(commandOwner);
            }
            stage.EndedUtc = clock.GetUtcNow();
            stage.DurationMilliseconds = (stage.EndedUtc.Value - stage.StartedUtc).TotalMilliseconds;
            if (result is not null)
            {
                var prefix = evidence.Stages.Count.ToString("D3") + "-" + name;
                stage.Stdout = prefix + ".stdout.log";
                stage.Stderr = prefix + ".stderr.log";
                File.WriteAllText(Path.Combine(output, stage.Stdout), Redact(result.StdOut));
                File.WriteAllText(Path.Combine(output, stage.Stderr), Redact(result.StdErr));
            }
            Save(evidence, output);
        }
    }

    private async Task Provenance(VerificationEvidence evidence, string output, string checkout,
        Dictionary<string, string> environment, CancellationToken ct)
    {
        var inputs = evidence.Inputs;
        inputs["schema"] = "1";
        inputs["repository"] = evidence.Repository;
        inputs["gitDirectory"] = evidence.GitDirectory;
        inputs["commit"] = evidence.Commit;
        inputs["tree"] = evidence.Tree;
        inputs["spec"] = evidence.SpecSha256;
        inputs["recipe"] = evidence.RecipeSha256;
        inputs["inputs"] = "**";
        inputs["os"] = RuntimeInformation.OSDescription;
        inputs["architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
        inputs["buildConfiguration"] = evidence.Recipe.BuildConfiguration;
        inputs["preparationConfiguration"] = evidence.Recipe.PreparationConfiguration;
        inputs["runner"] = VerificationFiles.HashFile(Environment.ProcessPath ?? throw new IOException("Runner executable unknown."));
        foreach (var (key, value) in VerificationEnvironment.Identity(environment)) inputs[key] = value;
        try
        {
            foreach (var (name, arguments) in new (string, string[])[] { ("dotnet", ["--info"]), ("pwsh", ["--version"]), ("git", ["--version"]) })
            {
                var resolved = ExecutableResolver.Resolve(name) ?? throw new IOException(name + " executable unknown.");
                if (resolved.Prefix.Count != 0) throw new IOException("Toolchain shim identity unknown.");
                inputs[name + "/executable"] = VerificationFiles.HashFile(resolved.FileName);
                var result = await Command(evidence, output, "probe-" + name, new(resolved.FileName, arguments), checkout, environment, ct);
                if (string.IsNullOrWhiteSpace(result.StdOut)) throw new IOException("Empty toolchain probe: " + name);
                inputs[name + "/version"] = VerificationEnvironment.Canonicalize(result.StdOut, environment);
            }
            // Match the installer's vswhere discovery and fingerprint the complete discovered tool trees.
            // A missing prerequisite disables reuse; it never relaxes the build/test/smoke gates.
            var toolDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
            var programFiles = environment.GetValueOrDefault("ProgramFiles(x86)");
            if (programFiles is null) throw new IOException("ProgramFiles(x86) is unknown.");
            var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            inputs["vswhere/executable"] = VerificationFiles.HashFile(vswhere);
            var vs = await Command(evidence, output, "probe-vswhere", new(vswhere,
                ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"]), checkout, environment, ct);
            var vsPath = vs.StdOut.Trim();
            if (!Path.IsPathFullyQualified(vsPath)) throw new IOException("Visual Studio toolchain discovery failed.");
            toolDirectories["VCToolsInstallDir"] = Path.Combine(vsPath, "VC");
            toolDirectories["WindowsSdkDir"] = environment.GetValueOrDefault("WindowsSdkDir") ?? Path.Combine(programFiles, "Windows Kits", "10");
            foreach (var variable in new[] { "VCToolsInstallDir", "WindowsSdkDir" })
            {
                var directory = toolDirectories[variable];
                if (!Directory.Exists(directory))
                    throw new IOException(variable + " is not established; Native AOT toolchain identity incomplete.");
                var identities = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(VerificationFiles.PlainPath(directory), "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    VerificationFiles.PlainPath(file);
                    identities[Path.GetRelativePath(directory, file)] = VerificationFiles.HashFile(file);
                }
                if (identities.Count == 0) throw new IOException("Empty toolchain directory.");
                inputs[variable + "/contents"] = VerificationCache.Key(identities);
            }
            var dotnet = ExecutableResolver.Resolve("dotnet")!.Value.FileName;
            var sdk = Path.Combine(Path.GetDirectoryName(dotnet)!, "sdk");
            var sdkInputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(sdk, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                VerificationFiles.PlainPath(file);
                sdkInputs[Path.GetRelativePath(sdk, file)] = VerificationFiles.HashFile(file);
            }
            if (sdkInputs.Count == 0) throw new IOException("SDK content identity unknown.");
            inputs["sdkContents"] = VerificationCache.Key(sdkInputs);
            if (!environment.TryGetValue("NUGET_PACKAGES", out var packages) || !Directory.Exists(packages))
                throw new IOException("NUGET_PACKAGES must identify an inspectable package store to establish Native AOT compiler identity.");
            var compilerPackages = Directory.EnumerateDirectories(packages).Where(p => Path.GetFileName(p).Contains("ilcompiler", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (compilerPackages.Length == 0) throw new IOException("Native AOT compiler packages are not available for identity probing.");
            var compilerInputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var directory in compilerPackages)
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    VerificationFiles.PlainPath(file);
                    compilerInputs[Path.GetRelativePath(packages, file)] = VerificationFiles.HashFile(file);
                }
            inputs["nativeCompilerContents"] = VerificationCache.Key(compilerInputs);
            evidence.Key = VerificationCache.Key(inputs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VerificationCommandException)
        { evidence.CacheStatus = evidence.CacheStatus == "bypassed" ? "bypassed" : "disabled"; evidence.CacheReason = ex.Message; }
        Save(evidence, output);
    }

    private async Task Readiness(VerificationEvidence evidence, string output, string url, CancellationToken ct)
    {
        var stage = new VerificationStage { Name = "readiness", File = "HTTP GET", Arguments = [url], WorkingDirectory = ".", StartedUtc = clock.GetUtcNow() };
        evidence.Stages.Add(stage);
        Save(evidence, output);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await http.GetAsync(url, deadline.Token);
                    if (response.IsSuccessStatusCode) { stage.Status = "success"; stage.ExitCode = 0; return; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, deadline.Token);
            }
        }
        finally
        {
            if (stage.Status == "running") stage.Status = "failed";
            stage.EndedUtc = clock.GetUtcNow();
            stage.DurationMilliseconds = (stage.EndedUtc.Value - stage.StartedUtc).TotalMilliseconds;
            Save(evidence, output);
        }
    }

    private static VerificationCommand Expand(VerificationCommand command, string install) =>
        new(command.File.Replace("{install}", install, StringComparison.Ordinal),
            command.Arguments.Select(a => a.Replace("{install}", install, StringComparison.Ordinal)).ToArray());

    private static string LoopbackUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static VerificationProcess Identity(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks,
        process.MainModule?.FileName ?? throw new IOException("Process executable identity unavailable."));

    private static Process? Find(VerificationProcess identity)
    {
        Process process;
        try { process = Process.GetProcessById(identity.Pid); }
        catch (ArgumentException) { return null; }
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks)
        { process.Dispose(); return null; }
        if (!string.Equals(process.MainModule?.FileName, identity.Executable, StringComparison.OrdinalIgnoreCase))
        { process.Dispose(); throw new IOException("Process executable identity mismatch."); }
        return process;
    }

    private static void CaptureServer(VerificationOwnership ownership)
    {
        var pidFile = VerificationFiles.PlainPath(Path.Combine(ownership.Scratch, "home", MuthurEnvironment.PidFile));
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile), out var pid))
            throw new IOException("Server PID not available; retain scratch and retry verification cleanup after startup finishes.");
        using var process = Process.GetProcessById(pid);
        var identity = Identity(process);
        var executable = Path.Combine(ownership.Scratch, "install", "server", "Muthur.Server.exe");
        if (!string.Equals(identity.Executable, executable, StringComparison.OrdinalIgnoreCase) || identity.StartUtcTicks < ownership.Runner.StartUtcTicks)
            throw new IOException("Scratch PID does not identify this installation's server.");
        ownership.Server = identity;
        SaveOwnership(ownership);
    }

    private async Task Clean(VerificationOwnership ownership, VerificationEvidence? evidence, Dictionary<string, string> environment, CancellationToken ct, bool recovering = false, VerificationJob? job = null)
    {
        ValidateOwnership(ownership);
        if (ownership.Cleaned) return;
        var journal = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(ownership.Output, "ownership.json")), VerificationJsonContext.Default.VerificationOwnership)!;
        var contained = ownership.Containment is not null;
        if (recovering && contained) await VerificationJob.RecoverAsync(ownership.RunId, ct);
        if (journal.CommandRunning && !contained)
        {
            if (journal.ActiveCommand is null)
                throw new IOException("Interrupted command launch has no persisted PID/start identity; inspect scratch manually before cleanup.");
            using var active = Find(journal.ActiveCommand);
            if (active is not null)
            {
                active.Kill(entireProcessTree: true);
                await active.WaitForExitAsync(ct);
            }
            else if (recovering)
                throw new IOException("Interrupted command root has exited; descendant ownership cannot be established. Inspect scratch manually.");
            ownership.CommandRunning = false;
            ownership.ActiveCommand = null;
            SaveOwnership(ownership);
        }
        if (ownership.ServerLaunchStarted && !(recovering && contained))
        {
            if (ownership.Server is null && File.Exists(Path.Combine(ownership.Scratch, "home", MuthurEnvironment.PidFile))) CaptureServer(ownership);
            if (ownership.Server is null && !contained) throw new IOException("Server identity missing; cannot establish ownership.");
            using var owned = ownership.Server is null ? null : Find(ownership.Server);
            if (owned is null && !recovering && evidence?.Error is null) throw new IOException("Owned server exited before installed down; smoke cleanup did not complete normally.");
            var down = Expand(VerificationRecipes.Muthur.Down, Path.Combine(ownership.Scratch, "install"));
            Exception? downError = null;
            try
            {
                if (owned is not null && evidence is not null) await Command(evidence, ownership.Output, "down", down, ownership.Scratch, environment, ct);
                else if (owned is not null)
                {
                    var result = await processes.RunAsync(down.File, down.Arguments, ownership.Scratch, ct: ct,
                        scrubEnvironment: VerificationEnvironment.Scrub(), environment: environment);
                    if (!result.Ok) throw new IOException("Installed down failed.");
                }
            }
            catch (Exception ex) { downError = ex; }
            using var process = ownership.Server is null ? null : Find(ownership.Server);
            if (process is not null)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct);
            }
            if (downError is not null) throw new IOException("Installed down failed; owned server stopped but retain evidence/scratch.", downError);
        }
        if (job is not null) await job.StopAsync(ct);
        ownership.CommandRunning = false;
        ownership.ActiveCommand = null;
        SaveOwnership(ownership);
        if (Directory.Exists(ownership.Checkout))
        {
            if ((await Git(ownership.Checkout, ["rev-parse", "HEAD"], ct, environment)).Trim() != ownership.Commit)
                throw new IOException("Owned checkout HEAD changed; refuse deletion.");
            await Git(ownership.Repository, ["worktree", "remove", "--force", ownership.Checkout], ct, environment);
        }
        VerificationFiles.DeleteOwned(ownership.Scratch, ownership.Output);
        ownership.Cleaned = true;
        SaveOwnership(ownership);
    }

    private static void ValidateOwnership(VerificationOwnership ownership)
    {
        VerificationFiles.PlainPath(ownership.Output);
        VerificationFiles.PlainPath(ownership.Repository);
        var expected = Path.Combine(ownership.Output, "scratch-" + ownership.RunId.ToString("N"));
        if (ownership.Version != 1 || ownership.Scratch != expected || ownership.Checkout != Path.Combine(expected, "checkout") ||
            ownership.RunId == Guid.Empty || !VerificationFiles.Within(ownership.Scratch, ownership.Output))
            throw new IOException("Malformed ownership; inspect ownership.json manually. No guessed cleanup.");
        if (ownership.Containment is not null && ownership.Containment != VerificationJob.Name(ownership.RunId))
            throw new IOException("Malformed containment identity; no guessed cleanup.");
        VerificationFiles.PlainPath(ownership.Scratch);
        if (!Uri.TryCreate(ownership.Url, UriKind.Absolute, out var url) || url.Host != "127.0.0.1" || url.Scheme != "http" ||
            url.AbsolutePath != "/" || url.Port < 1 || url.UserInfo.Length != 0 || url.Query.Length != 0)
            throw new IOException("Invalid owned loopback URL.");
        if (ownership.Server is not null && !string.Equals(ownership.Server.Executable,
            Path.Combine(expected, "install", "server", "Muthur.Server.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recorded server is outside this run's installation.");
    }

    public async Task<VerificationResult> CleanupAsync(string output, CancellationToken ct = default)
    {
        output = VerificationFiles.PlainPath(output);
        using var gate = new FileStream(Path.Combine(output, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var ownership = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(output, "ownership.json")), VerificationJsonContext.Default.VerificationOwnership)
            ?? throw new IOException("Missing ownership; inspect manually, no guessed cleanup.");
        if (ownership.Output != output) throw new IOException("Ownership root mismatch.");
        ValidateOwnership(ownership);
        using var active = Find(ownership.Runner);
        if (active is not null) throw new IOException("Runner PID/start identity is still active; cleanup refused.");
        var evidencePath = Path.Combine(output, "evidence.json");
        var evidence = JsonSerializer.Deserialize(File.ReadAllText(evidencePath), VerificationJsonContext.Default.VerificationEvidence)
            ?? throw new IOException("Missing evidence; inspect manually.");
        if (evidence.RunId != ownership.RunId || evidence.Commit != ownership.Commit || evidence.Repository != ownership.Repository)
            throw new IOException("Evidence/ownership mismatch.");
        if (!ownership.Cleaned)
        {
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cleanup.CancelAfter(TimeSpan.FromSeconds(45));
            await Clean(ownership, evidence, VerificationEnvironment.Create(ownership.Scratch, ownership.Url), cleanup.Token, recovering: true);
            evidence.CleanupSucceeded = true;
            if (evidence.Status == "running") evidence.Status = "interrupted";
            evidence.EndedUtc = clock.GetUtcNow();
            Save(evidence, output);
        }
        else if (evidence.Status == "running")
        {
            evidence.Status = "interrupted";
            evidence.CleanupSucceeded = true;
            evidence.EndedUtc = clock.GetUtcNow();
            Save(evidence, output);
        }
        return new(ownership.RunId, evidence.Status, evidencePath, evidence.CacheStatus, 0);
    }

    private static void SaveOwnership(VerificationOwnership ownership) => VerificationFiles.Atomic(
        Path.Combine(ownership.Output, "ownership.json"), ownership, VerificationJsonContext.Default.VerificationOwnership);
    private static void Save(VerificationEvidence evidence, string output) => VerificationFiles.Atomic(
        Path.Combine(output, "evidence.json"), evidence, VerificationJsonContext.Default.VerificationEvidence);
    private static string Redact(string value) => Regex.Replace(value,
        @"(?i)(bearer\s+|(?:token|password|secret|api[_-]?key)\s*[=:]\s*)[^\s""']+", "$1[REDACTED]", RegexOptions.CultureInvariant);

    private sealed class VerificationCommandException(string name, int exitCode) : Exception($"Verification stage '{name}' exited {exitCode}.")
    {
        public int ExitCode { get; } = exitCode;
    }
}
