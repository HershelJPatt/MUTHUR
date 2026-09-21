using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class IncidentEndpoints
{
    public static void MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.Incidents, (string? project, IncidentService service, CancellationToken ct) => service.ListAsync(project, ct));
        app.MapGet(Routes.IncidentMatches, (string? project, string? signature, string? path, string? configuration, IncidentService service, CancellationToken ct) =>
            service.MatchAsync(project, signature, path, configuration, ct));
        app.MapGet(Routes.Incidents + "/{id}", (string id, IncidentService service, CancellationToken ct) => service.GetAsync(id, ct));
        app.MapGet(Routes.Incidents + "/{id}/metrics", (string id, string? hours, IncidentService service, CancellationToken ct) =>
        {
            var count = 24;
            if (hours is not null && !int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out count))
                throw Fail.Rule("incident_input", "Hours must be an integer between 1 and 720.");
            return service.MetricsAsync(id, count, ct);
        });
        app.MapPost(Routes.Incidents, async (HttpContext http, IncidentService service, CancellationToken ct) =>
            await service.AddAsync(http.GetCaller(), await ReadAsync(http, MuthurJsonContext.Default.AddIncidentRequest, ct), ct));
        app.MapPost(Routes.Incidents + "/{id}/update", async (HttpContext http, string id, IncidentService service, CancellationToken ct) =>
            await service.UpdateAsync(http.GetCaller(), id, await ReadAsync(http, MuthurJsonContext.Default.UpdateIncidentRequest, ct), ct));
        app.MapPost(Routes.Incidents + "/{id}/transition", async (HttpContext http, string id, IncidentService service, CancellationToken ct) =>
            await service.TransitionAsync(http.GetCaller(), id, await ReadAsync(http, MuthurJsonContext.Default.TransitionIncidentRequest, ct), ct));
        app.MapPost(Routes.Incidents + "/{id}/observe", async (HttpContext http, string id, IncidentService service, CancellationToken ct) =>
            await service.ObserveAsync(http.GetCaller(), id, await ReadAsync(http, MuthurJsonContext.Default.ObserveIncidentRequest, ct), ct));
        app.MapPost(Routes.Incidents + "/{id}/unlink", async (HttpContext http, string id, IncidentService service, CancellationToken ct) =>
            await service.UnlinkAsync(http.GetCaller(), id, await ReadAsync(http, MuthurJsonContext.Default.UnlinkIncidentRequest, ct), ct));
        app.MapPost(Routes.Incidents + "/{id}/suppress", async (HttpContext http, string id, IncidentService service, CancellationToken ct) =>
            await service.SuppressAsync(http.GetCaller(), id, await ReadAsync(http, MuthurJsonContext.Default.SuppressIncidentRequest, ct), ct));
        app.MapPost(Routes.Incidents + "/{id}/recover", async (HttpContext http, string id, IncidentService service, CancellationToken ct) =>
            await service.RecoverAsync(http.GetCaller(), id, await ReadAsync(http, MuthurJsonContext.Default.RecoverIncidentRequest, ct), ct));
    }

    private static async Task<T> ReadAsync<T>(HttpContext http, JsonTypeInfo<T> json, CancellationToken ct) where T : class
    {
        http.GetCaller().RequireIdentified();
        try
        {
            return await JsonSerializer.DeserializeAsync(http.Request.Body, json, ct)
                ?? throw Fail.Rule("incident_input", "A JSON request object is required.");
        }
        catch (JsonException)
        {
            throw Fail.Rule("incident_input", "Malformed incident request.");
        }
    }
}
