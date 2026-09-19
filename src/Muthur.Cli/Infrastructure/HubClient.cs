using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Muthur.Contracts;

namespace Muthur.Cli.Infrastructure;

public sealed record ApiResult(int Status, string Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public int ExitCode => Status switch
    {
        >= 200 and < 300 => ExitCodes.Ok,
        0 => ExitCodes.NotRunning,
        (int)HttpStatusCode.UnprocessableEntity => ExitCodes.RuleViolation,
        (int)HttpStatusCode.Conflict => ExitCodes.Conflict,
        (int)HttpStatusCode.NotFound => ExitCodes.NotFound,
        (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => ExitCodes.Unauthorized,
        // A hub that is stopping and a hub that has stopped need the same thing from the caller.
        (int)HttpStatusCode.ServiceUnavailable => ExitCodes.NotRunning,
        _ => ExitCodes.Error,
    };
}

/// <summary>Thin HTTP client. Responses are passed through as raw JSON; the CLI rarely needs to understand them.</summary>
public sealed class HubClient(string? token, TimeSpan? timeout = null)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(MuthurEnvironment.Url), Timeout = timeout ?? TimeSpan.FromSeconds(30) };

    public static HubClient For(ParseResult parse, TimeSpan? timeout = null) => new(Globals.ResolveToken(parse), timeout);

    public Task<ApiResult> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<ApiResult> PostAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, null, ct);

    public Task<ApiResult> PostAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(body, typeInfo), ct);

    public Task<ApiResult> PutAsync<T>(string path, T body, JsonTypeInfo<T> typeInfo, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, path, JsonSerializer.Serialize(body, typeInfo), ct);

    public Task<ApiResult> DeleteAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, path, null, ct);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            return new ApiResult((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Windows takes ~2s to refuse a loopback connection, so a short client timeout also means "not running".
            var body = JsonSerializer.Serialize(
                new ErrorResponse("not_running", $"MUTHUR is not reachable at {MuthurEnvironment.Url}. Start it with: muthur up"),
                MuthurJsonContext.Default.ErrorResponse);
            return new ApiResult(0, body);
        }
    }
}
