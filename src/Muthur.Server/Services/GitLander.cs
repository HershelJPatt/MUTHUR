using System.Text.RegularExpressions;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Launch;

namespace Muthur.Server.Services;

public enum LandOutcome
{
    Landed,
    /// <summary>The branch no longer merges cleanly: the task must go back to its owner.</summary>
    Conflict,
    /// <summary>The environment prevented landing (dirty checkout, push refused, gh missing). The task stays validated.</summary>
    Refused,
}

public sealed record LandResult(
    LandOutcome Outcome,
    string? Commit = null,
    string? PrUrl = null,
    string? Code = null,
    string? Message = null,
    /// <summary>Files that conflicted, on a <see cref="LandOutcome.Conflict"/> result. Uncapped.</summary>
    IReadOnlyList<string>? Files = null,
    /// <summary>Task ids landed on the target since this branch diverged — the candidates it collided with. Best effort.</summary>
    IReadOnlyList<string>? LandedSince = null)
{
    public static LandResult Refuse(string code, string message) => new(LandOutcome.Refused, Code: code, Message: message);
}

public interface ITaskLander
{
    /// <summary>The commit at the tip of <paramref name="branch"/>, or null when the branch does not exist.</summary>
    Task<string?> BranchHeadAsync(Project project, string branch, CancellationToken ct = default);

    /// <summary>The file's contents at the tip of <paramref name="branch"/>, or null when either is absent.</summary>
    Task<string?> ReadFileAsync(Project project, string branch, string path, CancellationToken ct = default);
    Task<LandResult> LandAsync(Project project, WorkTask task, string ownerName, CancellationToken ct = default);
}

public interface IPullRequestOpener
{
    /// <summary>Returns the pull request URL, or throws <see cref="InvalidOperationException"/> with the reason.</summary>
    Task<string> OpenAsync(string repoPath, string baseBranch, string headBranch, string title, string body, CancellationToken ct = default);
}

