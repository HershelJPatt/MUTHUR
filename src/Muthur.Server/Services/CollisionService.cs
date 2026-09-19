using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Launch;

namespace Muthur.Server.Services;

/// <summary>Two in-flight tasks whose branches no longer merge into each other.</summary>
public sealed record Collision(string TaskA, string TaskB, IReadOnlyList<string> Files);

/// <summary>
/// Which in-flight tasks are on a collision course, decided by attempting the merge rather than by guessing
/// from filenames. Measured on this repository: of 28 pairs in flight, 11 shared a file and none of them
/// conflicted — so only a real merge failure is worth showing, and a shared path is worth nothing.
/// <para>
/// Every caller is a dashboard render, so the bounds are the design rather than a precaution: one computation
/// per <see cref="CacheWindow"/> however many panels ask, a fixed number of branches, a budget for the whole
/// pass, a timeout on each git call, and no failure a render can see.
/// </para>
/// </summary>
// The spec's signature also took MuthurOptions; every bound here is a constant, so it was unread and CS9113
// (warnings are errors) rejected it. Nothing about this service is deployment-specific yet.
public sealed class CollisionService(ITaskLander lander, IProcessRunner processes, Ledger ledger, TimeProvider clock)
{
    /// <summary>How long an answer stands. A burst of ledger events costs at most one pass a minute.</summary>
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(60);

    /// <summary>The whole pass. Whatever has been computed when it runs out is what gets shown.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>One git call. Short, because the answer is advisory and the caller is a web page.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>66 pairs. Tasks past the twelfth by id are not considered, so the cost has a ceiling.</summary>
    private const int MaxBranches = 12;

    private readonly SemaphoreSlim _pass = new(1, 1);

    /// <summary>One reference, so a reader outside the semaphore can never see a list and a time that disagree.</summary>
    private Snapshot? _snapshot;

    private sealed record Snapshot(DateTimeOffset At, IReadOnlyList<Collision> Collisions);

    /// <summary>One in-flight branch worth comparing, with the project whose repository holds it.</summary>
    private sealed record Candidate(int Id, string Branch, Project Project);

    /// <summary>Pairs of in-flight tasks that would conflict, recomputed at most once per cache window.</summary>
    public async Task<IReadOnlyList<Collision>> CurrentAsync(CancellationToken ct = default)
    {
        if (Fresh() is { } cached) return cached.Collisions;

        // A caller that arrives while a pass is running takes the last answer instead of queueing behind it:
        // two panels rendering at once must not be able to start two passes, or wait on one.
        if (!await TryEnterAsync(ct)) return Volatile.Read(ref _snapshot)?.Collisions ?? [];
        try
        {
            if (Fresh() is { } computed) return computed.Collisions;
            var found = await ComputeAsync(ct);
            Volatile.Write(ref _snapshot, new Snapshot(clock.GetUtcNow(), found));
            return found;
        }
        catch
        {
            // Nothing here has anything to unwind and the caller is a render, so every failure — including a
            // cancellation — becomes the previous answer, or none. The panel never shows an error.
            // Stamped anyway: a repeatable failure must cost one attempt a minute, not one per render.
            var previous = Volatile.Read(ref _snapshot)?.Collisions ?? [];
            Volatile.Write(ref _snapshot, new Snapshot(clock.GetUtcNow(), previous));
            return previous;
        }
        finally
        {
            _pass.Release();
        }
    }

    private Snapshot? Fresh() =>
        Volatile.Read(ref _snapshot) is { } snapshot && clock.GetUtcNow() - snapshot.At < CacheWindow ? snapshot : null;

    private async Task<bool> TryEnterAsync(CancellationToken ct)
    {
        try { return await _pass.WaitAsync(0, ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<IReadOnlyList<Collision>> ComputeAsync(CancellationToken ct)
    {
        var candidates = await ledger.ReadAsync(async (db, _) =>
        {
            // Validated counts, and counts most: a validated task is queued to land, so two conflicting ones
            // are not a future risk but a bounce about to happen. Blocked stays out — it waits on a human and
            // rarely has a branch yet.
            var tasks = await db.Tasks.Include(t => t.Project)
                .Where(t => (t.State == TaskState.InProgress || t.State == TaskState.Validating || t.State == TaskState.Validated)
                    && t.Branch != null && t.Branch != "")
                .OrderBy(t => t.Id)
                .Take(MaxBranches)
                .ToListAsync(ct);
            return tasks.Where(t => t.Project is not null).Select(t => new Candidate(t.Id, t.Branch!, t.Project!)).ToList();
        }, ct);
        if (candidates.Count < 2) return [];

        using var budget = new CancellationTokenSource(Budget, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        var exists = new Dictionary<(Guid Project, string Branch), bool>();
        var found = new List<Collision>();
        try
        {
            // Only within a project: branches of two repositories have nothing to say to each other.
            foreach (var group in candidates.GroupBy(c => c.Project.Id))
            {
                var inProject = group.ToList();
                if (inProject.Count < 2 || !Directory.Exists(inProject[0].Project.RepoPath)) continue;

                for (var i = 0; i < inProject.Count; i++)
                {
                    for (var j = i + 1; j < inProject.Count; j++)
                    {
                        if (bounded.IsCancellationRequested) return found;
                        var (a, b) = (inProject[i], inProject[j]);
                        if (a.Branch == b.Branch) continue;
                        if (!await ExistsAsync(a, exists, bounded.Token) || !await ExistsAsync(b, exists, bounded.Token)) continue;

                        if (await ConflictAsync(a.Project.RepoPath, a.Branch, b.Branch, bounded.Token) is { } files)
                            found.Add(new Collision(Wire.TaskId(a.Id), Wire.TaskId(b.Id), files));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The budget ran out part-way through. Showing what was computed is what the budget is for.
        }
        return found;
    }

    /// <summary>A branch that has gone is not a collision. Asked once per branch, not once per pair.</summary>
    private async Task<bool> ExistsAsync(Candidate candidate, Dictionary<(Guid, string), bool> exists, CancellationToken ct)
    {
        var key = (candidate.Project.Id, candidate.Branch);
        if (exists.TryGetValue(key, out var known)) return known;
        return exists[key] = await lander.BranchHeadAsync(candidate.Project, candidate.Branch, ct) is not null;
    }

    /// <summary>The conflicting paths when the two branches do not merge; null when they do, or when git could not say.</summary>
    private async Task<IReadOnlyList<string>?> ConflictAsync(string repo, string branchA, string branchB, CancellationToken ct)
    {
        var tree = await processes.RunAsync("git", ["merge-tree", "--write-tree", "--name-only", branchA, branchB],
            repo, timeout: GitTimeout, ct: ct);

        // git merge-tree exits 0 clean and 1 on conflicts; any other code is git refusing to answer at all —
        // a missing ref, unrelated histories — and 124 and 127 are this runner's own timeout and start failure.
        // Only 1 is evidence of a conflict, and an indicator that fires on anything else would be worse than none.
        if (tree.ExitCode != 1) return null;

        // "<tree oid>\n<conflicted paths>\n\n<informational messages>"; the messages are prose, so they have spaces.
        var lines = tree.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Skip(1).TakeWhile(line => !line.Contains(' ')).ToList();
    }
}
