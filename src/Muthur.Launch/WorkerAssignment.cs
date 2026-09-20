namespace Muthur.Launch;

public sealed class WorkerDispatchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Immutable Git provenance checked before the first model call.</summary>
public sealed record WorkerAssignment(string Repository, string BaseBranch, string BaseCommit, string DefaultBranch,
    string SpecPath, string SpecBlob, string WorkerBranch, string Worktree)
{
    public static async Task<WorkerAssignment> ResolveAsync(IProcessRunner processes, string repository, string baseBranch,
        string defaultBranch, string specPath, string workerBranch, string worktree, CancellationToken ct = default)
    {
        async Task<string?> Git(params string[] args)
        {
            var result = await processes.RunAsync("git", args, repository, timeout: TimeSpan.FromMinutes(1), ct: ct);
            return result.Ok ? result.StdOut.Trim() : null;
        }
        if (baseBranch == "HEAD") baseBranch = await Git("symbolic-ref", "--quiet", "--short", "HEAD") ?? "";
        baseBranch = LocalName(baseBranch);
        defaultBranch = LocalName(defaultBranch);
        foreach (var (name, kind) in new[] { (baseBranch, "base"), (defaultBranch, "default"), (workerBranch, "worker") })
            if (string.IsNullOrWhiteSpace(name) || await Git("check-ref-format", "refs/heads/" + name) is null)
                throw new WorkerDispatchException("invalid_assignment", $"The {kind} must be a named local branch.");
        var baseCommit = await Git("rev-parse", "--verify", $"refs/heads/{baseBranch}^{{commit}}")
            ?? throw new WorkerDispatchException("invalid_base", $"Local base branch '{baseBranch}' does not resolve to a commit.");
        if (await Git("rev-parse", "--verify", $"refs/heads/{defaultBranch}^{{commit}}") is null)
            throw new WorkerDispatchException("invalid_default_branch", $"Local default branch '{defaultBranch}' does not exist.");
        specPath = specPath.Replace('\\', '/');
        if (Path.IsPathRooted(specPath) || specPath.Split('/').Any(p => p is ".." or "." or "") || specPath.Contains(':'))
            throw new WorkerDispatchException("invalid_spec_path", "The spec must be a repository-relative committed file.");
        var specObject = $"{baseCommit}:{specPath}";
        if (await Git("cat-file", "-t", specObject) != "blob")
            throw new WorkerDispatchException("spec_not_committed", $"'{specPath}' is not a committed file on '{baseBranch}'.");
        var treeEntry = await Git("--literal-pathspecs", "ls-tree", baseCommit, "--", specPath);
        if (treeEntry is null || !(treeEntry.StartsWith("100644 ", StringComparison.Ordinal) || treeEntry.StartsWith("100755 ", StringComparison.Ordinal)))
            throw new WorkerDispatchException("invalid_spec_path", "The frozen spec must be a regular committed file, not a symbolic link.");
        var blob = await Git("rev-parse", "--verify", specObject)
            ?? throw new WorkerDispatchException("spec_not_committed", "The committed spec could not be resolved.");
        return new(Path.GetFullPath(repository), baseBranch, baseCommit, defaultBranch, specPath, blob,
            workerBranch, Path.GetFullPath(worktree));
    }

    public async Task VerifyCreatedAsync(IProcessRunner processes, CancellationToken ct = default)
    {
        async Task<string?> Git(params string[] args)
        {
            var result = await processes.RunAsync("git", args, Worktree, timeout: TimeSpan.FromMinutes(1), ct: ct);
            return result.Ok ? result.StdOut.Trim() : null;
        }
        var actual = await Git("rev-parse", "--show-toplevel");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (actual is null || !Path.GetFullPath(actual).Equals(Worktree, comparison) ||
            await Git("branch", "--show-current") != WorkerBranch || await Git("rev-parse", "HEAD") != BaseCommit ||
            await Git("status", "--porcelain") != "")
            throw new WorkerDispatchException("worktree_mismatch", "The created worktree does not match the clean dispatched branch and commit.");
        if (await Git("rev-parse", "--verify", $"refs/heads/{BaseBranch}^{{commit}}") != BaseCommit)
            throw new WorkerDispatchException("base_moved", $"Base '{BaseBranch}' moved after dispatch preparation. Redispatch against the updated spec.");
    }

    private static string LocalName(string branch) => branch.StartsWith("refs/heads/", StringComparison.Ordinal)
        ? branch["refs/heads/".Length..] : branch;
}
