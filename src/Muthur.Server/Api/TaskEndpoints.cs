using Muthur.Contracts;
using Muthur.Core;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.Tasks + "/{id}/units", async (string id, TaskUnitService units, CancellationToken ct) =>
        {
            var graph = await units.GetAsync(id, ct);
            return graph is null ? Results.Content("null", "application/json") :
                Results.Json(graph, MuthurJsonContext.Default.TaskUnitGraph);
        });
        app.MapGet(Routes.Tasks + "/{id}/resume", (string id, TaskUnitService units, CancellationToken ct) => units.ResumeAsync(id, ct));
        app.MapPost(Routes.Tasks + "/{id}/units/define", (HttpContext http, string id, DefineTaskUnitsRequest request, TaskUnitService units, CancellationToken ct) =>
            units.DefineAsync(http.GetCaller(), id, request, ct));
        app.MapPost(Routes.Tasks + "/{id}/units/checkpoint", (HttpContext http, string id, TaskUnitCheckpointRequest request, TaskUnitService units, CancellationToken ct) =>
            units.CheckpointAsync(http.GetCaller(), id, request, ct));
        app.MapPost(Routes.Tasks + "/{id}/dependencies", (HttpContext http, string id, DependenciesRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.SetDependenciesAsync(http.GetCaller(), id, request, ct));
        app.MapPost(Routes.Tasks, (HttpContext http, AddTaskRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.AddAsync(http.GetCaller(), request, ct));

        app.MapGet(Routes.Tasks, (HttpContext http, string? state, string? project, string? owner, bool? mine, bool? open, int? limit, TaskService tasks, CancellationToken ct) =>
        {
            if (mine == true)
            {
                http.GetCaller().RequireAgent();
                owner = http.GetCaller().Name;
            }
            return tasks.ListAsync(new TaskQuery(ParseStates(state), project, owner, open == true, limit ?? 500), ct);
        });

        app.MapGet(Routes.Tasks + "/{id}", (string id, TaskService tasks, CancellationToken ct) => tasks.GetAsync(id, ct));

        app.MapPost(Routes.Tasks + "/{id}/claim", (HttpContext http, string id, ClaimTaskRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.ClaimAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/release", (HttpContext http, string id, ReleaseTaskRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.ReleaseAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/spec", (HttpContext http, string id, SetSpecRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.SetSpecAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/priority", (HttpContext http, string id, SetPriorityRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.SetPriorityAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/attended", (HttpContext http, string id, AttendedRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.SetAttendedAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/hold", (HttpContext http, string id, HoldRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.SetHoldAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/cancel", (HttpContext http, string id, CancelTaskRequest request, TaskService tasks, CancellationToken ct) =>
            tasks.CancelAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Tasks + "/{id}/reopen", (HttpContext http, string id, TaskService tasks, CancellationToken ct) =>
            tasks.ReopenAsync(http.GetCaller(), id, ct));

        app.MapGet(Routes.Events, (long? since, string? task, int? limit, EventService events, CancellationToken ct) =>
            events.ListAsync(since, task, limit ?? 100, ct));
    }

    private static List<TaskState>? ParseStates(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var states = new List<TaskState>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Wire.TryParseTaskState(part, out var state))
                throw Fail.Rule("invalid_state", $"'{part}' is not a task state. Valid: {string.Join(", ", Wire.AllTaskStates.Select(s => s.ToWire()))}.");
            states.Add(state);
        }
        return states;
    }
}
