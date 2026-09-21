using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        const string root = "/api/v1/knowledge";
        app.MapGet(root, (KnowledgeService s, CancellationToken ct) => s.ListAsync(ct));
        app.MapGet(root + "/context", (string project, string role, string harness, string platform, string configuration, KnowledgeService s, CancellationToken ct) => s.ContextAsync(project, role, harness, platform, configuration, ct));
        app.MapGet(root + "/{id}", (string id, KnowledgeService s, CancellationToken ct) => s.GetAsync(id, ct));
        app.MapPost(root, (LessonWriteRequest r, HttpContext h, KnowledgeService s, CancellationToken ct) => s.WriteAsync(h.GetCaller(), null, r, ct));
        app.MapPost(root + "/{id}/edit", (string id, LessonWriteRequest r, HttpContext h, KnowledgeService s, CancellationToken ct) => s.WriteAsync(h.GetCaller(), id, r, ct));
        app.MapPost(root + "/{id}/publish", (string id, LessonPublishRequest r, HttpContext h, KnowledgeService s, CancellationToken ct) => s.PublishAsync(h.GetCaller(), id, r, ct));
        app.MapPost(root + "/{id}/retire", (string id, LessonRetireRequest r, HttpContext h, KnowledgeService s, CancellationToken ct) => s.RetireAsync(h.GetCaller(), id, r, ct));
    }
}
