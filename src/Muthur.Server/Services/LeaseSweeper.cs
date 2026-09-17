namespace Muthur.Server.Services;

/// <summary>Releases leases whose holders went away. Also runs once at startup to reconcile after downtime.</summary>
public sealed class LeaseSweeper(IServiceProvider services, TimeProvider clock, ILogger<LeaseSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var released = await services.GetRequiredService<TaskService>().SweepExpiredClaimsAsync(stoppingToken);
                if (released > 0) logger.LogInformation("Returned {Count} task(s) with lapsed claims to the backlog.", released);
                var freed = await services.GetRequiredService<RoleService>().SweepExpiredHoldsAsync(stoppingToken);
                if (freed > 0) logger.LogInformation("Freed {Count} role(s) whose holders went quiet.", freed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lease sweep failed.");
            }
            await Task.Delay(Interval, clock, stoppingToken);
        }
    }
}
