using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Launch;

namespace Muthur.Server.Services;

/// <summary>Whether each project's checkout is somewhere MUTHUR could actually build and land.</summary>
public sealed class DoctorRepoCheck(Ledger ledger, IProcessRunner processes) : IDoctorCheck
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
    {
        var projects = await ledger.ReadAsync(async (db, _) => await db.Projects.OrderBy(p => p.Key).ToListAsync(ct), ct);

        var checks = new List<CheckDto>();
        foreach (var project in projects) checks.Add(await InspectAsync(project, ct));
        return checks;
    }

    private async Task<CheckDto> InspectAsync(Project project, CancellationToken ct)
    {
        var repo = project.RepoPath;
        var target = project.DefaultBranch;

        if (!Directory.Exists(repo))
            return Check(CheckStatus.Fail, $"{repo} does not exist; nothing in this project can be built or landed.");

        if (!(await GitAsync(repo, ct, "rev-parse", "--git-dir")).Ok)
            return Check(CheckStatus.Fail, $"{repo} is not a git repository.");

        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{target}")).Ok)
            return Check(CheckStatus.Fail, $"Default branch '{target}' does not exist; MUTHUR has nothing to land onto.");

        var worktree = await GitAsync(repo, ct, "status", "--porcelain", "--untracked-files=no");
        if (worktree.StdOut.Trim().Length > 0)
            return Check(CheckStatus.Warn, "Working tree is dirty; MUTHUR refuses to merge in a checkout with uncommitted changes.");

        var head = (await GitAsync(repo, ct, "rev-parse", "--abbrev-ref", "HEAD")).StdOut.Trim();
        return head == target
            ? Check(CheckStatus.Ok, $"Clean checkout of '{target}'.")
            : Check(CheckStatus.Warn, $"Checked out on '{head}', not '{target}'. Normal while a task is in flight.");

        CheckDto Check(CheckStatus status, string detail) => new("repo", project.Key, status, detail);
    }

    private Task<ProcessResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] arguments) =>
        processes.RunAsync("git", arguments, workingDirectory, timeout: GitTimeout, ct: ct);
}
