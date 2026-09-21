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

public enum PushOutcome
{
    /// <summary>The default branch reached the remote.</summary>
    Pushed,
    /// <summary>Nothing to do: no remote, no branch, or the remote already has this commit.</summary>
    Skipped,
    /// <summary>The remote refused it or could not be reached. Nothing was lost; a later pass tries again.</summary>
    Refused,
}

public sealed record PushResult(PushOutcome Outcome, string? Commit = null, string? Message = null);

public sealed record GitCommitInspection(string Sha, string TreeSha, IReadOnlyList<string> Parents);

public interface ITaskLander
{
    Task<GitCommitInspection?> InspectCommitAsync(Project project, string sha, CancellationToken ct = default) =>
        Task.FromResult<GitCommitInspection?>(null);
    Task<bool> IsAncestorAsync(Project project, string ancestor, string descendant, CancellationToken ct = default) =>
        Task.FromResult(false);
    /// <summary>The commit at the tip of <paramref name="branch"/>, or null when the branch does not exist.</summary>
    Task<string?> BranchHeadAsync(Project project, string branch, CancellationToken ct = default);

    /// <summary>The file's contents at the tip of <paramref name="branch"/>, or null when either is absent.</summary>
    Task<string?> ReadFileAsync(Project project, string branch, string path, CancellationToken ct = default);
    Task<LandResult> LandAsync(Project project, WorkTask task, string ownerName, CancellationToken ct = default);

    /// <summary>
    /// Sends the project's default branch to `origin`, fast-forward only. A project with no remote, or one whose
    /// remote already has the commit, is <see cref="PushOutcome.Skipped"/> — not a failure and not worth a word.
    /// </summary>
    Task<PushResult> PushDefaultBranchAsync(Project project, CancellationToken ct = default);
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

    private static bool IsObjectId(string sha) => sha is { Length: 40 or 64 } && sha.All(Uri.IsHexDigit);

