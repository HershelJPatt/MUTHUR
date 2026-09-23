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

        var offline = new Option<bool>("--offline") { Description = "Skip the checks that touch the network; report only what the hub already knows." };
        var doctor = new Command("doctor", "Check whether this hub can do its job: ingest, outbound, projects, repositories, roles, agents. Exit 1 if anything failed.") { offline };
        doctor.SetAction(async (parse, ct) =>
        {
            var skipProbes = parse.GetValue(offline);
            var result = await HubClient.For(parse, DoctorTimeout(skipProbes))
                .GetAsync(Routes.Doctor + (skipProbes ? "?probe=false" : ""), ct);
            return DoctorExitCode(result, Output.Emit(parse, result));
        });
        root.Subcommands.Add(doctor);

        var hours = new Option<int?>("--hours") { Description = "The window to report on, in hours (default 24, at most 720)." };
        var task = new Option<string?>("--task") { Description = "One task, e.g. T-12: only its sessions, worker runs and cost to land." };
        var receipts = new Command("receipts", "What this organization spent in a window: sessions by harness and account, which tasks consumed them, what they cost, and where the time went.") { hours, task };
        receipts.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Receipts + ReceiptsQuery(parse.GetValue(hours), parse.GetValue(task)), ct)));
        root.Subcommands.Add(receipts);

        var up = new Command("up", "Start the hub server in the background (no-op if already running).");
        up.SetAction(UpAsync);
        root.Subcommands.Add(up);

        var any = new Option<bool>("--any") { Description = "Stop the hub at MUTHUR_URL even if it belongs to a different installation." };
        var down = new Command("down", "Stop the hub server that belongs to this installation.") { any };
        down.SetAction(async (parse, ct) =>
        {
            // A build under test run without its scratch environment would otherwise take the live hub down.
            // So "down" must positively establish that the running hub is the one bundled with this CLI;
            // anything it cannot establish (no bundled server, unreadable status, an older hub) is a refusal.
            var running = await new HubClient(null, TimeSpan.FromSeconds(3)).GetAsync(Routes.Status, ct);
            if (running.ExitCode == ExitCodes.NotRunning) return ExitCodes.Ok;
            if (!parse.GetValue(any))
            {
                var ours = ServerProcess.Locate() is { } mine ? Path.GetDirectoryName(Path.GetFullPath(mine)) : null;
                string? theirs = null;
                try
                {
                    if (running.IsSuccess)
                        theirs = System.Text.Json.JsonSerializer.Deserialize(running.Body, MuthurJsonContext.Default.StatusResponse)?.ServerDirectory;
                }
                catch (System.Text.Json.JsonException)
                {
                    // Something answers on that port, but it does not speak our status format: not ours.
                }
                var isOurs = ours is not null && theirs is { Length: > 0 } && string.Equals(Path.GetFullPath(theirs), ours, StringComparison.OrdinalIgnoreCase);
                if (!isOurs)
                    return Output.Error("not_my_hub",
                        $"The hub at {MuthurEnvironment.Url} runs from {(theirs is { Length: > 0 } ? theirs : "an installation that cannot be identified")}; " +
                        $"this CLI {(ours is null ? "has no bundled server (it is a build output, not an installation)" : $"belongs to {ours}")}. " +
                        "Use the CLI of the hub's own installation, fix MUTHUR_URL/MUTHUR_HOME, or pass --any.", ExitCodes.RuleViolation);
            }

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

    /// <summary>
    /// A probing run waits on the hub's own probes — 60s per gh call, 30s per git call — and HubClient's
    /// default of 30s would come back as ApiResult(0), reporting a healthy hub as not_running. Offline
    /// touches nothing and keeps the default.
    /// </summary>
    internal static TimeSpan? DoctorTimeout(bool offline) => offline ? null : TimeSpan.FromSeconds(180);

    /// <summary>
    /// The window and the task as a query string. An absent --hours sends no parameter, so the hub's own default
    /// of 24 stands; a number outside 1..720 is sent as typed rather than refused here, because the hub clamps it
    /// and a founder asking for 100000 hours wants everything, not an argument error. The task is sent as typed
    /// too: the hub owns what a task id looks like, and answers 422 for one it cannot read.
    /// </summary>
    internal static string ReceiptsQuery(int? hours, string? task = null)
    {
        var query = new List<string>();
        if (hours is { } h) query.Add("hours=" + h);
        if (task is { Length: > 0 } t) query.Add("task=" + Uri.EscapeDataString(t));
        return query.Count == 0 ? "" : "?" + string.Join('&', query);
    }

    /// <summary>A report that reached us and contains a failed check is exit 1; a warning never is.</summary>
    internal static int DoctorExitCode(ApiResult result, int emitted)
    {
        if (!result.IsSuccess || emitted != ExitCodes.Ok) return emitted;
        var report = System.Text.Json.JsonSerializer.Deserialize(result.Body, MuthurJsonContext.Default.DoctorDto)!;
        return report.Fail > 0 ? ExitCodes.Error : ExitCodes.Ok;
    }

    private static async Task<int> UpAsync(ParseResult parse, CancellationToken ct)
    {
        var (status, error) = await StartAsync(ct);
        return status is not null ? Output.Emit(parse, status) : Output.Error(error!.Value.Code, error.Value.Message);
    }

    /// <summary>Starts the bundled server unless one already answers at MUTHUR_URL; the status it answered with, or why it could not be started.</summary>
    internal static async Task<(ApiResult? Status, (string Code, string Message)? Error)> StartAsync(CancellationToken ct)
    {
        var probe = new HubClient(null, TimeSpan.FromSeconds(3));
        var current = await probe.GetAsync(Routes.Status, ct);
        if (current.IsSuccess) return (current, null);

        if (ServerProcess.Locate() is not { } server)
            return (null, ("server_not_found", $"Muthur.Server was not found next to the CLI (server/) and ${MuthurEnvironment.ServerPathVariable} is not set."));

        ServerProcess.StartDetached(server);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(250, ct);
            current = await probe.GetAsync(Routes.Status, ct);
            if (current.IsSuccess) return (current, null);
        }
        return (null, ("start_timeout", $"The server did not answer within 15s. See {Path.Combine(MuthurEnvironment.Home, MuthurEnvironment.LogFile)}."));
    }
}
