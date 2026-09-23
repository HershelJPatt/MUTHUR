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
    public const string AskWaitVariable = "MUTHUR_SIM_ASK_WAIT";
    /// <summary>Where every scripted session logs what it was handed: one "kind task promptBytes" line each, the run's token proxy.</summary>
    public const string SessionsLog = "sim-sessions.log";
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
        var askWait = new Option<int?>("--ask-wait") { Description = $"Seconds an orchestrator waits in-session for the founder's answer before leaving notes and exiting (default ${AskWaitVariable}, else 60)." };
        var model = new Option<string?>("--model") { Description = "The catalog model this session runs as; `limited` fails the way a rate-limited CLI does." };
        var agent = new Command("agent", "One scripted session, run by the sim harness with the prompt on stdin. Refuses a home or URL that could be a real hub.")
            { report, cd, pace, askWait, model };
        agent.SetAction((parse, ct) => AgentAsync(parse, parse.GetValue(report)!, parse.GetValue(cd)!, Pace(parse.GetValue(pace)), AskWait(parse.GetValue(askWait)), parse.GetValue(model), processes, ct));
        sim.Subcommands.Add(agent);

        var tasks = new Option<int>("--tasks") { DefaultValueFactory = _ => 6, Description = "Tasks to seed. The second asks the founder a question first; the third is built wrong once; the fourth's assignment check fails once." };
        var runPace = new Option<int>("--pace") { DefaultValueFactory = _ => 1500, Description = "Milliseconds between a session's steps." };
        var answer = new Option<int>("--auto-answer") { DefaultValueFactory = _ => 20, Description = "Seconds before the founder question is answered for you; 0 leaves it on Needs you for a person." };
        var runAskWait = new Option<int>("--ask-wait") { DefaultValueFactory = _ => 60, Description = "Seconds an orchestrator waits in-session for the answer before leaving notes and exiting. Below --auto-answer exercises the resume-from-notes path." };
        var minutes = new Option<int>("--minutes") { DefaultValueFactory = _ => 20, Description = "Give up watching after this long." };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 0, Description = "Loopback port for the scratch hub (default: a free one)." };
        var keep = new Option<bool>("--keep") { Description = "Leave the scratch hub running and its files in place when done." };
        var harness = new Option<string?>("--harness") { Description = "Staff the mastermind and implementer tiers on a real harness (claude, codex, codex-oss) instead of the scripted one; validation stays deterministic. The matching kit is installed into the scratch repository." };
        var runModel = new Option<string?>("--model") { Description = "The model for --harness, e.g. gemma4:26b on codex-oss." };
        var effort = new Option<string?>("--effort") { Description = "Reasoning effort for --harness (low, medium, high)." };
        var run = new Command("run", "Start a scratch hub on the sim harness, seed tasks, turn the conductor on and stream the ledger until every task is done.")
            { tasks, runPace, answer, runAskWait, minutes, port, keep, harness, runModel, effort };
        run.SetAction((parse, ct) => RunAsync(parse, new RunOptions(parse.GetValue(tasks), parse.GetValue(runPace), parse.GetValue(answer),
            parse.GetValue(minutes), parse.GetValue(port), parse.GetValue(keep), parse.GetValue(runAskWait),
            parse.GetValue(harness), parse.GetValue(runModel), parse.GetValue(effort)), processes, ct));
        sim.Subcommands.Add(run);
    }

    internal static int Pace(int? explicitPace) =>
        explicitPace ?? (int.TryParse(Environment.GetEnvironmentVariable(PaceVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnv) ? fromEnv : 1500);

    internal static int AskWait(int? explicitWait) =>
        explicitWait ?? (int.TryParse(Environment.GetEnvironmentVariable(AskWaitVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnv) ? fromEnv : 60);

    private static async Task<int> AgentAsync(ParseResult parse, string reportFile, string repo, int pace, int askWait, string? model, IProcessRunner processes, CancellationToken ct)
    {
        var home = Environment.GetEnvironmentVariable(MuthurEnvironment.HomeVariable);
        if (SimSession.Refusal(home, MuthurEnvironment.Url) is { } why)
            return Output.Error("sim_refused", why, ExitCodes.RuleViolation);
        var prompt = await Console.In.ReadToEndAsync(ct);
        // An account out of quota answers nothing at all: the launcher's rate-limit detection is what gets exercised.
        if (SimSession.IsLimited(model))
        {
            Console.Error.WriteLine(SimSession.UsageLimitMessage);
            return ExitCodes.Error;
        }
        if (SimSession.Parse(prompt) is not { } brief)
            return Output.Error("sim_prompt_unrecognized", "The prompt names no task, or none of an orchestrator, implementer or validator session.", ExitCodes.RuleViolation);
        // What a model would have read before its first action, in bytes: the launcher's prompt, then the kit files
        // the role's procedure sends it to. The closest thing to a token count the sim has.
        var kitBytes = KitBytes(brief.Kind, repo, KitCommands.LocateKit());
        try { await File.AppendAllTextAsync(Path.Combine(home!, SessionsLog), $"{brief.Kind}\t{brief.Task}\t{Encoding.UTF8.GetByteCount(prompt)}\t{kitBytes}\n", ct); }
        catch (IOException) { }

        // The inbox wait blocks server-side for up to --ask-wait seconds; the client must outlast it. An implementer
        // has no hub identity (the launcher scrubs it) and needs none: its whole world is the worktree.
        var session = new SimSession(HubClient.For(parse, TimeSpan.FromSeconds(askWait + 30)), processes, repo, pace, askWait, ct);
        var status = brief.Kind switch
        {
            "validator" => await session.ValidateAsync(brief.Task, brief.Role!),
            "implementer" => await session.ImplementAsync(brief.Task, prompt),
            _ => await session.OrchestrateAsync(brief.Task),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportFile))!);
        await File.WriteAllTextAsync(reportFile, status, ct);
        Console.Out.WriteLine(status);
        return ExitCodes.Ok;
    }

    internal sealed record RunOptions(int Tasks, int Pace, int AutoAnswerSeconds, int Minutes, int Port, bool Keep, int AskWaitSeconds = 60,
        string? Harness = null, string? Model = null, string? Effort = null)
    {
        /// <summary>Whether the mastermind and implementer tiers run a real model rather than the scripted session.</summary>
        public bool RealHarness => Harness is { Length: > 0 } && !Harness.Equals("sim", StringComparison.OrdinalIgnoreCase);

        /// <summary>The kit a real harness reads its procedures from: the Codex kit serves the local-provider variant too.</summary>
        public string? Kit => !RealHarness ? null
            : Harness!.StartsWith("codex", StringComparison.OrdinalIgnoreCase) || Harness.Equals("pi", StringComparison.OrdinalIgnoreCase) ? "codex"   // pi reads AGENTS.md too
            : Harness.ToLowerInvariant();
    }

    /// <summary>
    /// Every tier staffed by the sim harness; the local models are absent rather than disabled, since there is
    /// nothing to toggle back. The implementer tier leads with an account out of quota, the way the live catalog
    /// did for six hours: what the launchers do about it is the thing the run measures.
    /// </summary>
    internal const string Catalog = """
        {
          "tiers": {
            "mastermind": [ { "harness": "sim", "model": "scripted", "account": "sim" } ],
            "implementer": [
              { "harness": "sim", "model": "limited", "account": "sim-limited" },
              { "harness": "sim", "model": "scripted", "account": "sim" }
            ],
            "utility": []
          }
        }
        """;

    /// <summary>
    /// The catalog for a run: the scripted one, or one that staffs both model tiers on a real harness. The limited
    /// scripted candidate stays at the head of the implementer tier either way, so the fall-through is still measured;
    /// the real candidate's account is "local", which is what the worker launcher's local-tier rules key on.
    /// </summary>
    internal static string CatalogFor(RunOptions o)
    {
        if (!o.RealHarness) return Catalog;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            void Candidate()
            {
                json.WriteStartObject();
                json.WriteString("harness", o.Harness!.ToLowerInvariant());
                json.WriteString("model", o.Model ?? "");
                json.WriteString("account", "local");
                if (o.Effort is { Length: > 0 } effort) json.WriteString("reasoningEffort", effort);
                json.WriteEndObject();
            }
            json.WriteStartObject();
            json.WriteStartObject("tiers");
            json.WriteStartArray("mastermind"); Candidate(); json.WriteEndArray();
            json.WriteStartArray("implementer");
            json.WriteStartObject();
            json.WriteString("harness", "sim"); json.WriteString("model", "limited"); json.WriteString("account", "sim-limited");
            json.WriteEndObject();
            Candidate();
            json.WriteEndArray();
            json.WriteStartArray("utility"); json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

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
        Environment.SetEnvironmentVariable(AskWaitVariable, o.AskWaitSeconds.ToString(CultureInfo.InvariantCulture));
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
                """{ "key": "sim", "build": "echo sim build", "test": "echo sim test", "checksOnlyValidation": true, "notes": "A scratch project driven by muthur sim run." }""", ct);
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# sim\n\nA throwaway repository the sim lands tasks into.\n", ct);
            Directory.CreateDirectory(Path.Combine(repo, "specs"));
            await File.WriteAllTextAsync(Path.Combine(repo, "specs", "_TEMPLATE.md"), "# T-n — title\n\n## Goal\n\n## Verification\n", ct);
            if (o.Kit is { } kit)
            {
                // A real model reads its procedures from the installed kit, exactly as it would in a real project.
                var installed = KitCommands.InstallInto(parse, kit, repo, Project);
                if (installed != ExitCodes.Ok) return installed;
            }
            await Git("add", "-A");
            await Git("-c", "commit.gpgsign=false", "commit", "-q", "-m", "sim: initial");
            await Git("checkout", "-q", "--detach");   // nobody's working tree sits on main, so the hub may move it
            await File.WriteAllTextAsync(Path.Combine(home, MuthurEnvironment.HarnessFile), CatalogFor(o), ct);

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
                    4 => ("block", "Unit whose assignment check fails (blocked once)"),
                    _ => ("plain", $"Ship feature {i}"),
                };
                var body = $"sim: {mode}\n\nSeeded by muthur sim run. The orchestrator specs it, an implementer writes {{id}}.txt, the validator reads it back.";
                var added = await Must(founder.PostAsync(Routes.Tasks, new AddTaskRequest(title, Project, body, i <= 2 ? 1 : 0), MuthurJsonContext.Default.AddTaskRequest, ct), "task");
                seeded.Add(JsonSerializer.Deserialize(added.Body, MuthurJsonContext.Default.TaskDto)!.Id);
            }
            await Must(founder.PostAsync(Routes.ConductorSessions, new ConductorSessionsRequest(2, null, null, null, false), MuthurJsonContext.Default.ConductorSessionsRequest, ct), "conductor sessions");
            await Must(founder.PostAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true), MuthurJsonContext.Default.ConductorOrchestratorSwitch, ct), "conductor orchestrators");
            await Must(founder.PostAsync(Routes.Conductor, new ConductorSwitch(true), MuthurJsonContext.Default.ConductorSwitch, ct), "conductor");

            stderr.WriteLine($"sim: hub {url}  home {home}" + (o.RealHarness ? $"  harness {o.Harness}/{o.Model}" : ""));
            stderr.WriteLine($"sim: board {url}/   needs you {url}/needs-you   stream {url}/stream");
            var status = await founder.GetAsync(Routes.Conductor, ct);
            var sweep = status.IsSuccess ? JsonSerializer.Deserialize(status.Body, MuthurJsonContext.Default.ConductorStatusDto)?.IntervalSeconds : null;
            stderr.WriteLine($"sim: {seeded.Count} tasks seeded ({string.Join(", ", seeded)}); the conductor wakes on ledger events and sweeps every {sweep?.ToString(CultureInfo.InvariantCulture) ?? "?"}s. Ctrl+C stops the run.");

            var watch = Stopwatch.StartNew();
            var report = await WatchAsync(founder, seeded, o, watch, stderr, Path.Combine(home, SessionsLog), ct);
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
        public int Sessions, Validators, Bounces, Questions;
        /// <summary>Model sessions started for this task: every one is a cold start that reads the task from nothing.</summary>
        public int ColdStarts => Sessions + Validators;
        /// <summary>Seconds the task spent waiting on the hub between phases — none of it work, all of it schedule.</summary>
        public double? Wait => Added is { } a && Claimed is { } c && Implemented is { } i && Verdict is { } v && Landed is { } l
            ? (c - a + (v - i) + (l - v)).TotalSeconds : null;
    }

    /// <summary>What the launchers recorded across the run, read off the ledger as it streams past.</summary>
    internal sealed class Runs
    {
        public int WorkerRuns, WorkerBlocked, LaunchesIntoLimitedAccount, AccountLimits;
        /// <summary>Real model processes started (sessions and worker runs on a harness other than sim), the tokens they reported, and their wall time.</summary>
        public int ModelCalls; public long ModelTokens; public double ModelSeconds;

        public void Count(EventDto e)
        {
            var payload = e.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : e.Payload.GetRawText();
            switch (e.Type)
            {
                case "worker.finished" or "worker.failed":
                    WorkerRuns++;
                    if (e.Payload.ValueKind == JsonValueKind.Object && e.Payload.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "blocked")
                        WorkerBlocked++;
                    CountModel(e.Payload, harnessKey: "worker");
                    break;
                case "conductor.session_finished":
                    CountModel(e.Payload, harnessKey: "harness");
                    break;
                case "account.limited":
                    AccountLimits++;
                    break;
            }
            // A start that hit the limit shows either as the failed session's error text or, when the launcher fell
            // through to the next candidate, as a "quota" attempt inside the run it eventually reported.
            if (e.Type is "conductor.session_failed" or "worker.failed" or "worker.finished" &&
                (payload.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || payload.Contains("\"quota\"", StringComparison.Ordinal)))
                LaunchesIntoLimitedAccount++;
        }

        /// <summary>A model row is one whose harness is not the scripted sim; its tokens are input plus output, or the one total a CLI prints.</summary>
        private void CountModel(JsonElement payload, string harnessKey)
        {
            if (payload.ValueKind != JsonValueKind.Object) return;
            var harness = payload.TryGetProperty(harnessKey, out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
            if (harness.Length == 0 || harness.StartsWith("sim", StringComparison.OrdinalIgnoreCase)) return;
            if (payload.TryGetProperty("started", out var started) && started.ValueKind == JsonValueKind.False) return;
            ModelCalls++;
            long Number(string name) => payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
            var split = Number("inputTokens") + Number("outputTokens");
            ModelTokens += split > 0 ? split : Number("totalTokens");
            ModelSeconds += Number("seconds");
        }
    }

    /// <summary>
    /// Reads the sessions log the agents wrote: prompt bytes and kit bytes by session. A three-column line from an
    /// older agent still counts, with no kit bytes; a malformed one is skipped, never a crash at the end of a run.
    /// </summary>
    internal static (int Sessions, long Bytes, long KitBytes) PromptBytes(string path)
    {
        if (!File.Exists(path)) return (0, 0, 0);
        var sessions = 0; long bytes = 0, kit = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split('\t');
            if (parts.Length is not (3 or 4) || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            sessions++;
            bytes += n;
            if (parts.Length == 4 && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)) kit += k;
        }
        return (sessions, bytes, kit);
    }

    /// <summary>
    /// The bytes of the kit files a role's procedure sends it to read after the prompt: from the kit installed in
    /// the repository when there is one (the skill has the core procedure expanded into it), else from the source
    /// kit. An implementer's prompt inlines its whole contract, so it reads nothing more. No kit at all counts 0.
    /// </summary>
    internal static long KitBytes(string kind, string repo, string? kit)
    {
        var skills = Path.Combine(repo, ".claude", "skills");
        var installed = File.Exists(Path.Combine(skills, "muthur-orchestrate", "SKILL.md"));
        string[] files = (kind, installed) switch
        {
            ("orchestrator", true) => [Path.Combine(skills, "muthur-orchestrate", "SKILL.md"), Path.Combine(skills, "muthur-orchestrate", "reference.md"), Path.Combine(repo, "specs", "_TEMPLATE.md")],
            ("validator", true) => [Path.Combine(skills, "muthur-validate", "SKILL.md"), Path.Combine(repo, "briefs", "validator.md")],
            ("orchestrator", false) when kit is not null => [Path.Combine(kit, "core", "orchestrate.md"), Path.Combine(kit, "core", "orchestrate-reference.md"),
                Path.Combine(kit, "core", "spec-template.md"), Path.Combine(kit, "claude", "skills", "muthur-orchestrate.md")],
            ("validator", false) when kit is not null => [Path.Combine(kit, "core", "validate.md"), Path.Combine(kit, "briefs", "validator.md"), Path.Combine(kit, "claude", "skills", "muthur-validate.md")],
            _ => [],
        };
        return files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
    }

    /// <summary>Streams the ledger to stderr until every seeded task is done, answering the founder question if asked to, and returns the friction report.</summary>
    private static async Task<string> WatchAsync(HubClient founder, IReadOnlyList<string> seeded, RunOptions o, Stopwatch watch, TextWriter stderr, string sessionsLog, CancellationToken ct)
    {
        var phases = seeded.ToDictionary(id => id, _ => new Phases());
        var answered = new HashSet<int>();
        var runs = new Runs();
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
                    runs.Count(e);
                    if (e.TaskId is null || !phases.TryGetValue(e.TaskId, out var p)) continue;
                    switch (e.Type)
                    {
                        case "task.added": p.Added ??= e.At; break;
                        case "task.claimed": p.Claimed ??= e.At; p.Sessions++; break;
                        case "validation.claimed": p.Validators++; break;
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
            var landed = phases.Values.Count(p => p.Landed is not null);
            var coldStarts = phases.Values.Sum(p => p.ColdStarts);
            var (promptSessions, promptBytes, kitBytes) = PromptBytes(sessionsLog);
            var waits = phases.Values.Where(p => p.Wait is not null).Select(p => p.Wait!.Value).ToList();
            json.WriteBoolean("complete", done);
            if (o.RealHarness) json.WriteString("harness", $"{o.Harness}/{o.Model}");
            // Zero on the scripted harness by construction; on a real one, every process the launchers started, what
            // those processes said they used, and how long they ran — the three numbers a harness comparison needs.
            json.WriteNumber("modelCalls", runs.ModelCalls);
            json.WriteNumber("modelTokens", runs.ModelTokens);
            json.WriteNumber("modelSeconds", Math.Round(runs.ModelSeconds, 1));
            if (runs.ModelCalls > 0) json.WriteNumber("modelTokensPerCall", runs.ModelTokens / runs.ModelCalls);
            json.WriteNumber("landed", landed);
            json.WriteNumber("seconds", Math.Round(watch.Elapsed.TotalSeconds, 1));
            // The numbers tuning is judged on: what a task costs in cold starts (each one a full context load for a
            // model), what each cold start reads before its first action, and seconds spent waiting on the schedule
            // rather than on work.
            json.WriteNumber("coldStarts", coldStarts);
            if (landed > 0) json.WriteNumber("coldStartsPerLanded", Math.Round((double)coldStarts / landed, 2));
            json.WriteNumber("promptBytes", promptBytes);
            json.WriteNumber("kitBytes", kitBytes);
            if (promptSessions > 0) json.WriteNumber("promptBytesPerColdStart", promptBytes / promptSessions);
            if (promptSessions > 0) json.WriteNumber("contextBytesPerColdStart", (promptBytes + kitBytes) / promptSessions);
            if (waits.Count > 0) json.WriteNumber("waitSecondsPerLanded", Math.Round(waits.Average(), 1));
            // What the launchers did: how often a worker ran, how often it came back blocked, and how many starts
            // went into an account that was out of quota before anything noticed.
            json.WriteNumber("workerRuns", runs.WorkerRuns);
            json.WriteNumber("workerBlocked", runs.WorkerBlocked);
            json.WriteNumber("launchesIntoLimitedAccount", runs.LaunchesIntoLimitedAccount);
            json.WriteNumber("accountLimits", runs.AccountLimits);
            json.WriteStartArray("tasks");
            foreach (var (id, p) in phases)
            {
                json.WriteStartObject();
                json.WriteString("task", id);
                json.WriteNumber("sessions", p.Sessions);
                json.WriteNumber("validators", p.Validators);
                json.WriteNumber("coldStarts", p.ColdStarts);
                json.WriteNumber("questions", p.Questions);
                json.WriteNumber("bounces", p.Bounces);
                Seconds(json, "backlogToClaim", p.Added, p.Claimed);
                Seconds(json, "claimToImplemented", p.Claimed, p.Implemented);
                Seconds(json, "implementedToVerdict", p.Implemented, p.Verdict);
                Seconds(json, "verdictToLanded", p.Verdict, p.Landed);
                Seconds(json, "total", p.Added, p.Landed);
                if (p.Wait is { } wait) json.WriteNumber("wait", Math.Round(wait, 1)); else json.WriteNull("wait");
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
