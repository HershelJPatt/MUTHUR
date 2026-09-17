using System.Security.Cryptography;
using System.Text;
using Muthur.Core;
using Muthur.Server.Infrastructure;

namespace Muthur.Server.Auth;

public enum CallerKind { Anonymous, Agent, Founder }

/// <summary>Who is making this request. Resolved once per request from the bearer token.</summary>
public sealed record Caller(CallerKind Kind, Guid? AgentId = null, string Name = "anonymous")
{
    public static readonly Caller Anonymous = new(CallerKind.Anonymous);
    public static readonly Caller Founder = new(CallerKind.Founder, null, "founder");

    public bool IsFounder => Kind == CallerKind.Founder;
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
    public async Task InvokeAsync(HttpContext http, InstanceInfo instance)
    {
        if (http.BearerToken() is { } token && Tokens.SecureEquals(token, instance.FounderToken))
            http.SetCaller(Caller.Founder);
        await next(http);
    }
}
