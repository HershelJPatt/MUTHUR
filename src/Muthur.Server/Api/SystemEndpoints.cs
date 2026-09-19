using System.Reflection;
using Muthur.Contracts;
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
