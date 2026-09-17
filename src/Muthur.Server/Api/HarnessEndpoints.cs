using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class HarnessEndpoints
{
    public static void MapHarnessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.Tiers, (string? tier, HarnessService harnesses, CancellationToken ct) => harnesses.TiersAsync(tier, ct));

        app.MapPost(Routes.AccountLimits, async (HttpContext http, AccountLimitRequest request, HarnessService harnesses, CancellationToken ct) =>
        {
            await harnesses.ReportLimitAsync(http.GetCaller(), request, ct);
            return Results.NoContent();
        });

        app.MapPost(Routes.WorkerRuns, async (HttpContext http, WorkerRunReport report, HarnessService harnesses, CancellationToken ct) =>
        {
            await harnesses.RecordWorkerRunAsync(http.GetCaller(), report, ct);
            return Results.NoContent();
        });
    }
}
