using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>What one pass did. Errors are messages already safe to show the founder.</summary>
public sealed record PushPass(int Attempted, int Pushed, IReadOnlyList<string> Errors);

public sealed class PushService(Ledger ledger, ITaskLander lander, TimeProvider clock, ILogger<PushService> logger)
{
    /// <summary>How long a project that failed to push is left alone before the next attempt.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _pushing = new(1, 1);

    public async Task<PushPass> PushAsync(CancellationToken ct = default)
    {
        await _pushing.WaitAsync(ct);
        try
        {
            var projects = await ledger.ReadAsync((db, _) =>
                db.Projects.Where(p => p.LandMode == LandMode.Merge).OrderBy(p => p.Key).ToListAsync(ct), ct);
            var now = clock.GetUtcNow();
            var attempted = 0;
            var pushed = 0;
            var errors = new List<string>();
            foreach (var project in projects)
            {
                if (project.LastPushError is not null && project.LastPushAttemptAt is { } last && now - last < RetryAfter)
                    continue;

                attempted++;
                var result = await lander.PushDefaultBranchAsync(project, ct);
                if (result.Outcome == PushOutcome.Skipped && project.LastPushError is null) continue;

                await ledger.MutateAsync(Caller.System, async m =>
                {
                    var p = await m.Db.Projects.SingleAsync(p => p.Id == project.Id, ct);
                    p.LastPushAttemptAt = m.Now;
                    if (result.Outcome == PushOutcome.Pushed)
                    {
                        if (p.LastPushedCommit != result.Commit)
                            m.Record("project.pushed", payload: new { project = p.Key, branch = p.DefaultBranch, commit = result.Commit });
                        p.LastPushedCommit = result.Commit;
                        p.LastPushAt = m.Now;
                    }
                    if (result.Outcome is PushOutcome.Pushed or PushOutcome.Skipped && p.LastPushError is not null)
                    {
                        m.Record("project.push_recovered", payload: new { project = p.Key, branch = p.DefaultBranch });
                        MessageService.PostFromHub(m, Recipient.Founder, null,
                            $"'{p.Key}' is reaching the remote again: '{p.DefaultBranch}' is pushed.", taskId: null);
                        p.LastPushError = null;
                    }
                    if (result.Outcome == PushOutcome.Refused)
                    {
                        var error = result.Message;
                        if (p.LastPushError is null)
                        {
                            m.Record("project.push_failing", payload: new { project = p.Key, branch = p.DefaultBranch, error });
                            MessageService.PostFromHub(m, Recipient.Founder, null,
                                $"Landed work on '{p.Key}' is not reaching the remote. Pushing '{p.DefaultBranch}' from {p.RepoPath} failed: {error} The merge is safe on the local branch and the hub keeps trying; nothing reaches anyone else until it goes through.", taskId: null);
                        }
                        p.LastPushError = error;
                    }
                }, ct);

                if (result.Outcome == PushOutcome.Pushed)
                {
                    pushed++;
                    logger.LogInformation("Pushed {Project} branch {Branch} at {Commit}.", project.Key, project.DefaultBranch, result.Commit);
                }
                if (result.Outcome == PushOutcome.Refused)
                {
                    errors.Add($"{project.Key}: {result.Message}");
                    logger.LogWarning("Push of {Project} failed: {Message}", project.Key, result.Message);
                }
            }
            return new PushPass(attempted, pushed, errors);
        }
        finally
        {
            _pushing.Release();
        }
    }
}

/// <summary>Sends each merge-mode project's default branch to its remote. The first pass is the catch-up for whatever landed while the hub was off.</summary>
public sealed class PushWorker(IServiceProvider services, TimeProvider clock, ILogger<PushWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await services.GetRequiredService<PushService>().PushAsync(stoppingToken);
                if (result.Pushed > 0) logger.LogInformation("Pushed {Count} project(s).", result.Pushed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Push pass failed.");
            }
            await Task.Delay(Interval, clock, stoppingToken);
        }
    }
}
