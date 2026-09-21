using System.Text.RegularExpressions;
using Muthur.Core;
using Muthur.Launch;

namespace Muthur.Server.Services;

internal sealed partial class TaskUnitGit(IProcessRunner runner, string repo, CancellationToken ct)
{
    [GeneratedRegex("\\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\\z")]
    private static partial Regex ObjectIdPattern();

    internal static string ObjectId(string? value)
    {
        if (value is null || !ObjectIdPattern().IsMatch(value))
            throw Fail.Rule("invalid_unit_object", "A full hexadecimal Git object ID is required.");
        return value.ToLowerInvariant();
    }

    internal static void PathName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500 || value.StartsWith('/') ||
            value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl) ||
            value.Split('/').Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw Fail.Rule("invalid_unit_path", "Use a canonical relative Git file path without traversal.");
    }

    internal async Task BranchName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 250 || value.StartsWith('-') || value == "HEAD" ||
            !(await Run("check-ref-format", "refs/heads/" + value)).Ok)
            throw Fail.Rule("invalid_unit_branch", "Use a named local branch.");
    }

    internal async Task<string> Head(string branch)
    {
        await BranchName(branch);
        var result = await Run("rev-parse", "--verify", "refs/heads/" + branch + "^{commit}");
        if (!result.Ok) throw Fail.Rule("unit_artifact_missing", "The local branch is missing; recover artifacts or redispatch.");
        return ObjectId(result.StdOut.Trim());
    }

    internal async Task Commit(string commit)
    {
        ObjectId(commit);
        var result = await Run("cat-file", "-t", commit);
        if (!result.Ok || result.StdOut.Trim() != "commit")
            throw Fail.Rule("unit_artifact_missing", "The pinned commit is missing; recover artifacts or redispatch.");
    }

    internal async Task<bool> Ancestor(string ancestor, string descendant)
    {
        await Commit(ancestor);
        await Commit(descendant);
        var result = await Run("merge-base", "--is-ancestor", ancestor, descendant);
        if (result.ExitCode is not (0 or 1)) throw Fail.Rule("unit_git_failed", "Git ancestry could not be established.");
        return result.Ok;
    }

    internal async Task<string> FileBlob(string commit, string path)
    {
        await Commit(commit);
        PathName(path);
        // Probe each component, with literal pathspecs: a symlink in any position is not evidence.
        var parts = path.Split('/');
        string blob = "";
        for (var i = 0; i < parts.Length; i++)
        {
            var prefix = string.Join('/', parts.Take(i + 1));
            var result = await Run("--literal-pathspecs", "ls-tree", "-z", commit, "--", prefix);
            var entry = result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var fields = entry.Length == 1 ? entry[0].Split('\t')[0].Split(' ') : [];
            var regular = i == parts.Length - 1;
            if (!result.Ok || fields.Length != 3 || (regular ? fields[0] is not ("100644" or "100755") || fields[1] != "blob" : fields[0] != "040000" || fields[1] != "tree"))
                throw Fail.Rule("unit_artifact_missing", "A pinned regular committed file is missing; recover artifacts or redispatch.");
            blob = ObjectId(fields[2]);
        }
        var present = await Run("cat-file", "-t", blob);
        if (!present.Ok || present.StdOut.Trim() != "blob")
            throw Fail.Rule("unit_artifact_missing", "The pinned file blob is missing; recover artifacts or redispatch.");
        return blob;
    }

    private Task<ProcessResult> Run(params string[] args) => runner.RunAsync("git", args, repo,
        timeout: TimeSpan.FromSeconds(10), ct: ct,
        environment: new Dictionary<string, string> { ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_OPTIONAL_LOCKS"] = "0" });
}
