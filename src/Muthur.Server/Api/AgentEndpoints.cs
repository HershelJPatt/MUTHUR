using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Routes.AgentRegister, (HttpContext http, RegisterAgentRequest request, AgentService agents, CancellationToken ct) =>
            agents.RegisterAsync(http.GetCaller(), request, ct));

        app.MapPost(Routes.AgentHeartbeat, async (HttpContext http, HeartbeatRequest request, AgentService agents, CancellationToken ct) =>
        {
            await agents.HeartbeatAsync(http.GetCaller(), request, ct);
            return await agents.GetAsync(http.GetCaller().RequireAgent(), ct);
        });

        app.MapPost(Routes.AgentLimited, async (HttpContext http, LimitedRequest request, AgentService agents, CancellationToken ct) =>
        {
            await agents.SetLimitedAsync(http.GetCaller(), request, ct);
            return await agents.GetAsync(http.GetCaller().RequireAgent(), ct);
        });

        app.MapGet(Routes.AgentMe, (HttpContext http, AgentService agents, CancellationToken ct) =>
            agents.GetAsync(http.GetCaller().RequireAgent(), ct));

        app.MapGet(Routes.Agents, (bool? all, AgentService agents, CancellationToken ct) =>
            agents.RosterAsync(all ?? false, ct));
    }
}
