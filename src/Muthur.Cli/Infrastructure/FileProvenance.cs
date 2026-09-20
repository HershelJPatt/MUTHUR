using Muthur.Launch;

namespace Muthur.Cli.Infrastructure;

/// <summary>Where a file the founder passed on the command line actually came from, since a repository's working tree moves under them.</summary>
public static class FileProvenance
{
    private static readonly IProcessRunner Processes = new ProcessRunner();

    /// <summary>The ref and short commit of the repository holding <paramref name="path"/>, or null when it is not in one.</summary>
    public static (string Ref, string Commit)? Describe(string path) =>
        RepoFile.DescribeAsync(Processes, path).GetAwaiter().GetResult();

    /// <summary>True when git cannot name a commit containing this file: modified, staged, or never added.</summary>
    public static bool IsDirty(string path) =>
        RepoFile.IsDirtyAsync(Processes, path).GetAwaiter().GetResult();
}
