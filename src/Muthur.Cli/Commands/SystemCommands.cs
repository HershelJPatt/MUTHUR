using System.CommandLine;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class SystemCommands
{
    public static void AddTo(RootCommand root)
    {
        var status = new Command("status", "Show whether the hub is running.");
        status.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Status, ct)));
        root.Subcommands.Add(status);

        var up = new Command("up", "Start the hub server in the background (no-op if already running).");
        up.SetAction(UpAsync);
        root.Subcommands.Add(up);

        var down = new Command("down", "Stop the hub server.");
        down.SetAction(async (parse, ct) =>
        {
            var result = await new HubClient(Globals.ReadFounderToken()).PostAsync(Routes.Shutdown, ct);
            if (result.ExitCode == ExitCodes.NotRunning) return ExitCodes.Ok;
            if (!result.IsSuccess) return Output.Emit(parse, result);

            // The server answers before it stops listening; "down" only returns once it is really gone.
            var probe = new HubClient(null, TimeSpan.FromSeconds(3));
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if ((await probe.GetAsync(Routes.Status, ct)).ExitCode == ExitCodes.NotRunning) return ExitCodes.Ok;
                await Task.Delay(250, ct);
            }
            return Output.Error("stop_timeout", "The server accepted the shutdown but is still answering after 10s.");
        });
        root.Subcommands.Add(down);
    }

    private static async Task<int> UpAsync(ParseResult parse, CancellationToken ct)
    {
        var probe = new HubClient(null, TimeSpan.FromSeconds(3));
        var current = await probe.GetAsync(Routes.Status, ct);
        if (current.IsSuccess) return Output.Emit(parse, current);

        if (ServerProcess.Locate() is not { } server)
            return Output.Error("server_not_found",
                $"Muthur.Server was not found next to the CLI (server/) and ${MuthurEnvironment.ServerPathVariable} is not set.");

        ServerProcess.StartDetached(server);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(250, ct);
            current = await probe.GetAsync(Routes.Status, ct);
            if (current.IsSuccess) return Output.Emit(parse, current);
        }
        return Output.Error("start_timeout",
            $"The server did not answer within 15s. See {Path.Combine(MuthurEnvironment.Home, MuthurEnvironment.LogFile)}.");
    }
}
