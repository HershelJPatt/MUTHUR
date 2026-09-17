using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class InboundEndpoints
{
    public static void MapInboundEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Routes.Inbound, (HttpContext http, AddInboundRequest request, InboundService inbound, CancellationToken ct) =>
            inbound.AddAsync(http.GetCaller(), request, ct));

        app.MapGet(Routes.Inbound, (string? status, string? project, int? limit, InboundService inbound, CancellationToken ct) =>
            inbound.ListAsync(status, project, limit ?? 200, ct));

        app.MapGet(Routes.Inbound + "/{id}", (string id, InboundService inbound, CancellationToken ct) => inbound.GetAsync(id, ct));

        app.MapPost(Routes.Inbound + "/{id}/claim", (HttpContext http, string id, InboundService inbound, CancellationToken ct) =>
            inbound.ClaimAsync(http.GetCaller(), id, ct));

        app.MapPost(Routes.Inbound + "/{id}/convert", (HttpContext http, string id, ConvertInboundRequest request, InboundService inbound, CancellationToken ct) =>
            inbound.ConvertAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Inbound + "/{id}/dismiss", (HttpContext http, string id, DismissInboundRequest request, InboundService inbound, CancellationToken ct) =>
            inbound.DismissAsync(http.GetCaller(), id, request, ct));

        app.MapGet(Routes.IngestSources, (IngestService ingest, CancellationToken ct) => ingest.SourcesAsync(ct));

        app.MapPost(Routes.IngestPoll, (HttpContext http, IngestService ingest, CancellationToken ct) =>
        {
            http.GetCaller().RequireIdentified();
            return ingest.PollAsync(ct);
        });
    }
}
