using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

/// <summary>
/// The organization with the model taken out. <c>sim run</c> stands up a scratch hub whose every tier is the
/// <c>sim</c> harness, seeds tasks and turns the conductor on; the conductor then staffs <c>sim agent</c> sessions
/// exactly as it would staff a model, and the board shows the flow. Nothing here touches the live hub: the agent
/// refuses any home or URL that could be one, and the run makes its own.
/// </summary>
public static class SimCommands
{
    public const string PaceVariable = "MUTHUR_SIM_PACE";
    private const string Role = "win-validator";
    private const string Project = "sim";

    public static void AddTo(RootCommand root) => AddTo(root, new ProcessRunner());

    internal static void AddTo(RootCommand root, IProcessRunner processes)
    {
        var sim = new Command("sim", "A scripted organization on a scratch hub: the real loop, no model, no tokens.");
        root.Subcommands.Add(sim);

        var report = new Option<string>("--report") { Required = true, Description = "File the session writes its STATUS line to; the sim harness reads it." };
        var cd = new Option<string>("--cd") { Required = true, Description = "The project repository the session works in." };
        var pace = new Option<int?>("--pace") { Description = $"Milliseconds between steps (default ${PaceVariable}, else 1500) so the board can be watched." };
        var agent = new Command("agent", "One scripted session, run by the sim harness with the prompt on stdin. Refuses a home or URL that could be a real hub.")
            { report, cd, pace };
        agent.SetAction((parse, ct) => AgentAsync(parse, parse.GetValue(report)!, parse.GetValue(cd)!, Pace(parse.GetValue(pace)), processes, ct));
        sim.Subcommands.Add(agent);

        var tasks = new Option<int>("--tasks") { DefaultValueFactory = _ => 6, Description = "Tasks to seed. The second asks the founder a question first; the third is built wrong once." };
        var runPace = new Option<int>("--pace") { DefaultValueFactory = _ => 1500, Description = "Milliseconds between a session's steps." };
        var answer = new Option<int>("--auto-answer") { DefaultValueFactory = _ => 20, Description = "Seconds before the founder question is answered for you; 0 leaves it on Needs you for a person." };
        var minutes = new Option<int>("--minutes") { DefaultValueFactory = _ => 20, Description = "Give up watching after this long." };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 0, Description = "Loopback port for the scratch hub (default: a free one)." };
        var keep = new Option<bool>("--keep") { Description = "Leave the scratch hub running and its files in place when done." };
        var run = new Command("run", "Start a scratch hub on the sim harness, seed tasks, turn the conductor on and stream the ledger until every task is done.")
            { tasks, runPace, answer, minutes, port, keep };
        run.SetAction((parse, ct) => RunAsync(parse, new RunOptions(parse.GetValue(tasks), parse.GetValue(runPace), parse.GetValue(answer),
            parse.GetValue(minutes), parse.GetValue(port), parse.GetValue(keep)), processes, ct));
        sim.Subcommands.Add(run);
    }

    internal static int Pace(int? explicitPace) =>
        explicitPace ?? (int.TryParse(Environment.GetEnvironmentVariable(PaceVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnv) ? fromEnv : 1500);

    private static async Task<int> AgentAsync(ParseResult parse, string reportFile, string repo, int pace, IProcessRunner processes, CancellationToken ct)
    {
        if (SimSession.Refusal(Environment.GetEnvironmentVariable(MuthurEnvironment.HomeVariable), MuthurEnvironment.Url) is { } why)
            return Output.Error("sim_refused", why, ExitCodes.RuleViolation);
        var prompt = await Console.In.ReadToEndAsync(ct);
        if (SimSession.Parse(prompt) is not { } brief)
            return Output.Error("sim_prompt_unrecognized", "The prompt names no task, or neither an orchestrator nor a validator session.", ExitCodes.RuleViolation);

        var session = new SimSession(HubClient.For(parse), processes, repo, pace, ct);
        var status = brief.Kind == "validator" ? await session.ValidateAsync(brief.Task, brief.Role!) : await session.OrchestrateAsync(brief.Task);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportFile))!);
        await File.WriteAllTextAsync(reportFile, status, ct);
        Console.Out.WriteLine(status);
        return ExitCodes.Ok;
    }

    internal sealed record RunOptions(int Tasks, int Pace, int AutoAnswerSeconds, int Minutes, int Port, bool Keep);

    /// <summary>Every tier staffed by the sim harness; the local models are absent rather than disabled, since there is nothing to toggle back.</summary>
    internal const string Catalog = """
        {
          "tiers": {
            "mastermind": [ { "harness": "sim", "model": "scripted", "account": "sim" } ],
            "implementer": [ { "harness": "sim", "model": "scripted", "account": "sim" } ],
            "utility": []
          }
        }
        """;

    private const string Brief = """
        # win-validator — sim

        Read the task's branch and check that `<task>.txt` contains exactly the task id, as its spec says.
        Evidence names the command you ran and what it printed.
        """;

    private static async Task<int> RunAsync(ParseResult parse, RunOptions o, IProcessRunner processes, CancellationToken ct)
    {
        if (ServerProcess.Locate() is null)
            return Output.Error("sim_needs_install",
                "Run the sim from an installed CLI (scripts/install.ps1 -Destination <dir>): the scratch hub needs the bundled server, and its sessions need muthur.exe beside it.",
                ExitCodes.RuleViolation);
        if (o.Tasks < 1) return Output.Error("sim_tasks", "Seed at least one task.", ExitCodes.RuleViolation);

        var scratch = Path.Combine(Path.GetTempPath(), "muthur-sim", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var home = Path.Combine(scratch, "home");
        var repo = Path.Combine(scratch, "repo");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(repo);
        var port = o.Port > 0 ? o.Port : FreePort();
        var url = $"http://127.0.0.1:{port}";

        // This process's environment is what the server inherits, and through the server every session it starts.
        Environment.SetEnvironmentVariable(MuthurEnvironment.HomeVariable, home);
        Environment.SetEnvironmentVariable(MuthurEnvironment.UrlVariable, url);
        Environment.SetEnvironmentVariable(MuthurEnvironment.TokenVariable, null);
        Environment.SetEnvironmentVariable(Globals.AgentVariable, null);
        Environment.SetEnvironmentVariable(PaceVariable, o.Pace.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("Muthur__ConductorIntervalSeconds", "5");
        Environment.SetEnvironmentVariable("PATH", AppContext.BaseDirectory.TrimEnd('\\', '/') + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));

        async Task<ProcessResult> Git(params string[] arguments)
        {
            var result = await processes.RunAsync("git", arguments, repo, timeout: TimeSpan.FromSeconds(60), ct: ct);
            if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.Message}");
            return result;
        }

        var stderr = Console.Error;
        try
        {
            await Git("init", "-q", "-b", "main");
            await Git("config", "user.name", "MUTHUR sim");
            await Git("config", "user.email", "sim@example.invalid");
            await Git("config", "commit.gpgsign", "false");
            await Git("config", "core.autocrlf", "false");
            await File.WriteAllTextAsync(Path.Combine(repo, ProjectContext.FileName),
                """{ "key": "sim", "build": "echo sim build", "test": "echo sim test", "notes": "A scratch project driven by muthur sim run." }""", ct);
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# sim\n\nA throwaway repository the sim lands tasks into.\n", ct);
            Directory.CreateDirectory(Path.Combine(repo, "specs"));
            await File.WriteAllTextAsync(Path.Combine(repo, "specs", "_TEMPLATE.md"), "# T-n — title\n\n## Goal\n\n## Verification\n", ct);
            await Git("add", "-A");
            await Git("-c", "commit.gpgsign=false", "commit", "-q", "-m", "sim: initial");
            await Git("checkout", "-q", "--detach");   // nobody's working tree sits on main, so the hub may move it
            await File.WriteAllTextAsync(Path.Combine(home, MuthurEnvironment.HarnessFile), Catalog, ct);

            var (started, failure) = await SystemCommands.StartAsync(ct);
            if (started is null) return Output.Error(failure!.Value.Code, failure.Value.Message);
            var founder = new HubClient(Globals.ReadFounderToken());

            await Must(founder.PostAsync(Routes.Projects, new AddProjectRequest(Project, repo, "Sim project", "main", LandMode.Merge, [Role], []), MuthurJsonContext.Default.AddProjectRequest, ct), "project");
            await Must(founder.PutAsync(Routes.Roles, new DefineRoleRequest(Role, Brief, true, 2), MuthurJsonContext.Default.DefineRoleRequest, ct), "role");
            var seeded = new List<string>();
            for (var i = 1; i <= o.Tasks; i++)
            {
                var (mode, title) = i switch
                {
                    2 => ("ask", "Export invoices (needs a founder decision)"),
                    3 => ("bounce", "Rename the report column (built wrong the first time)"),
                    _ => ("plain", $"Ship feature {i}"),
                };
                var body = $"sim: {mode}\n\nSeeded by muthur sim run. The orchestrator writes {{id}}.txt; the validator reads it back.";
                var added = await Must(founder.PostAsync(Routes.Tasks, new AddTaskRequest(title, Project, body, i <= 2 ? 1 : 0), MuthurJsonContext.Default.AddTaskRequest, ct), "task");
                seeded.Add(JsonSerializer.Deserialize(added.Body, MuthurJsonContext.Default.TaskDto)!.Id);
            }
            await Must(founder.PostAsync(Routes.ConductorSessions, new ConductorSessionsRequest(2, null, null, null, false), MuthurJsonContext.Default.ConductorSessionsRequest, ct), "conductor sessions");
            await Must(founder.PostAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true), MuthurJsonContext.Default.ConductorOrchestratorSwitch, ct), "conductor orchestrators");
            await Must(founder.PostAsync(Routes.Conductor, new ConductorSwitch(true), MuthurJsonContext.Default.ConductorSwitch, ct), "conductor");

            stderr.WriteLine($"sim: hub {url}  home {home}");
            stderr.WriteLine($"sim: board {url}/   needs you {url}/needs-you   stream {url}/stream");
            stderr.WriteLine($"sim: {seeded.Count} tasks seeded ({string.Join(", ", seeded)}); the conductor looks every 5s. Ctrl+C stops the run.");

            var watch = Stopwatch.StartNew();
            var report = await WatchAsync(founder, seeded, o, watch, stderr, ct);
            Console.Out.WriteLine(report);
            return ExitCodes.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stderr.WriteLine("sim: stopped.");
            return ExitCodes.Ok;
        }
        catch (InvalidOperationException ex)
        {
            return Output.Error("sim_failed", ex.Message);
        }
        finally
        {
            if (o.Keep) stderr.WriteLine($"sim: kept running at {url}; MUTHUR_HOME={home}; stop it with: muthur down (with those two variables set)");
            else
            {
                await new HubClient(Globals.ReadFounderToken(), TimeSpan.FromSeconds(5)).PostAsync(Routes.Shutdown, CancellationToken.None);
                await Task.Delay(1500, CancellationToken.None);
                if (!RemoveScratch(scratch)) stderr.WriteLine($"sim: could not remove {scratch}");
            }
        }
    }

    /// <summary>Git marks its objects read-only, which a recursive delete on Windows refuses; clear that first.</summary>
    private static bool RemoveScratch(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<ApiResult> Must(Task<ApiResult> call, string what)
    {
        var result = await call;
        if (!result.IsSuccess) throw new InvalidOperationException($"{what}: {result.Status} {result.Body}");
        return result;
    }

    private sealed class Phases
    {
        public DateTimeOffset? Added, Claimed, Implemented, Verdict, Landed;
        public int Sessions, Bounces, Questions;
    }

    /// <summary>Streams the ledger to stderr until every seeded task is done, answering the founder question if asked to, and returns the friction report.</summary>
    private static async Task<string> WatchAsync(HubClient founder, IReadOnlyList<string> seeded, RunOptions o, Stopwatch watch, TextWriter stderr, CancellationToken ct)
    {
        var phases = seeded.ToDictionary(id => id, _ => new Phases());
        var answered = new HashSet<int>();
        long since = 0;
        var done = false;
        while (!ct.IsCancellationRequested && watch.Elapsed < TimeSpan.FromMinutes(o.Minutes) && !done)
        {
            var page = await founder.GetAsync($"{Routes.Events}?since={since}&limit=200", ct);
            if (page.IsSuccess)
            {
                foreach (var e in JsonSerializer.Deserialize(page.Body, MuthurJsonContext.Default.IReadOnlyListEventDto) ?? [])
                {
                    since = e.Seq;
                    stderr.WriteLine($"{e.At.ToLocalTime():HH:mm:ss}  {e.Type,-30} {e.TaskId,-6} {e.Actor}");
                    if (e.TaskId is null || !phases.TryGetValue(e.TaskId, out var p)) continue;
                    switch (e.Type)
                    {
                        case "task.added": p.Added ??= e.At; break;
                        case "task.claimed": p.Claimed ??= e.At; p.Sessions++; break;
                        case "task.implemented": p.Implemented = e.At; break;
                        case "task.validated": p.Verdict = e.At; break;
                        case "task.validation_failed": p.Verdict = e.At; p.Bounces++; break;
                        case "request.asked": p.Questions++; break;
                        case "task.landed": p.Landed = e.At; break;
                    }
                }
            }
            if (o.AutoAnswerSeconds > 0)
            {
                var open = await founder.GetAsync(Routes.Requests, ct);
                if (open.IsSuccess)
                    foreach (var request in JsonSerializer.Deserialize(open.Body, MuthurJsonContext.Default.IReadOnlyListFounderRequestDto) ?? [])
                    {
                        if (answered.Contains(request.Id) || request.CreatedAt.AddSeconds(o.AutoAnswerSeconds) > DateTimeOffset.UtcNow) continue;
                        var choice = request.Options.Count > 0 ? request.Options[0] : "yes";
                        if ((await founder.PostAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest(choice), MuthurJsonContext.Default.AnswerRequest, ct)).IsSuccess)
                        {
                            answered.Add(request.Id);
                            stderr.WriteLine($"{DateTimeOffset.Now:HH:mm:ss}  founder answered request {request.Id} for {request.Task}: {choice}");
                        }
                    }
            }
            done = phases.Values.All(p => p.Landed is not null);
            if (!done) await Task.Delay(1000, ct);
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteBoolean("complete", done);
            json.WriteNumber("modelCalls", 0);
            json.WriteNumber("landed", phases.Values.Count(p => p.Landed is not null));
            json.WriteNumber("seconds", Math.Round(watch.Elapsed.TotalSeconds, 1));
            json.WriteStartArray("tasks");
            foreach (var (id, p) in phases)
            {
                json.WriteStartObject();
                json.WriteString("task", id);
                json.WriteNumber("sessions", p.Sessions);
                json.WriteNumber("questions", p.Questions);
                json.WriteNumber("bounces", p.Bounces);
                Seconds(json, "backlogToClaim", p.Added, p.Claimed);
                Seconds(json, "claimToImplemented", p.Claimed, p.Implemented);
                Seconds(json, "implementedToVerdict", p.Implemented, p.Verdict);
                Seconds(json, "verdictToLanded", p.Verdict, p.Landed);
                Seconds(json, "total", p.Added, p.Landed);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Seconds(Utf8JsonWriter json, string name, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is { } f && to is { } t) json.WriteNumber(name, Math.Round((t - f).TotalSeconds, 1));
        else json.WriteNull(name);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
