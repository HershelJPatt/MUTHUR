using Muthur.Core;
using Muthur.Launch;

namespace Muthur.Server.Services;

/// <summary>
/// A role brief the founder named by path, read under the rules every client of <c>role define</c> obeys.
/// The console is not a laxer second door to that operation, so both rules are here: the path stays inside a
/// repository the hub was configured with, and the file matches a commit. Containment is not ceremony — this
/// control reads the founder's disk from a browser tab, and a path free to climb out of a checkout with
/// <c>..</c> would let that tab read anything on the machine.
/// </summary>
public sealed class BriefFileReader(IProcessRunner processes)
{
    /// <summary>The brief's markdown, or a <see cref="MuthurException"/> naming which of the rules refused it.</summary>
    public async Task<string> ReadAsync(IReadOnlyList<string> repositories, string path, CancellationToken ct = default)
    {
        // Every message below names the relative path the founder typed, never the absolute one: absolute
        // paths are not this page's business, and a browser tab that reports them is a directory listing.
        if (path.Trim() is not { Length: > 0 } relative)
            throw Fail.Rule("brief_file_required", "A role needs a brief: the path of its markdown file, from the repository root.");

        if (repositories.Count == 0)
            throw Fail.Rule("no_repository", "No project exists yet, so there is no repository to read a brief from. muthur project add <key> --repo <path> --founder");

        // A path that escapes a repository answers 'missing' and not an 'outside' code of its own: '../../etc'
        // answered differently from a typo would tell whoever is typing which absolute paths exist, and the
        // message says the rule anyway, for the founder who typed a real '..' by accident.
        if (Match(repositories, relative) is not { } full)
            throw Fail.Rule("brief_file_missing", $"No file at '{relative}' in {string.Join(" or ", repositories)}. The path is read from the repository root and stays inside it.");

        if (await RepoFile.IsDirtyAsync(processes, full, ct))
            throw Fail.Rule("brief_file_dirty", $"'{relative}' has no committed version, so the brief you install would match no commit. Commit it, or pass the text you mean to 'muthur role define'.");

        return await File.ReadAllTextAsync(full, ct);
    }

    /// <summary>
    /// The first repository holding <paramref name="relative"/> — and the search stops there, which is a rule
    /// and not an implementation detail. A dirty file in the first repository is refused; it never falls
    /// through to a clean file of the same name in the second. Falling through would mean a founder with two
    /// projects sometimes installs a brief from a repository they were not looking at, chosen by which copy
    /// happened to be committed, and it would make the dirty refusal skippable by leaving a stale copy
    /// elsewhere.
    /// </summary>
    private static string? Match(IReadOnlyList<string> repositories, string relative)
    {
        foreach (var root in repositories)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, relative));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // A root, or a name, this machine will not accept resolves to nothing, so this root matches nothing.
                continue;
            }

            if (RepoFile.IsInside(root, full) && File.Exists(full)) return full;
        }

        return null;
    }
}
