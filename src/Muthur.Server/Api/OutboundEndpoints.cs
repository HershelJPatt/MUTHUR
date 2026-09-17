using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class OutboundEndpoints
{
    public static void MapOutboundEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.OutboundTargets, (OutboundService outbound, CancellationToken ct) => outbound.TargetsAsync(ct));

        app.MapPut(Routes.OutboundTargets, (HttpContext http, DefineTargetRequest request, OutboundService outbound, CancellationToken ct) =>
            outbound.DefineTargetAsync(http.GetCaller(), request, ct));

        app.MapPost(Routes.Outbound, (HttpContext http, DraftOutboundRequest request, OutboundService outbound, CancellationToken ct) =>
            outbound.DraftAsync(http.GetCaller(), request, ct));

        app.MapGet(Routes.Outbound, (string? status, int? limit, OutboundService outbound, CancellationToken ct) =>
            outbound.ListAsync(status, limit ?? 100, ct));

        app.MapGet(Routes.Outbound + "/{id}", (string id, OutboundService outbound, CancellationToken ct) => outbound.GetAsync(id, ct));

        app.MapPost(Routes.Outbound + "/{id}/review", (HttpContext http, string id, ReviewOutboundRequest request, OutboundService outbound, CancellationToken ct) =>
            outbound.ReviewAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Outbound + "/{id}/approve", (HttpContext http, string id, OutboundService outbound, CancellationToken ct) =>
            outbound.FounderApproveAsync(http.GetCaller(), id, approve: true, note: null, ct));

        app.MapPost(Routes.Outbound + "/{id}/decline", (HttpContext http, string id, DeclineOutboundRequest request, OutboundService outbound, CancellationToken ct) =>
            outbound.FounderApproveAsync(http.GetCaller(), id, approve: false, request.Note, ct));

        app.MapPost(Routes.Outbound + "/{id}/send", (HttpContext http, string id, OutboundService outbound, CancellationToken ct) =>
            outbound.SendAsync(http.GetCaller(), id, ct));

        app.MapPost(Routes.Outbound + "/{id}/retry", (HttpContext http, string id, OutboundService outbound, CancellationToken ct) =>
            outbound.RetryAsync(http.GetCaller(), id, ct));
    }
}
