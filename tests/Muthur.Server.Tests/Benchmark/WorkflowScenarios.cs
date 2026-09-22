using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests.Benchmark;

public sealed class WorkflowScenarios : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new(integrationChecks: true);
    private readonly List<HttpClient> _clients = [];
    private readonly TrialReport _report;
    private HttpClient _founder = null!;
    private string? _id;
    private string? _branch;
    private const string Role = "win-validator";

    public WorkflowScenarios(TrialReport report)
    {
        _report = report;
        _repo.Git("config", "core.longpaths", "true");
    }

    private HttpClient Track(HttpClient client) { _clients.Add(client); return client; }

    private async Task<HttpClient> Agent(string name)
    {
        // Registration responses contain credentials: consume them without adding them to evidence.
        using var anonymous = _hub.CreateClient();
        using var response = await anonymous.PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest(name, "scripted", "offline"));
        response.EnsureSuccessStatusCode();
        var registration = await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse);
        return Track(_hub.CreateClient(registration!.Token));
    }

    private async Task<JsonElement> Send(HttpClient client, string path, object? body = null, int status = 200, string method = "POST")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = JsonContent.Create(body, options: WorkflowReport.Json);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? JsonSerializer.SerializeToElement<object?>(null) : JsonDocument.Parse(text).RootElement.Clone();
        _report.Http.Add(new(method, path, (int)response.StatusCode, json,
            body is null ? null : JsonSerializer.SerializeToElement(body, WorkflowReport.Json)));
        Assert.True((int)response.StatusCode == status, $"{method} {path}: expected {status}, got {(int)response.StatusCode}: {text}");
        return json;
    }

    private Task<JsonElement> Action(HttpClient client, string id, string action, object? body = null, int status = 200) =>
        Send(client, Routes.TaskAction(id, action), body, status);

    private async Task<string> Add(HttpClient owner, string title) =>
        (await Send(owner, Routes.Tasks, new AddTaskRequest(title, "demo"))).GetProperty("id").GetString()!;

    private async Task Spec(HttpClient owner, string id)
    {
        var branch = $"task/{id}-work";
        _repo.BranchWithFile(branch, $"specs/{id}.md", $"# {id} — Return 42\n");
        await Action(owner, id, "spec", new SetSpecRequest($"specs/{id}.md", branch));
    }

    private void Implement(string id, string result)
    {
        _repo.Git("checkout", "-q", $"task/{id}-work");
        _repo.Write("result.txt", result);
        _repo.Commit("scripted result");
        _repo.Git("checkout", "-q", "main");
        _report.WorkerRuns++;
    }

    private async Task Submit(HttpClient owner, string id) =>
        CaptureSubject(id, "implemented", await Action(owner, id, "implemented", new ImplementedRequest($"task/{id}-work")));

    public static string? SubjectId(JsonElement response)
    {
        if (!response.TryGetProperty("currentSubject", out var subject)) return null;
        if (subject.ValueKind != JsonValueKind.Object || !subject.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _))
            throw new InvalidDataException("Invalid validation subject in response.");
        return id.GetString();
    }

    private string? CaptureSubject(string id, string action, JsonElement response)
    {
        var subject = SubjectId(response);
        _report.ValidationSubjects.Add(new(id, action, subject, subject is null ? "unmeasured" : "observed",
            subject is null ? "Legacy response has no currentSubject; subject binding is unavailable." : null));
        return subject;
    }

    private async Task<string?> ClaimValidation(HttpClient validator, string id) =>
        CaptureSubject(id, "validate-claim", await Action(validator, id, "validate-claim", new ClaimValidationRequest(Role)));

    private Task<JsonElement> Verdict(HttpClient validator, string id, string? subject, string evidence, int status = 200) =>
        Action(validator, id, "pass", new { validator = Role, evidence, subjectId = subject }, status);

    private async Task<HttpClient> Validator(string name)
    {
        var validator = await Agent(name);
        await Send(validator, Routes.RoleAction(Role, "take"));
        return validator;
    }

    private async Task Approve(HttpClient validator, string id, bool negative = false)
    {
        await ApproveClaim(validator, id, await ClaimValidation(validator, id), negative);
    }

    private async Task ApproveClaim(HttpClient validator, string id, string? subject, bool negative = false)
    {
        if (!negative) Assert.Equal("42", _repo.Git("show", $"task/{id}-work:result.txt"));
        await Verdict(validator, id, subject,
            negative ? "Scripted false approval: claimed success without checking product" : "Independent scripted read of task branch result.txt equals 42");
    }

    private void Advance(string name, int minutes, bool blocked = false)
    {
        var start = _hub.Clock.GetUtcNow();
        _hub.Clock.Advance(TimeSpan.FromMinutes(minutes));
        _report.FakePhases.Add(new(name, start, _hub.Clock.GetUtcNow(), blocked));
    }

    private void Check(string name, bool condition)
    {
        _report.Checks[name] = condition;
        Assert.True(condition, name);
    }

    public async Task Run(Scenario scenario)
    {
        if (scenario.Id == "no-verdict-loop")
        {
            _hub.Settings["Muthur:ConductorEnabled"] = "true";
            _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "0";
            _hub.Settings["Muthur:ConductorMaxAttempts"] = "3";
            _hub.Settings["Muthur:ConductorStallProbeMinutes"] = "30";
        }
        _founder = Track(_hub.Founder());
        await Send(_founder, Routes.Projects, new AddProjectRequest("demo", _repo.Path, RequiredValidators: [Role], IngestSources: []));
        await Send(_founder, Routes.Roles, new DefineRoleRequest(Role, "Independently inspect result.txt", Holders: 2), method: "PUT");
        var owner = await Agent("owner");
        _id = await Add(owner, scenario.Id);
        _branch = $"task/{_id}-work";
        await Action(owner, _id, "claim", new ClaimTaskRequest());

        if (scenario.Id == "stale-spec")
        {
            var absent = await Action(owner, _id, "spec", new SetSpecRequest("specs/missing.md"), 422);
            Check("absent-spec-rejected", absent.GetProperty("code").GetString() == "spec_missing");
            _repo.BranchWithFile("task/wrong", "specs/wrong.md", "# T-999 — wrong task\n");
            var wrong = await Action(owner, _id, "spec", new SetSpecRequest("specs/wrong.md", "task/wrong"), 422);
            Check("wrong-task-rejected", wrong.GetProperty("code").GetString() == "spec_id_mismatch");
        }
        if (scenario.Id == "interrupted-owner")
        {
            Advance("owner-lease", 31);
            Check("owner-expired", await _hub.Services.GetRequiredService<TaskService>().SweepExpiredClaimsAsync() == 1);
            owner = await Agent("replacement");
            await Action(owner, _id, "claim", new ClaimTaskRequest());
            Check("replacement-claimed", (await owner.GetTaskAsync(_id)).Task.Owner == "replacement");
        }
        await Spec(owner, _id);
        if (scenario.Id == "stale-spec") Check("committed-spec-attached", !File.Exists(Path.Combine(_repo.Path, $"specs/{_id}.md")));

        if (scenario.Id == "dependency-wait")
        {
            var prerequisite = await Add(owner, "prerequisite");
            await Action(owner, _id, "dependencies", new DependenciesRequest([prerequisite], "Wait for shared result"));
            await Action(owner, _id, "claim", new ClaimTaskRequest(), 409);
            Check("dependent-refused", (await owner.GetTaskAsync(_id)).Task.State == TaskState.Backlog);
            Advance("dependency-wait", 10, true);
            await Action(owner, prerequisite, "claim", new ClaimTaskRequest());
            await Spec(owner, prerequisite);
            Implement(prerequisite, "42\n");
            await Submit(owner, prerequisite);
            var prerequisiteValidator = await Validator("prerequisite-validator");
            await Approve(prerequisiteValidator, prerequisite);
            await _hub.PassIntegrationAsync(_repo, prerequisite);
            await Action(owner, prerequisite, "land");
            await Send(prerequisiteValidator, Routes.RoleAction(Role, "release"));
            Check("prerequisite-done", (await owner.GetTaskAsync(prerequisite)).Task.State == TaskState.Done);
            await Action(owner, _id, "claim", new ClaimTaskRequest());
            _repo.Git("checkout", "-q", _branch);
            _repo.Git("rebase", "main");
            // The dependent makes its own observable commit even though the prerequisite already returns 42.
            _repo.Write("dependent.txt", "prerequisite complete\n");
            _repo.Commit("resume dependent");
            _repo.Git("checkout", "-q", "main");
            Check("dependent-resumed", (await owner.GetTaskAsync(_id)).Task.Owner == "owner");
        }
        if (scenario.Id != "dependency-wait") Implement(_id, scenario.Id == "false-green" ? "41\n" : "42\n");
        else _report.WorkerRuns++;
        await Submit(owner, _id);

        if (scenario.Id == "no-verdict-loop")
        {
            var conductor = _hub.Services.GetRequiredService<ConductorService>();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Assert.Equal(1, await conductor.RunPassAsync());
                var expected = attempt;
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    var observed = await _founder.GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
                    if (conductor.RunningCount == 0 && observed!.Count(e => e.Type == "conductor.no_verdict") == expected) break;
                    Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(60), "scripted conductor session did not settle");
                    await Task.Yield();
                }
            }
            for (var pass = 0; pass < 3; pass++) Assert.Equal(0, await conductor.RunPassAsync());
            Check("stopped-at-cap", _hub.Validators.Started.Count == 3);
            Advance("cooldown", 29, true);
            Assert.Equal(0, await conductor.RunPassAsync());
            Check("cooldown-held", _hub.Validators.Started.Count == 3);
            var events = await _founder.GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
            Check("attempts-observed", events!.Count(e => e.Type == "conductor.no_verdict") == 3
                && events!.Count(e => e.Type == "conductor.staffing") == 3);
            var messages = await _founder.GetFromJsonAsync($"{Routes.Messages}?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto);
            Check("one-escalation", events!.Count(e => e.Type == "conductor.stalled") == 1
                && messages!.Count(m => m.Body.Contains("reached a verdict", StringComparison.Ordinal)) == 1);
            await Send(_founder, $"{Routes.Messages}?founder=true", method: "GET");
            return;
        }

        var validator = await Validator("validator");
        if (scenario.Id == "late-verdict")
        {
            var firstSubject = await ClaimValidation(validator, _id);
            var second = await Validator("current-validator");
            for (var i = 0; i < 3; i++)
            {
                Advance("validation-lease", 11);
                await Send(second, Routes.AgentHeartbeat, new HeartbeatRequest());
            }
            Check("first-claim-expired", await _hub.Services.GetRequiredService<LifecycleService>().SweepExpiredValidationClaimsAsync() == 1);
            var secondSubject = await ClaimValidation(second, _id);
            await Send(validator, Routes.RoleAction(Role, "take"));
            var stale = await Verdict(validator, _id, firstSubject, "Expired validator scripted read of task branch result.txt equals 42; submitting the original claim", 409);
            Check("stale-verdict-refused", stale.GetProperty("code").GetString() == "validation_claimed");
            validator = second;
            await ApproveClaim(validator, _id, secondSubject);
        }
        else await Approve(validator, _id, scenario.Id == "false-green");
        if (scenario.Id == "conflicting-branches")
        {
            _repo.Git("checkout", "-q", "main");
            _repo.Write("result.txt", "conflicting main\n");
            _repo.Commit("concurrent main update");
            _repo.Git("checkout", "-q", "--detach");
            var before = _repo.Git("rev-parse", "main");
            // The merge is made by integration now, so that is where the conflict is found and refused.
            var conflicted = await _hub.RunIntegrationAsync(_id);
            Check("conflict-refused-by-integration", !conflicted.Passed && conflicted.Failure?.StartsWith("merge_conflict:", StringComparison.Ordinal) == true);
            Check("conflict-preserves-main", before == _repo.Git("rev-parse", "main") && _repo.Git("show", "main:result.txt") == "conflicting main");
            Check("conflict-not-done", (await owner.GetTaskAsync(_id)).Task.State == TaskState.InProgress);
            _repo.Git("checkout", "-q", _branch);
            // Resolve the add/add conflict while replaying the scripted result onto the new main.
            try { _repo.Git("rebase", "main"); }
            catch (InvalidOperationException)
            {
                Assert.True(Directory.Exists(Path.Combine(_repo.Path, ".git/rebase-merge")));
                _repo.Write("result.txt", "42\n");
                _repo.Git("add", "result.txt");
                _repo.Git("-c", "core.editor=true", "rebase", "--continue");
            }
            _repo.Git("checkout", "-q", "main");
            _report.WorkerRuns++;
            await Submit(owner, _id);
            await Action(owner, _id, "land", status: 422);
            await Approve(validator, _id);
            Check("fresh-validation-required", (await owner.GetTaskAsync(_id)).Events.Count(e => e.Type == "task.implemented") == 2);
        }
        await _hub.PassIntegrationAsync(_repo, _id);
        await Action(owner, _id, "land");
        var completed = await owner.GetTaskAsync(_id);
        Check("landed", completed.Task.State == TaskState.Done);
        if (scenario.Id == "interrupted-owner")
        {
            var tasks = await _founder.GetFromJsonAsync(Routes.Tasks, MuthurJsonContext.Default.IReadOnlyListTaskDto);
            Check("no-duplicate-completion", tasks!.Count(t => t.State == TaskState.Done) == 1 && tasks!.Count == 1);
        }
        if (scenario.Id == "late-verdict") Check("current-verdict", completed.Task.Validations.Single().Agent == "current-validator");
        if (scenario.Id == "false-green") Check("false-approval-recorded", completed.Task.Validations.Single().Verdict == "yes" && _repo.Git("show", "main:result.txt") == "41");
    }

    public async Task Capture()
    {
        if (_founder is null) return;
        if (_id is not null)
        {
            var task = await Send(_founder, Routes.Task(_id), method: "GET");
            _report.Task = task.Deserialize<TaskDetailDto>(WorkflowReport.Json);
        }
        _report.Tasks = (await Send(_founder, Routes.Tasks, method: "GET")).Deserialize<TaskDto[]>(WorkflowReport.Json);
        _report.Events = (await Send(_founder, Routes.Events, method: "GET")).Deserialize<EventDto[]>(WorkflowReport.Json);
        _report.StaffingAttempts = _report.Events!.Count(e => e.Type == "conductor.staffing");
        _report.ScriptedLauncherInvocations = _hub.Validators.Started.Count + _hub.Orchestrators.Started.Count;
        var main = _repo.Git("rev-parse", "main");
        var present = _repo.Git("ls-tree", "--name-only", "main", "result.txt") == "result.txt";
        // Read bytes through git without TestRepo.Git's general-purpose Trim().
        string? result = null;
        if (present)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = _repo.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "show", "main:result.txt" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            result = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, error);
        }
        _report.Git = new(main, _branch is null ? null : _repo.Git("rev-parse", _branch), result, present);
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _hub.Dispose();
        _repo.Dispose();
    }
}
