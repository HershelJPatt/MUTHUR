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

    /// <summary>True when git cannot name a commit containing this file: modified, staged, or never added.</summary>
    public static bool IsDirty(string path)
    {
        if (Full(path) is not { } file) return false;
        // Outside a repository there is no commit to disagree with, and provenance never gates a role. Inside
        // one, both probes are needed: status misses a file that was never added, and --untracked-files=no is
        // what stops bin/ and obj/ failing every run. --literal-pathspecs so a name containing * [ ? is a name.
        if (Git(file, "rev-parse", "--is-inside-work-tree") is not "true") return false;
        var name = Path.GetFileName(file);
        return Git(file, "--literal-pathspecs", "status", "--porcelain", "--untracked-files=no", "--", name) is { Length: > 0 }
            || Git(file, "--literal-pathspecs", "ls-files", "--error-unmatch", "--", name) is null;
    }

    /// <summary>
    /// git, run in the file's own directory; null on any failure — no git, no repository, an unreadable path.
    /// For <see cref="Describe"/> that reads as "not in a repository", because provenance is evidence and
    /// evidence that cannot be gathered stops nothing.
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
