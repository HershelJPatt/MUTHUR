using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class DesignEndpoints
{
    public static void MapDesignEndpoints(this IEndpointRouteBuilder app)
    {
        const string root = "/api/v1/designs/{task}";
        app.MapGet(root, (string task, DesignService s, CancellationToken ct) => s.GetAsync(task, ct));
        app.MapPost(root + "/attach", (string task, WriteDesignRequest r, HttpContext h, DesignService s, CancellationToken ct) => s.WriteAsync(h.GetCaller(), task, r, ct));
        app.MapPost(root + "/approve", (string task, ApproveDesignRequest r, HttpContext h, DesignService s, CancellationToken ct) => s.ApproveAsync(h.GetCaller(), task, r, ct));
        app.MapPost(root + "/check", (string task, CheckDesignRequest r, HttpContext h, DesignService s, CancellationToken ct) => s.CheckAsync(h.GetCaller(), task, r, ct));
    }
}
