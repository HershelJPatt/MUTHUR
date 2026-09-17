using Muthur.Contracts;
using Muthur.Core;

namespace Muthur.Server.Infrastructure;

/// <summary>Turns <see cref="MuthurException"/> into a status code + <see cref="ErrorResponse"/> body.</summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
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
            await http.Response.WriteAsJsonAsync(new ErrorResponse(ex.Code, ex.Message), MuthurJsonContext.Default.ErrorResponse);
        }
        catch (BadHttpRequestException ex) when (!http.Response.HasStarted)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new ErrorResponse("bad_request", ex.Message), MuthurJsonContext.Default.ErrorResponse);
        }
        catch (Exception ex) when (!http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled error on {Method} {Path}", http.Request.Method, http.Request.Path);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await http.Response.WriteAsJsonAsync(new ErrorResponse("internal_error", ex.Message), MuthurJsonContext.Default.ErrorResponse);
        }
    }
}
