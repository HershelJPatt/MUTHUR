using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class HarnessEndpoints
{
    public static void MapHarnessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Routes.ProbeAdmit, (HttpContext http, ProbeAdmissionRequest request, ConductorService conductor, CancellationToken ct) =>
            conductor.AdmitProbeAsync(http.GetCaller(), request, ct));
        app.MapPost(Routes.ProbeRelease, async (HttpContext http, ProbeReleaseRequest request, ConductorService conductor, CancellationToken ct) =>
        {
            await conductor.ReleaseProbeAsync(http.GetCaller(), request, ct);
            return Results.NoContent();
        });

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

        app.MapGet(Routes.Conductor, (ConductorService conductor, CancellationToken ct) => conductor.StatusAsync(ct));

        // Turning staffing on is a founder decision and is recorded as one.
        app.MapPost(Routes.Conductor, async (HttpContext http, ConductorSwitch request, ConductorService conductor, CancellationToken ct) =>
            await conductor.SetEnabledAsync(http.GetCaller(), request.Enabled, ct)).RequireFounder();

        // How many sessions it may run, and the lower number for the hours nobody is watching.
        app.MapPost(Routes.ConductorSessions, async (HttpContext http, ConductorSessionsRequest request, ConductorService conductor, CancellationToken ct) =>
            await conductor.SetCeilingAsync(http.GetCaller(), request, ct)).RequireFounder();

        // Whether it also starts work from the backlog. Its own switch, off until the founder asks for it.
        app.MapPost(Routes.ConductorOrchestrators, async (HttpContext http, ConductorOrchestratorSwitch request, ConductorService conductor, CancellationToken ct) =>
            await conductor.SetOrchestratorsAsync(http.GetCaller(), request.Enabled, ct)).RequireFounder();
    }
}
