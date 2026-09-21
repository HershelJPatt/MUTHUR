using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var root = Routes.Tasks + "/{id}/integration";
        app.MapGet(root, (HttpContext http, string id, IntegrationService service, CancellationToken ct) => service.ShowAsync(http.GetCaller(), id, ct));
        app.MapPost(root + "/claim", (HttpContext http, string id, IntegrationClaimRequest request, IntegrationService service, CancellationToken ct) => service.ClaimAsync(http.GetCaller(), id, ct));
        app.MapPost(root + "/started", (HttpContext http, string id, IntegrationStartRequest request, IntegrationService service, CancellationToken ct) => service.StartedAsync(http.GetCaller(), id, request, ct));
        app.MapPost(root + "/candidate", (HttpContext http, string id, IntegrationCandidateRequest request, IntegrationService service, CancellationToken ct) => service.CandidateAsync(http.GetCaller(), id, request, ct));
        app.MapPost(root + "/verdict", (HttpContext http, string id, IntegrationEvidenceDto request, IntegrationService service, CancellationToken ct) => service.VerdictAsync(http.GetCaller(), id, request, ct));
        app.MapPost(root + "/failure", (HttpContext http, string id, IntegrationFailureRequest request, IntegrationService service, CancellationToken ct) => service.FailureAsync(http.GetCaller(), id, request, ct));
        app.MapPost(root + "/renew", (HttpContext http, string id, IntegrationRenewRequest request, IntegrationService service, CancellationToken ct) => service.RenewAsync(http.GetCaller(), id, request, ct));
    }
}
