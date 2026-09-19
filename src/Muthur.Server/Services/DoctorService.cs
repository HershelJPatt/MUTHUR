using Muthur.Contracts;

namespace Muthur.Server.Services;

/// <param name="Probe">Whether checks may touch the network. False is the cheap read of what the hub already knows.</param>
public sealed record DoctorContext(bool Probe, DateTimeOffset Now);

/// <summary>One aspect of the hub's health. Never throws: a check that cannot run returns a fail that says why.</summary>
public interface IDoctorCheck
{
    Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default);
}

/// <summary>Asks every check whether the hub can do its job. Mutates nothing and records no ledger event.</summary>
public sealed class DoctorService(IEnumerable<IDoctorCheck> checks, TimeProvider clock)
{
    // A hub that cannot write its own log is read before anything that depends on reading it.
    private static readonly string[] Order = ["secret", "logging", "ingest", "outbound", "project", "repo", "role"];

    public async Task<DoctorDto> RunAsync(bool probe, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var context = new DoctorContext(probe, now);

        var found = new List<CheckDto>();
        foreach (var check in checks)
        {
            try
            {
                found.AddRange(await check.RunAsync(context, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A check that breaks reports itself rather than taking the whole report down.
                found.Add(new CheckDto("doctor", check.GetType().Name, CheckStatus.Fail, $"This check itself failed: {ex.Message}"));
            }
        }

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
}
