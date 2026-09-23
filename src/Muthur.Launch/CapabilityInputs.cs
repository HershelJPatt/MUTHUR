using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Muthur.Launch;

internal static class CapabilityExecutable
{
    internal static (string FileName, IReadOnlyList<string> Prefix)? Resolve(string name,
        Func<string, (string FileName, IReadOnlyList<string> Prefix)?> resolver)
    {
        var resolved = resolver(name);
        if (resolved is { } command && OperatingSystem.IsWindows() && command.FileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(Environment.SystemDirectory, "cmd.exe"), command.Prefix);
        return resolved;
    }
}

internal static class CapabilityInputs
{
    internal static readonly string[] ProjectFiles = ["muthur.project.json", "global.json", "NuGet.Config", "nuget.config",
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", ".claude/settings.json",
        ".claude/settings.local.json", ".codex/config.toml"];
    private static readonly string[] EnvironmentKeys = ["PATH", "PATHEXT", "USERPROFILE", "HOME", "APPDATA", "CODEX_HOME", "CLAUDE_CONFIG_DIR",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "MSBuildSDKsPath", "NUGET_PACKAGES", "NUGET_CONFIG_FILE", "TEMP", "TMP"];

    internal static async Task<(string Common, string Hash)?> ReadAsync(IProcessRunner runner, WorkerRequest request,
        Func<string, (string FileName, IReadOnlyList<string> Prefix)?> resolve, CancellationToken ct)
    {
        async Task<string> Run(string executable, IReadOnlyList<string> args)
        {
            var result = await runner.RunAsync(executable, args, request.WorkingDirectory, timeout: CapabilityBudget.Step, ct: ct,
                scrubEnvironment: ["MUTHUR_AGENT", "MUTHUR_TOKEN"], environment: request.GitEnvironment);
            if (!result.Ok || result.StdOut.Length > 1_048_576) throw new IOException("Identity input unavailable.");
            return result.StdOut.TrimEnd('\r', '\n');
        }
        var head = await Run("git", ["rev-parse", "HEAD"]);
        if (head != request.Capabilities!.BaseCommit) return null;
        if (await CapabilityHostGit.CheckFiltersAsync(runner, request.WorkingDirectory, request.GitEnvironment, ct) is { } refusal)
            throw new CapabilityFilterException(refusal.Code, refusal.Detail);
        // Inspect never checks out another revision. A checkout whose effective inputs differ from the
        // pinned tree cannot predict the newly allocated worker and is deliberately unknown.
        if ((await Run("git", CapabilityHostGit.Arguments(["diff", "--no-ext-diff", "--no-textconv", "--name-only", head, "--", .. ProjectFiles]))).Length != 0) return null;
        if ((await Run("git", CapabilityHostGit.Arguments(["ls-files", "--others", "--exclude-standard", "--", .. ProjectFiles]))).Length != 0) return null;
        foreach (var file in ProjectFiles)
        {
            try { _ = File.GetAttributes(Path.Combine(request.WorkingDirectory, file)); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((await Run("git", CapabilityHostGit.Arguments(["ls-files", "--", file]))).Length == 0) return null;
        }
        var common = Path.GetFullPath(Path.Combine(request.WorkingDirectory, await Run("git", ["rev-parse", "--git-common-dir"])));
        var parts = new List<string> { "capability-inputs-v2", RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString() };
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            parts.Add(identity.User?.Value ?? throw new IOException("Effective SID unavailable."));
        }
        else
        {
            var uid = await Run("id", ["-u"]);
            if (!uint.TryParse(uid, out _)) return null;
            parts.Add(uid);
        }
        foreach (var key in EnvironmentKeys)
        { parts.Add(key); parts.Add(CapabilityHash.Of(Environment.GetEnvironmentVariable(key) ?? "{unset}")); }
        foreach (var tool in new[] { "dotnet", "pwsh", "git" })
        {
            var resolved = CapabilityExecutable.Resolve(tool, resolve);
            if (resolved is null) return null;
            parts.Add(tool);
            parts.Add(Path.GetFullPath(resolved.Value.FileName));
            parts.AddRange(resolved.Value.Prefix);
            var version = await Run(resolved.Value.FileName, [.. resolved.Value.Prefix, "--version"]);
            if (string.IsNullOrWhiteSpace(version) || version.Length > 256) return null;
            parts.Add(CapabilityHash.Of(version));
        }
        var config = await Run("git", ["config", "--null", "--list"]);
        var entries = config.Split('\0').ToList();
        var trust = "safe.directory\n" + Path.GetFullPath(request.WorkingDirectory).Replace('\\', '/');
        var index = entries.FindLastIndex(e => e == trust);
        if (index < 0) return null;
        entries[index] = "safe.directory\n{allocated-worktree}";
        parts.Add(CapabilityHash.Of(string.Join('\0', entries)));
        var paths = ProjectFiles.Select(f => ("project:" + f, Path.Combine(request.WorkingDirectory, f))).ToList();
        var profile = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nuget = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config")
            : Path.Combine(profile, ".nuget", "NuGet", "NuGet.Config");
        paths.Add((nuget, nuget));
        if (Environment.GetEnvironmentVariable("NUGET_CONFIG_FILE") is { Length: > 0 } configuredNuget)
            paths.Add((configuredNuget, Path.GetFullPath(configuredNuget, request.WorkingDirectory)));
        // Existing ancestor settings retain their literal path and content. No user path is normalized.
        for (var parent = Directory.GetParent(request.WorkingDirectory); parent is not null; parent = parent.Parent)
            foreach (var file in ProjectFiles)
            {
                var path = Path.Combine(parent.FullName, file);
                try { _ = File.GetAttributes(path); paths.Add((path, path)); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        var files = CapabilitySettings.HashFiles(paths);
        if (files is null) return null;
        parts.Add(files);
        return (common, CapabilityHash.Of(string.Join('\n', parts)));
    }
}
