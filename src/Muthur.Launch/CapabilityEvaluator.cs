using System.Text.Json;

namespace Muthur.Launch;

public sealed class CapabilityEvaluator(IProcessRunner processes, TimeProvider? clock = null,
    Func<string, (string FileName, IReadOnlyList<string> Prefix)?>? resolve = null)
{
    public async Task<CapabilityInspection> InspectAsync(IHarnessAdapter? adapter, WorkerRequest request, CancellationToken ct = default)
    {
        var context = request.Capabilities ?? throw new ArgumentException("Capability context is required.", nameof(request));
        CapabilityInspection Unknown(string reason) => new(null, context.Requirements, [],
            new(context.Requirements.Count == 0, context.Requirements.Select(k => new CapabilityMissing(k, CapabilityStates.Unknown, reason)).ToArray()), reason);
        if (context.LaunchPath is not ("worker-run" or "conductor-validator" or "conductor-orchestrator" or "native-subagent"))
            return Unknown("Unknown launch path.");
        if (adapter is null || adapter.CapabilityExecutable is not { } executableName)
            return Unknown("Harness adapter has no capability identity implementation.");
        if (context.LaunchPath != "worker-run")
            return Unknown("Exact conductor/native execution identity prediction is unsupported; no other launch path can satisfy it.");
        try
        {
            request = request with { GitEnvironment = request.RequireRepository ? SessionWorkspace.GitEnvironment(request.WorkingDirectory) : null };
            if (string.IsNullOrWhiteSpace(context.BaseCommit) || string.IsNullOrWhiteSpace(context.RepositoryRoot))
                return Unknown("Repository and pinned base commit are required identity inputs.");
            var executable = CapabilityExecutable.Resolve(executableName, resolve ?? ExecutableResolver.Resolve);
            if (executable is null) return Unknown("Harness executable cannot be resolved.");
            var version = await processes.RunAsync(executable.Value.FileName, [.. executable.Value.Prefix, "--version"],
                request.WorkingDirectory, timeout: CapabilityBudget.Step, ct: ct,
                scrubEnvironment: ["MUTHUR_AGENT", "MUTHUR_TOKEN"], environment: request.GitEnvironment);
            if (!version.Ok || string.IsNullOrWhiteSpace(version.StdOut) || version.StdOut.Length > 256)
                return Unknown("Harness version discovery failed or exceeded the detail limit.");
            var settings = adapter.CapabilitySettings(request);
            if (settings is null) return Unknown("Harness settings identity is unreadable.");
            var inputs = await CapabilityInputs.ReadAsync(processes, request, resolve ?? ExecutableResolver.Resolve, ct);
            if (inputs is null) return Unknown("Pinned project, effective principal, Git or toolchain identity cannot be predicted exactly from this checkout.");
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartArray();
                json.WriteStringValue("capability-config-v2");
                json.WriteStringValue(inputs.Value.Hash);
                json.WriteStringValue(Path.GetFullPath(executable.Value.FileName));
                foreach (var prefix in executable.Value.Prefix) json.WriteStringValue(prefix);
                json.WriteStringValue(context.LaunchPath);
                json.WriteStringValue(request.Model);
                json.WriteStringValue(request.ReasoningEffort);
                json.WriteBooleanValue(request.RequireRepository);
                json.WriteStringValue(request.GitCommonDirectory);
                foreach (var list in new[] { request.AllowedCommands, request.DeniedCommands })
                {
                    json.WriteStartArray();
                    foreach (var command in list.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) json.WriteStringValue(command);
                    json.WriteEndArray();
                }
                json.WriteStartObject();
                var gitEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
                    if (pair.Key is string key && key.StartsWith("GIT_", StringComparison.Ordinal) && pair.Value is string value)
                        gitEnvironment[key] = value;
                foreach (var pair in request.GitEnvironment ?? new Dictionary<string, string>()) gitEnvironment[pair.Key] = pair.Value;
                var count = request.GitEnvironment?.GetValueOrDefault("GIT_CONFIG_COUNT");
                var appended = int.TryParse(count, out var number) && number > 0 ? number - 1 : -1;
                foreach (var pair in gitEnvironment.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var isAllocatedTrust = pair.Key == $"GIT_CONFIG_VALUE_{appended}" &&
                        request.GitEnvironment?.GetValueOrDefault($"GIT_CONFIG_KEY_{appended}") == "safe.directory" &&
                        pair.Value == Path.GetFullPath(request.WorkingDirectory).Replace('\\', '/');
                    json.WriteString(pair.Key, isAllocatedTrust ? "{allocated-worktree}" : pair.Value);
                }
                json.WriteEndObject();
                json.WriteStringValue(settings);
                json.WriteEndArray();
            }
            var identity = new CapabilityIdentity(Environment.MachineName, adapter.Name, version.StdOut.Trim(), context.LaunchPath,
                CapabilityHash.Of(System.Text.Encoding.UTF8.GetString(stream.ToArray())),
                Path.TrimEndingDirectorySeparator(inputs.Value.Common), context.BaseCommit);
            var store = new CapabilityStore(context.CacheDirectory, clock);
            var (observations, diagnostic) = store.Read(identity);
            return new(identity, context.Requirements, observations, store.Match(identity, context.Requirements, observations, diagnostic), diagnostic);
        }
        catch (CapabilityFilterException ex)
        {
            return Unknown(ex.Code + ": " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return Unknown("Capability identity input is missing or unreadable.");
        }
    }

    public static string Explain(CapabilityMatch match) => "capability_mismatch: " +
        string.Join("; ", match.Missing.Select(m => $"{m.Capability}={m.State}: {m.Reason}"));
}

public static class CapabilitySettings
{
    public static string? HashFiles(IEnumerable<(string Scope, string Path)> paths)
    {
        try
        {
            var parts = new List<string>();
            foreach (var (scope, path) in paths.OrderBy(p => p.Scope, StringComparer.Ordinal))
            {
                parts.Add(scope);
                // Absence is an explicit part of the effective configuration; an unreadable existing file is unknown.
                try
                {
                    using var stream = File.OpenRead(path);
                    if (stream.Length > 1_048_576) return null;
                    var bytes = new byte[1_048_577];
                    var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                    if (count > 1_048_576) return null;
                    parts.Add(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, count))));
                }
                catch (FileNotFoundException) { parts.Add("absent"); }
                catch (DirectoryNotFoundException) { parts.Add("absent"); }
            }
            return CapabilityHash.Of(string.Join('\n', parts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