/// <summary>
/// The only code in the organization that writes to a project's default branch. Agents ask; the hub,
/// having checked the task is validated, performs the merge (or opens the pull request) itself.
/// </summary>
public sealed partial class GitLander(IProcessRunner processes, IPullRequestOpener pullRequests) : ITaskLander
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The merge subject <see cref="MergeAsync"/> writes, which is how a land is recognised in the target's history.</summary>
    [GeneratedRegex(@"^Land (T-\d+):")]
    private static partial Regex LandedSubject();

    public async Task<string?> BranchHeadAsync(Project project, string branch, CancellationToken ct = default)
    {
        var result = await GitAsync(project.RepoPath, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        var head = result.StdOut.Trim();
        return result.Ok && head.Length > 0 ? head : null;
    }

    /// <summary>
    /// A file as a branch has it, without checking anything out. "Not there" — no such branch, or no such path
    /// on it — is null rather than a failure: the caller has other places to look, and git cannot tell the two
    /// apart in its exit code anyway.
    /// </summary>
    public async Task<string?> ReadFileAsync(Project project, string branch, string path, CancellationToken ct = default)
    {
        // Forward slashes whatever the platform: the argument is a path inside a git tree, not a path on disk.
        var result = await GitAsync(project.RepoPath, ct, "show", $"{branch}:{path.Replace('\\', '/')}");
        return result.Ok ? result.StdOut : null;
    }

    public async Task<LandResult> LandAsync(Project project, WorkTask task, string ownerName, CancellationToken ct = default)
    {
        var repo = project.RepoPath;
        var branch = task.Branch!;
        if (!Directory.Exists(repo))
            return LandResult.Refuse("repo_missing", $"Repository '{repo}' does not exist.");
        if (await BranchHeadAsync(project, branch, ct) is null)
            return LandResult.Refuse("branch_missing", $"Branch '{branch}' does not exist in {repo}.");

        return project.LandMode == LandMode.Pr
            ? await OpenPullRequestAsync(project, task, ownerName, ct)
            : await MergeAsync(project, task, ownerName, ct);
    }

    private async Task<LandResult> MergeAsync(Project project, WorkTask task, string ownerName, CancellationToken ct)
    {
        var repo = project.RepoPath;
        var target = project.DefaultBranch;
        var branch = task.Branch!;

        var targetSha = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{target}");
        if (!targetSha.Ok)
            return LandResult.Refuse("default_branch_missing", $"Default branch '{target}' does not exist in {repo}.");

        if ((await GitAsync(repo, ct, "merge-base", "--is-ancestor", branch, target)).Ok)
            return new LandResult(LandOutcome.Landed, Commit: targetSha.StdOut.Trim()); // already merged

        var message = $"Land {Wire.TaskId(task.Id)}: {task.Title}\n\nLanded by MUTHUR for {ownerName}.";
        return await FindCheckoutAsync(repo, target, ct) is { } checkout
            ? await MergeInCheckoutAsync(checkout, target, branch, message, ct)
            : await MergeWithoutCheckoutAsync(repo, target, branch, targetSha.StdOut.Trim(), message, ct);
    }

    /// <summary>The default branch is checked out somewhere: merge there so its working tree stays in sync.</summary>
    private async Task<LandResult> MergeInCheckoutAsync(string checkout, string target, string branch, string message, CancellationToken ct)
    {
        var status = await GitAsync(checkout, ct, "status", "--porcelain", "--untracked-files=no");
        if (!status.Ok) return LandResult.Refuse("git_failed", status.Message);
        if (status.StdOut.Trim().Length > 0)
            return LandResult.Refuse("dirty_checkout", $"'{target}' is checked out at {checkout} with uncommitted changes. Commit or stash them, then land again.");

        var merge = await GitAsync(checkout, ct, "merge", "--no-ff", "-m", message, branch);
        if (!merge.Ok)
        {
            var conflicted = await GitAsync(checkout, ct, "diff", "--name-only", "--diff-filter=U");
            await GitAsync(checkout, ct, "merge", "--abort");
            var files = SplitFiles(conflicted.StdOut);
            return new LandResult(LandOutcome.Conflict, Code: "merge_conflict", Message: ConflictMessage(branch, target, files),
                Files: files, LandedSince: await LandedSinceAsync(checkout, branch, target, ct));
        }
        var head = await GitAsync(checkout, ct, "rev-parse", "HEAD");
        return new LandResult(LandOutcome.Landed, Commit: head.StdOut.Trim());
    }

    /// <summary>Nobody has the default branch checked out: build the merge commit with plumbing and move the ref.</summary>
    private async Task<LandResult> MergeWithoutCheckoutAsync(string repo, string target, string branch, string targetSha, string message, CancellationToken ct)
    {
        var tree = await GitAsync(repo, ct, "merge-tree", "--write-tree", "--name-only", target, branch);
        var lines = tree.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tree.ExitCode == 1)
        {
            var files = SplitFiles(string.Join('\n', lines.Skip(1).TakeWhile(l => !l.Contains(' '))));
            return new LandResult(LandOutcome.Conflict, Code: "merge_conflict", Message: ConflictMessage(branch, target, files),
                Files: files, LandedSince: await LandedSinceAsync(repo, branch, target, ct));
        }
        if (!tree.Ok || lines.Length == 0) return LandResult.Refuse("git_failed", tree.Message);

        var branchSha = (await GitAsync(repo, ct, "rev-parse", branch)).StdOut.Trim();
        var commit = await GitAsync(repo, ct, "commit-tree", lines[0], "-p", targetSha, "-p", branchSha, "-m", message);
        if (!commit.Ok) return LandResult.Refuse("git_failed", commit.Message);

        var sha = commit.StdOut.Trim();
        // Compare-and-swap on the old value: if the branch moved meanwhile, fail rather than lose commits.
        var update = await GitAsync(repo, ct, "update-ref", "-m", $"muthur: land {branch}", $"refs/heads/{target}", sha, targetSha);
        return update.Ok ? new LandResult(LandOutcome.Landed, Commit: sha) : LandResult.Refuse("git_failed", update.Message);
    }

    private async Task<LandResult> OpenPullRequestAsync(Project project, WorkTask task, string ownerName, CancellationToken ct)
    {
        var branch = task.Branch!;
        var push = await GitAsync(project.RepoPath, ct, "push", "--set-upstream", "origin", branch);
        if (!push.Ok) return LandResult.Refuse("push_failed", push.Message);

        var body = $"{task.Body}\n\n---\nTask {Wire.TaskId(task.Id)} · owner `{ownerName}`" +
                   (task.SpecPath is null ? "" : $" · spec `{task.SpecPath}`") +
                   "\nValidated in MUTHUR; opened by the hub. A human merges.";
        try
        {
            var url = await pullRequests.OpenAsync(project.RepoPath, project.DefaultBranch, branch, $"{Wire.TaskId(task.Id)}: {task.Title}", body, ct);
            return new LandResult(LandOutcome.Landed, PrUrl: url);
        }
        catch (InvalidOperationException ex)
        {
            return LandResult.Refuse("pr_failed", ex.Message);
        }
    }

    /// <summary>Path of the worktree that has <paramref name="branch"/> checked out, if any.</summary>
    private async Task<string?> FindCheckoutAsync(string repo, string branch, CancellationToken ct)
    {
        var list = await GitAsync(repo, ct, "worktree", "list", "--porcelain");
        string? current = null;
        foreach (var line in list.StdOut.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) current = line["worktree ".Length..];
            else if (line == $"branch refs/heads/{branch}") return current;
        }
        return null;
    }

    private static IReadOnlyList<string> SplitFiles(string files) =>
        files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The prose agents read on a bounced land. Its list stays capped; only <see cref="LandResult.Files"/> is uncapped.</summary>
    private static string ConflictMessage(string branch, string target, IReadOnlyList<string> files)
    {
        var list = string.Join(", ", files.Take(12));
        return $"'{branch}' no longer merges cleanly into '{target}'" + (list.Length > 0 ? $" (conflicts: {list})" : "") +
               ". Rebase or merge the default branch into the task branch, re-verify, and mark it implemented again.";
    }

    /// <summary>
    /// Task ids landed on <paramref name="target"/> since <paramref name="branch"/> diverged — what this branch collided
    /// with. Evidence, not a contract: if git cannot answer, the answer is empty and the land reports the conflict anyway.
    /// </summary>
    private async Task<IReadOnlyList<string>> LandedSinceAsync(string workingDirectory, string branch, string target, CancellationToken ct)
    {
        var mergeBase = await GitAsync(workingDirectory, ct, "merge-base", branch, target);
        if (!mergeBase.Ok) return [];
        var log = await GitAsync(workingDirectory, ct, "log", "--format=%s", $"{mergeBase.StdOut.Trim()}..{target}");
        if (!log.Ok) return [];

        return [.. log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(subject => LandedSubject().Match(subject))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)];
    }

    private Task<ProcessResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] arguments) =>
        processes.RunAsync("git", arguments, workingDirectory, timeout: GitTimeout, ct: ct);
}

/// <summary>Opens pull requests with the GitHub CLI. Other forges get their own implementation.</summary>
public sealed class GhPullRequestOpener(IProcessRunner processes) : IPullRequestOpener
{
    public async Task<string> OpenAsync(string repoPath, string baseBranch, string headBranch, string title, string body, CancellationToken ct = default)
    {
        var result = await processes.RunAsync("gh",
            ["pr", "create", "--base", baseBranch, "--head", headBranch, "--title", title, "--body", body],
            repoPath, timeout: TimeSpan.FromMinutes(2), ct: ct);

        // On success gh prints the URL; when a PR for the branch already exists it prints that URL in the error.
        var url = (result.StdOut + "\n" + result.StdErr)
            .Split(['\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(part => part.StartsWith("https://", StringComparison.Ordinal) && part.Contains("/pull/"));
        if (url is not null) return url;
        throw new InvalidOperationException($"gh pr create failed: {result.Message}");
    }
}
