using Muthur.Contracts;

namespace Muthur.Server.Services;

/// <param name="Probe">Whether checks may touch the network. False is the cheap read of what the hub already knows.</param>
public sealed record DoctorContext(bool Probe, DateTimeOffset Now);

/// <summary>One aspect of the hub's health. Never throws: a check that cannot run returns a fail that says why.</summary>
public interface IDoctorCheck
{
    Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default);
}

/// <summary>
/// Asks every check whether the hub can do its job. Mutates nothing and records no ledger event.
/// <para>
/// One budget for the whole run, not one per check: T-14 gave each probe its own timeout, and six checks
/// each allowed a minute is still six minutes of a request nobody can cancel. The ceiling has to be on the
/// thing the founder is actually waiting for. A check that does not answer inside it is reported rather than
/// waited on, because a doctor that hangs is worse than one that says it could not tell you — from the
/// outside those look the same, and only one of them ends.
/// </para>
/// </summary>
public sealed class DoctorService(IEnumerable<IDoctorCheck> checks, TimeProvider clock, MuthurOptions options)
{
    // A hub that cannot write its own log is read before anything that depends on reading it.
    private static readonly string[] Order = ["secret", "logging", "ingest", "outbound", "project", "repo", "role"];

    public async Task<DoctorDto> RunAsync(bool probe, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var context = new DoctorContext(probe, now);
        var seconds = options.DoctorBudgetSeconds;

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(seconds), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        // One delay for the run, completing when the budget runs out or the caller gives up. Raced against
        // each check rather than merely passed to it, so a check that ignores its token cannot hold the hub —
        // and a caller that walks away is not left waiting on a check that was never going to look.
        var expired = Task.Delay(Timeout.InfiniteTimeSpan, clock, bounded.Token);

        var found = new List<CheckDto>();
        foreach (var check in checks)
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested)
            {
                found.Add(Unanswered(check, $"This check was not reached: the {seconds}s doctor budget was spent before it ran."));
                continue;
            }
            try
            {
                var running = check.RunAsync(context, bounded.Token);
                if (await Task.WhenAny(running, expired) == running)
                {
                    found.AddRange(await running);
                }
                else
                {
                    Discard(running);   // it is still going; its result and its faults are nobody's business now
                    ct.ThrowIfCancellationRequested();   // the caller gave up, which is not a timeout
                    found.Add(Unanswered(check, $"This check did not answer within the {seconds}s doctor budget."));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // the caller gave up, which is not a timeout and not this report's business
            }
            catch (OperationCanceledException)
            {
                found.Add(Unanswered(check, $"This check did not answer within the {seconds}s doctor budget."));
            }
            catch (Exception ex)
            {
                // A check that breaks reports itself rather than taking the whole report down.
                found.Add(new CheckDto("doctor", check.GetType().Name, CheckStatus.Fail, $"This check itself failed: {ex.Message}"));
            }
        }
        Discard(expired);

        var sorted = found
            .OrderBy(c => Rank(c.Category))
            .ThenBy(c => c.Subject, StringComparer.Ordinal)
            .ThenBy(c => c.Detail, StringComparer.Ordinal)
            .ToList();

        return new DoctorDto(
            now,
            probe,
            sorted.Count(c => c.Status == CheckStatus.Ok),
            sorted.Count(c => c.Status == CheckStatus.Warn),
            sorted.Count(c => c.Status == CheckStatus.Fail),
            sorted);
    }

    /// <summary>A category nobody planned for sorts after the ones that were, rather than before them.</summary>
    private static int Rank(string category) => Array.IndexOf(Order, category) switch { < 0 => Order.Length, var i => i };

    /// <summary>
    /// A check the budget ran out on. 'warn', never 'fail': not answering is not evidence the hub is broken,
    /// and reporting it as a failure would make an overloaded machine look like a misconfigured one.
    /// </summary>
    private static CheckDto Unanswered(IDoctorCheck check, string detail) =>
        new("doctor", check.GetType().Name, CheckStatus.Warn, detail);

    /// <summary>Keeps an abandoned task's eventual fault from surfacing later as an unobserved exception.</summary>
    private static void Discard(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
