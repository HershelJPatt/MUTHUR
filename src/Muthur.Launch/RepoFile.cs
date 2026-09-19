namespace Muthur.Launch;

/// <summary>
/// The two rules that govern a file named by path — it stays inside the repository, and it matches a commit.
/// They live here because this is the one assembly every client of them references, and a rule one client
/// enforces and another does not is the laxer of two doors onto the same operation.
/// </summary>
public static class RepoFile
{
    /// <summary>
    /// How long any one git probe gets. Not a parameter: a timeout a caller can pass is a rule a caller can weaken.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>A separator at the boundary is what keeps '/repo-evil' from counting as inside '/repo'.</summary>
    public static bool IsInside(string root, string full)
    {
        // A root this machine will not accept contains nothing, and false is the refusing answer.
        if (Full(root) is not { } rooted) return false;
        var trimmed = rooted.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Length > trimmed.Length
            && full.StartsWith(trimmed, comparison)
            && (full[trimmed.Length] == Path.DirectorySeparatorChar || full[trimmed.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>True when git cannot name a commit containing this file: modified, staged, or never added.</summary>
    public static async Task<bool> IsDirtyAsync(IProcessRunner processes, string path, CancellationToken ct = default)
    {
        if (Full(path) is not { } file) return false;
        // Outside a repository there is no commit to disagree with, and provenance never gates a role. Inside
        // one, both probes are needed: status misses a file that was never added, and --untracked-files=no is
        // what stops bin/ and obj/ failing every run. --literal-pathspecs so a name containing * [ ? is a name.
        if (await GitAsync(processes, file, ct, "rev-parse", "--is-inside-work-tree") is not "true") return false;
        var name = Path.GetFileName(file);
        return await GitAsync(processes, file, ct, "--literal-pathspecs", "status", "--porcelain", "--untracked-files=no", "--", name) is { Length: > 0 }
            || await GitAsync(processes, file, ct, "--literal-pathspecs", "ls-files", "--error-unmatch", "--", name) is null;
    }

    /// <summary>The ref and short commit of the repository holding <paramref name="path"/>, or null when it is not in one.</summary>
    public static async Task<(string Ref, string Commit)?> DescribeAsync(IProcessRunner processes, string path, CancellationToken ct = default)
    {
        if (Full(path) is not { } file) return null;
        return await GitAsync(processes, file, ct, "rev-parse", "--abbrev-ref", "HEAD") is { Length: > 0 } reference
            && await GitAsync(processes, file, ct, "rev-parse", "--short", "HEAD") is { Length: > 0 } commit
                ? (reference, commit)
                : null;
    }

    /// <summary>
    /// git, run in the file's own directory; null on any failure — no git, no repository, an unreadable path.
    /// For <see cref="DescribeAsync"/> that reads as "not in a repository", because provenance is evidence and
    /// evidence that cannot be gathered stops nothing. A cancelled token is the exception: it propagates rather
    /// than becoming an answer nobody asked for.
    /// </summary>
    private static async Task<string?> GitAsync(IProcessRunner processes, string file, CancellationToken ct, params string[] arguments)
    {
        if (Path.GetDirectoryName(file) is not { Length: > 0 } directory || !Directory.Exists(directory)) return null;
        try
        {
            var result = await processes.RunAsync("git", arguments, directory, timeout: Budget, ct: ct);
            return result.Ok ? result.StdOut.Trim() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