    public async Task<GitCommitInspection?> InspectCommitAsync(Project project, string sha, CancellationToken ct = default)
    {
        if (!IsObjectId(sha)) return null;
        var result = await GitAsync(project.RepoPath, ct, "show", "--no-patch", "--format=%H%n%T%n%P", sha, "--");
        var output = result.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = (output.EndsWith('\n') ? output[..^1] : output).Split('\n');
        if (!result.Ok || lines.Length != 3 || lines[0].Trim() != sha) return null;
        return new GitCommitInspection(lines[0].Trim(), lines[1].Trim(),
            lines[2].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public async Task<bool> IsAncestorAsync(Project project, string ancestor, string descendant, CancellationToken ct = default) =>
        IsObjectId(ancestor) && IsObjectId(descendant)
        && (await GitAsync(project.RepoPath, ct, "merge-base", "--is-ancestor", ancestor, descendant)).Ok;

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
        if (task.CurrentSubject is not { } subject)
            return LandResult.Refuse("validation_provenance_unknown", Validations.Recovery);
        if (!Directory.Exists(repo))
            return LandResult.Refuse("repo_missing", $"Repository '{repo}' does not exist.");
        var head = await BranchHeadAsync(project, branch, ct);
        if (head is null)
            return LandResult.Refuse("branch_missing", $"Branch '{branch}' does not exist in {repo}.");
        if (head != subject.ImplementationSha)
            return LandResult.Refuse("implementation_changed", "The task branch no longer names the approved implementation. " + Validations.Recovery);

        return project.LandMode == LandMode.Pr
            ? await OpenPullRequestAsync(project, task, ownerName, ct)
            : await MergeAsync(project, task, ownerName, ct);
    }

    public async Task<PushResult> PushDefaultBranchAsync(Project project, CancellationToken ct = default)
    {
        var repo = project.RepoPath;
        var target = project.DefaultBranch;
        if (!Directory.Exists(repo))
            return new PushResult(PushOutcome.Skipped);
        if (!(await GitAsync(repo, ct, "remote", "get-url", "origin")).Ok)
            return new PushResult(PushOutcome.Skipped);

        var head = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{target}");
        if (!head.Ok)
            return new PushResult(PushOutcome.Skipped);
        var local = head.StdOut.Trim();

        var remote = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{target}");
        if (remote.Ok && remote.StdOut.Trim() == local)
            return new PushResult(PushOutcome.Skipped, Commit: local);

        var push = await processes.RunAsync("git",
            ["-c", "credential.interactive=false", "push", "origin", $"refs/heads/{target}:refs/heads/{target}"],
            project.RepoPath, timeout: GitTimeout, ct: ct,
            environment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
        return push.Ok
            ? new PushResult(PushOutcome.Pushed, Commit: local)
            : new PushResult(PushOutcome.Refused, Commit: local, Message: push.Message);
    }

    private async Task<LandResult> MergeAsync(Project project, WorkTask task, string ownerName, CancellationToken ct)
    {
        var repo = project.RepoPath;
        var target = project.DefaultBranch;
        var candidate = task.CurrentIntegrationCandidate;
        if (candidate is not { State: "passed", CandidateSha: not null, TreeSha: not null, PromotionIntentAt: not null }
            || candidate.SubjectId != task.CurrentSubjectId || candidate.EvidenceSha256 is null)
            return LandResult.Refuse("integration_required", "A current passing integration candidate and durable promotion intent are required.");
        var inspection = await InspectCommitAsync(project, candidate.CandidateSha, ct);
        if (inspection is null || inspection.TreeSha != candidate.TreeSha
            || (!candidate.AlreadyIncluded && !inspection.Parents.SequenceEqual(new[] { candidate.TargetSha, candidate.ImplementationSha }))
            || (candidate.AlreadyIncluded && (candidate.CandidateSha != candidate.TargetSha || !await IsAncestorAsync(project, candidate.ImplementationSha, candidate.TargetSha, ct))))
            return LandResult.Refuse("integration_candidate_invalid", "The tested candidate no longer has its recorded structure.");
        var targetSha = await BranchHeadAsync(project, target, ct);
        if (targetSha is null) return LandResult.Refuse("default_branch_missing", $"Default branch '{target}' is missing.");
        if (targetSha != candidate.TargetSha && await IsAncestorAsync(project, candidate.CandidateSha, targetSha, ct))
            return new LandResult(LandOutcome.Landed, Commit: candidate.CandidateSha); // Recover an exact durable intent, never an implementation-only ancestor.
        if (targetSha != candidate.TargetSha) return LandResult.Refuse("integration_target_changed", "The default branch moved; test a new integration candidate.");
        var inventory = await GitAsync(repo, ct, "worktree", "list", "--porcelain");
        if (!inventory.Ok) return LandResult.Refuse("git_failed", inventory.Message);
        string? checkout = null;
        foreach (var line in inventory.StdOut.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) checkout = line[9..];
            if (line == $"branch refs/heads/{target}")
                return LandResult.Refuse("integration_target_checked_out", $"'{target}' is checked out at {checkout}. Its owner must switch to another branch or detached HEAD before exact promotion.");
        }
        var update = await GitAsync(repo, ct, "update-ref", "-m", $"muthur: promote {Wire.TaskId(task.Id)}", $"refs/heads/{target}", candidate.CandidateSha, candidate.TargetSha);
        return update.Ok ? new LandResult(LandOutcome.Landed, Commit: candidate.CandidateSha)
            : LandResult.Refuse("integration_target_changed", "Compare-and-swap refused promotion: " + update.Message);
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
        var tree = await GitAsync(repo, ct, "merge-tree", "--write-tree", "--name-only", targetSha, branch);
        var lines = tree.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tree.ExitCode == 1)
        {
            var files = SplitFiles(string.Join('\n', lines.Skip(1).TakeWhile(l => !l.Contains(' '))));
            return new LandResult(LandOutcome.Conflict, Code: "merge_conflict", Message: ConflictMessage(branch, target, files),
                Files: files, LandedSince: await LandedSinceAsync(repo, branch, targetSha, ct));
        }
        if (!tree.Ok || lines.Length == 0) return LandResult.Refuse("git_failed", tree.Message);

        var commit = await GitAsync(repo, ct, "commit-tree", lines[0], "-p", targetSha, "-p", branch, "-m", message);
        if (!commit.Ok) return LandResult.Refuse("git_failed", commit.Message);

        var sha = commit.StdOut.Trim();
        // Compare-and-swap on the old value: if the branch moved meanwhile, fail rather than lose commits.
        var update = await GitAsync(repo, ct, "update-ref", "-m", $"muthur: land {branch}", $"refs/heads/{target}", sha, targetSha);
        return update.Ok ? new LandResult(LandOutcome.Landed, Commit: sha) : LandResult.Refuse("git_failed", update.Message);
    }

    private async Task<LandResult> OpenPullRequestAsync(Project project, WorkTask task, string ownerName, CancellationToken ct)
    {
        var subject = task.CurrentSubject!;
        var branch = $"muthur-approved/{Wire.TaskId(task.Id)}/{subject.Id:D}";
        var reference = $"refs/heads/{branch}";
        var existing = await BranchHeadAsync(project, branch, ct);
        if (existing is not null && existing != subject.ImplementationSha)
            return LandResult.Refuse("approved_ref_changed", "The round-specific approved ref changed. " + Validations.Recovery);
        if (existing is null)
        {
            var pin = await GitAsync(project.RepoPath, ct, "update-ref", reference, subject.ImplementationSha, new string('0', subject.ImplementationSha.Length));
            if (!pin.Ok) return LandResult.Refuse("git_failed", pin.Message);
        }
        var push = await GitAsync(project.RepoPath, ct, "push", "origin", $"{subject.ImplementationSha}:{reference}");
        if (!push.Ok) return LandResult.Refuse("push_failed", push.Message);

        var body = $"{task.Body}\n\n---\nTask {Wire.TaskId(task.Id)} · owner `{ownerName}`" +
                   (task.SpecPath is null ? "" : $" · spec `{task.SpecPath}`") +
                   $"\nValidation subject `{subject.Id:D}` · implementation `{subject.ImplementationSha}` · spec SHA-256 `{subject.SpecSha256}`." +
                   "\nValidated in MUTHUR; opened by the hub. A human merges." +
                   "\nIntegration status: delegated_external_checks_unknown. External CI must test the exact current base/head merge result and enforce freshness.";
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
