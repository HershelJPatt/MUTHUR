using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.Api + "/overseer", (OverseerService service, CancellationToken ct) => service.StatusAsync(ct));
        app.MapPost(Routes.Api + "/overseer/config", (HttpContext http, OverseerConfig config, OverseerService service, CancellationToken ct) =>
            service.ConfigureAsync(http.GetCaller(), config, ct)).RequireFounder();
        app.MapPost(Routes.Api + "/overseer/checkpoint", async (HttpContext http, OverseerCheckpoint checkpoint, OverseerService service, CancellationToken ct) =>
        { await service.CheckpointAsync(http.GetCaller(), checkpoint, ct); return Results.Ok(); });
        app.MapPost(Routes.Api + "/overseer/decide", (HttpContext http, OverseerDecision decision, RequestService service, CancellationToken ct) =>
            service.DecideAsync(http.GetCaller(), decision, ct));
        app.MapPost(Routes.Messages, (HttpContext http, SendMessageRequest request, MessageService messages, CancellationToken ct) =>
            messages.SendAsync(http.GetCaller(), request, ct));

        app.MapGet(Routes.Messages, (int? limit, bool? founder, MessageService messages, CancellationToken ct) =>
            messages.HistoryAsync(limit ?? 50, founder == true, ct));

        app.MapGet(Routes.Inbox, (HttpContext http, int? wait, bool? peek, MessageService messages, CancellationToken ct) =>
            messages.InboxAsync(http.GetCaller(), wait ?? 0, peek == true, ct));

        app.MapPost(Routes.Requests, (HttpContext http, AskRequest request, RequestService requests, CancellationToken ct) =>
            requests.AskAsync(http.GetCaller(), request, ct));

        app.MapGet(Routes.Requests, (bool? open, int? limit, RequestService requests, CancellationToken ct) =>
            requests.ListAsync(open != false, limit ?? 200, ct));

        app.MapPost(Routes.Requests + "/{id:int}/answer", (HttpContext http, int id, AnswerRequest request, RequestService requests, CancellationToken ct) =>
            requests.AnswerAsync(http.GetCaller(), id, request, ct));

        app.MapPost(Routes.Requests + "/{id:int}/cancel", (HttpContext http, int id, RequestService requests, CancellationToken ct) =>
            requests.CancelAsync(http.GetCaller(), id, ct));
    }
}
