using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Muthur.Contracts;
using Muthur.Core;

namespace Muthur.Server.Infrastructure;

/// <summary>Turns <see cref="MuthurException"/> into a status code + <see cref="ErrorResponse"/> body.</summary>
public sealed class ErrorMiddleware(
    RequestDelegate next,
    ILogger<ErrorMiddleware> logger,
    IOptions<JsonOptions> json,
    IHostApplicationLifetime lifetime)
{
    public async Task InvokeAsync(HttpContext http)
    {
        // Asked to stop, so nothing new starts. A request that reached the database here would find the
        // connection disposed underneath it and be answered "internal error" — the hub telling an agent it
        // broke, when what happened is that it was told to stop.
        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await StoppingAsync(http);
            return;
        }

        try
        {
            await next(http);
        }
        catch (MuthurException ex) when (!http.Response.HasStarted)
        {
            http.Response.StatusCode = ex.Kind switch
            {
                ErrorKind.RuleViolation => StatusCodes.Status422UnprocessableEntity,
                ErrorKind.Conflict => StatusCodes.Status409Conflict,
                ErrorKind.NotFound => StatusCodes.Status404NotFound,
                ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
                _ => StatusCodes.Status400BadRequest,
            };
            await http.Response.WriteAsJsonAsync(new ErrorResponse(ex.Code, ex.Message), json.Value.SerializerOptions);
        }
        catch (BadHttpRequestException ex) when (!http.Response.HasStarted)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new ErrorResponse("bad_request", ex.Message), json.Value.SerializerOptions);
        }
        // The stop arrived after this request was already past the guard above — the window is microseconds
        // wide, and the disposal that follows the signal is what the request actually trips over. Both
        // conditions are required: an ObjectDisposedException on a hub that is *not* stopping is a real
        // defect (a disposed HttpClient, a captured scoped service) and has to keep arriving as a 500 with
        // its stack in the log, or this stops being a fix and becomes a way to stop hearing about a bug.
        catch (ObjectDisposedException) when (!http.Response.HasStarted && lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await StoppingAsync(http);
        }
        catch (Exception ex) when (!http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled error on {Method} {Path}", http.Request.Method, http.Request.Path);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await http.Response.WriteAsJsonAsync(new ErrorResponse("internal_error", ex.Message), json.Value.SerializerOptions);
        }
    }

    /// <summary>Deliberately not logged: a hub that was asked to stop and then declined a request did nothing wrong.</summary>
    private async Task StoppingAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await http.Response.WriteAsJsonAsync(
            new ErrorResponse("hub_stopping", "The hub is shutting down and did not do this. Start it again with: muthur up"),
            json.Value.SerializerOptions);
    }
}
