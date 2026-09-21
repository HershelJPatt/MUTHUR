using System.Security.Cryptography;
using System.Text;
using Muthur.Core;
using Muthur.Server.Infrastructure;
using Muthur.Server.Services;

namespace Muthur.Server.Auth;

public enum CallerKind { Anonymous, Agent, Founder }

/// <summary>Who is making this request. Resolved once per request from the bearer token.</summary>
public sealed record Caller(CallerKind Kind, Guid? AgentId = null, string Name = "anonymous", string? Model = null, bool IntegrationRunner = false)
{
    public static readonly Caller Anonymous = new(CallerKind.Anonymous);
    public static readonly Caller Founder = new(CallerKind.Founder, null, "founder");

    /// <summary>Used for work the hub does on its own behalf (lease sweeps, ingest).</summary>
    public static readonly Caller System = new(CallerKind.Founder, null, "muthur");

    public bool IsFounder => Kind == CallerKind.Founder;
    public bool IsAgent => Kind == CallerKind.Agent;

    public Guid RequireAgent() =>
        AgentId ?? throw Fail.Unauthorized("This command must be run as a registered agent (set MUTHUR_AGENT or pass --as-agent).");

    public void RequireIdentified()
    {
        if (Kind == CallerKind.Anonymous)
            throw Fail.Unauthorized("This command needs an identity: run as a registered agent or pass --founder.");
    }
}

public static class Tokens
{
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool SecureEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

public static class CallerHttpExtensions
{
    private const string ItemKey = "muthur.caller";

    public static Caller GetCaller(this HttpContext http) => http.Items[ItemKey] as Caller ?? Caller.Anonymous;

    public static void SetCaller(this HttpContext http, Caller caller) => http.Items[ItemKey] = caller;

    public static string? BearerToken(this HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }

    /// <summary>Endpoint may only be called with the founder token.</summary>
    public static RouteHandlerBuilder RequireFounder(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            if (!ctx.HttpContext.GetCaller().IsFounder)
                throw Fail.Unauthorized("This command requires the founder token.");
            return await next(ctx);
        });
}

public sealed class CallerMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, InstanceInfo instance, AgentService agents)
    {
        if (http.BearerToken() is { Length: > 0 } token)
        {
            if (Tokens.SecureEquals(token, instance.FounderToken))
                http.SetCaller(Caller.Founder);
            else if (await agents.AuthenticateAsync(token, http.RequestAborted) is { } caller)
                http.SetCaller(caller);
            else
                throw Fail.Unauthorized("The bearer token is not recognized. Re-register the agent or check MUTHUR_AGENT.");
        }
        if (http.GetCaller().IntegrationRunner)
            await http.RequestServices.GetRequiredService<IntegrationService>()
                .AuthorizeRequestAsync(http.GetCaller(), http.Request.Method, http.Request.Path.Value ?? "", http.RequestAborted);
        // The overseer is a technical delegate, never a general-purpose founder or outbound actor.
        if (http.GetCaller().Name == OverseerService.Identity && http.Request.Method != "GET")
        {
            if (http.Request.Method != "POST" || http.Request.Path.Value is not
                ("/api/v1/overseer/checkpoint" or "/api/v1/overseer/decide" or "/api/v1/overseer/triage" or "/api/v1/agents/heartbeat" or "/api/v1/tasks"))
                throw Fail.Unauthorized("The overseer may only checkpoint, answer technical requests, file follow-ups, and renew its presence.");
            if (http.Request.Path.Value == "/api/v1/tasks")
                await http.RequestServices.GetRequiredService<Ledger>().MutateAsync(http.GetCaller(), m => OverseerService.RequireActive(m, http.RequestAborted), http.RequestAborted);
        }
        await next(http);
    }
}
