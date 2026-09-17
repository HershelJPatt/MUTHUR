using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Routes.Projects, (HttpContext http, AddProjectRequest request, ProjectService projects, CancellationToken ct) =>
            projects.AddAsync(http.GetCaller(), request, ct));

        app.MapGet(Routes.Projects, (ProjectService projects, CancellationToken ct) => projects.ListAsync(ct));

        app.MapGet(Routes.Projects + "/{key}", (string key, ProjectService projects, CancellationToken ct) => projects.GetAsync(key, ct));

        app.MapPut(Routes.Projects + "/{key}", (HttpContext http, string key, UpdateProjectRequest request, ProjectService projects, CancellationToken ct) =>
            projects.UpdateAsync(http.GetCaller(), key, request, ct));
    }
}
