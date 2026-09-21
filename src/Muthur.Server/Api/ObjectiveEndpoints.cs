using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class ObjectiveEndpoints
{
    public static void MapObjectiveEndpoints(this IEndpointRouteBuilder app)
    {
        const string path = "/api/v1/objectives";
        app.MapGet(path, (string? project, ObjectiveService s, CancellationToken ct) => s.ListAsync(project, ct));
        app.MapGet(path + "/{id}", (string id, ObjectiveService s, CancellationToken ct) => s.GetAsync(id, ct));
        app.MapPost(path, (AddObjectiveRequest r, HttpContext h, ObjectiveService s, CancellationToken ct) => s.AddAsync(h.GetCaller(), r, ct));
        app.MapPost(path + "/{id}/amend", (string id, AmendObjectiveRequest r, HttpContext h, ObjectiveService s, CancellationToken ct) => s.AmendAsync(h.GetCaller(), id, r, ct));
        app.MapPost(path + "/{id}/link", (string id, LinkObjectiveRequest r, HttpContext h, ObjectiveService s, CancellationToken ct) => s.LinkAsync(h.GetCaller(), id, r, ct));
        app.MapPost(path + "/{id}/observe", (string id, ObserveObjectiveRequest r, HttpContext h, ObjectiveService s, CancellationToken ct) => s.ObserveAsync(h.GetCaller(), id, r, ct));
        app.MapPost(path + "/{id}/effort", (string id, EffortObjectiveRequest r, HttpContext h, ObjectiveService s, CancellationToken ct) => s.EffortAsync(h.GetCaller(), id, r, ct));
        app.MapPost(path + "/{id}/accept", (string id, AcceptObjectiveRequest r, HttpContext h, ObjectiveService s, CancellationToken ct) => s.AcceptAsync(h.GetCaller(), id, r, ct));
        app.MapPost(path + "/{id}/close", (string id, CloseObjectiveRequest r, HttpContext h, ObjectiveService s, CancellationToken ct) => s.CloseAsync(h.GetCaller(), id, r, ct));
    }
}
