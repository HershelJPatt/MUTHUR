using Muthur.Launch;

namespace Muthur.Cli.Infrastructure;

/// <summary>Where a file the founder passed on the command line actually came from, since a repository's working tree moves under them.</summary>
public static class FileProvenance
{
    private static readonly IProcessRunner Processes = new ProcessRunner();
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>The ref and short commit of the repository holding <paramref name="path"/>, or null when it is not in one.</summary>
    public static (string Ref, string Commit)? Describe(string path)
    {
        if (Full(path) is not { } file) return null;
        return Git(file, "rev-parse", "--abbrev-ref", "HEAD") is { Length: > 0 } reference
            && Git(file, "rev-parse", "--short", "HEAD") is { Length: > 0 } commit
                ? (reference, commit)
                : null;
    }

    /// <summary>True when that one file has uncommitted changes.</summary>
    public static bool IsDirty(string path) =>
        Full(path) is { } file
        && Git(file, "status", "--porcelain", "--untracked-files=no", "--", Path.GetFileName(file)) is { Length: > 0 };

    /// <summary>
    /// git, run in the file's own directory. Any failure — no git, no repository, an unreadable path — reads
    /// as "not in a repository": provenance is evidence, and evidence that cannot be gathered stops nothing.
    /// </summary>
    private static string? Git(string file, params string[] arguments)
    {
        if (Path.GetDirectoryName(file) is not { Length: > 0 } directory || !Directory.Exists(directory)) return null;
        try
        {
            var result = Processes.RunAsync("git", arguments, directory, timeout: Budget).GetAwaiter().GetResult();
            return result.Ok ? result.StdOut.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The absolute path, or null when it is not one this machine would accept at all.</summary>
    private static string? Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
