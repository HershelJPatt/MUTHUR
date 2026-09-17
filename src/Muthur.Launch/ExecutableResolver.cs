namespace Muthur.Launch;

/// <summary>
/// Finds a CLI on PATH the way a shell would. On Windows, npm-installed CLIs are .cmd shims, which
/// CreateProcess cannot start directly, so those are routed through cmd.exe.
/// </summary>
public static class ExecutableResolver
{
    public static (string FileName, IReadOnlyList<string> Prefix)? Resolve(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? Wrap(name) : null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), name + extension.ToLowerInvariant());
                if (File.Exists(candidate)) return Wrap(candidate);
            }
        }
        return null;
    }

    private static (string, IReadOnlyList<string>) Wrap(string path) =>
        OperatingSystem.IsWindows() && Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat"
            ? ("cmd.exe", ["/d", "/c", path])
            : (path, []);
}
