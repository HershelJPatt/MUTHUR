using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class RoutingEndpoints
{
    public static void MapRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/routing/history", (int? hours, RoutingService s, CancellationToken ct) => s.HistoryAsync(hours ?? 168, ct));
        app.MapGet("/api/v1/routing/{task}", (string task, RoutingService s, CancellationToken ct) => s.ShowAsync(task, ct));
        app.MapPost("/api/v1/routing/{task}", (string task, RoutingReport r, HttpContext h, RoutingService s, CancellationToken ct) => s.RecordAsync(h.GetCaller(), task, r, ct));
    }
}
