using System.Reflection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Server.Auth;
using Muthur.Server.Infrastructure;
using Muthur.Server.Services;

namespace Muthur.Server.Api;

public static class SystemEndpoints
{
    public static readonly string Version =
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Routes.Status, (InstanceInfo instance, MuthurOptions options, TimeProvider clock) =>
            new StatusResponse(
                "MUTHUR",
                Version,
                instance.InstanceId,
                Environment.ProcessId,
                instance.StartedAt,
                clock.GetUtcNow(),
                options.DataDir,
                options.DbProvider,
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        // Unauthenticated, exactly like status: nothing doctor returns is a secret.
        app.MapGet(Routes.Doctor, (bool? probe, DoctorService doctor, CancellationToken ct) => doctor.RunAsync(probe ?? true, ct));

        // Also unauthenticated: receipts names accounts and task titles, and /harness/tiers and /tasks already
        // serve both without a token.
        app.MapGet(Routes.Receipts, (int? hours, string? task, ReceiptsService receipts, CancellationToken ct) =>
        {
            int? taskId = null;
            if (task is { Length: > 0 })
            {
                if (!Wire.TryParseTaskId(task, out var id)) throw Fail.Rule("invalid_task_id", $"'{task}' is not a task id like T-12.");
                taskId = id;
            }
            return receipts.ReadAsync(hours ?? 24, taskId, ct);
        });

        app.MapPost(Routes.Shutdown, (IHostApplicationLifetime lifetime) =>
        {
            // Respond first, then stop: the caller gets its 202 before the listener closes.
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                lifetime.StopApplication();
            });
            return Results.Accepted();
        }).RequireFounder();
    }
}
