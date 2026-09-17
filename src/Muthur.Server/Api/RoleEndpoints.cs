using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class RoleEndpoints
{
    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.Roles, (RoleService roles, CancellationToken ct) => roles.ListAsync(ct));

        app.MapPut(Routes.Roles, (HttpContext http, DefineRoleRequest request, RoleService roles, CancellationToken ct) =>
            roles.DefineAsync(http.GetCaller(), request, ct));

        app.MapGet(Routes.Roles + "/{key}/brief", (string key, RoleService roles, CancellationToken ct) => roles.BriefAsync(key, ct));

        app.MapPost(Routes.Roles + "/{key}/take", (HttpContext http, string key, RoleService roles, CancellationToken ct) =>
            roles.TakeAsync(http.GetCaller(), key, ct));

        app.MapPost(Routes.Roles + "/{key}/release", async (HttpContext http, string key, RoleService roles, CancellationToken ct) =>
        {
            await roles.ReleaseAsync(http.GetCaller(), key, ct);
            return Results.NoContent();
        });
    }

    public static void MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Routes.Tasks + "/{id}/implemented", (HttpContext http, string id, ImplementedRequest request, LifecycleService lifecycle, CancellationToken ct) =>
            lifecycle.ImplementedAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/pass", (HttpContext http, string id, VerdictRequest request, LifecycleService lifecycle, CancellationToken ct) =>
            lifecycle.VerdictAsync(http.GetCaller(), id, request, pass: true, ct));

        app.MapPost(Routes.Tasks + "/{id}/fail", (HttpContext http, string id, VerdictRequest request, LifecycleService lifecycle, CancellationToken ct) =>
            lifecycle.VerdictAsync(http.GetCaller(), id, request, pass: false, ct));

        app.MapPost(Routes.Tasks + "/{id}/land", (HttpContext http, string id, LifecycleService lifecycle, CancellationToken ct) =>
            lifecycle.LandAsync(http.GetCaller(), id, ct));

        app.MapGet(Routes.Validations, (string? role, LifecycleService lifecycle, CancellationToken ct) => lifecycle.PendingAsync(role, ct));
    }
}
