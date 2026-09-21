using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Data;
using Muthur.Core.Entities;
using Muthur.Launch;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class ValidationSubjectTests : IDisposable
{
    private const string Branch = "task/T-1-subject";
    private const string Report = "Ran dotnet test at the claimed SHA; all checks passed. Reproduce: dotnet test.";
    private readonly TestRepo _repo = new();
    private readonly SubjectBarrier _barrier = new();
    private readonly HubFactory _hub;

    public ValidationSubjectTests() => _hub = new HubFactory { Processes = _barrier };

    public void Dispose()
    {
        _barrier.Continue.TrySetResult();
        _hub.Dispose();
        _repo.Dispose();
    }

    private async Task<(HttpClient Owner, HttpClient Validator, TaskDto Task)> RoundAsync(bool required = true)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: required ? ["win-validator"] : []);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "Run the committed build and test commands."))).EnsureSuccessStatusCode();
        var owner = await _hub.RegisterAgentAsync("owner");
        var validator = await _hub.RegisterAgentAsync("validator");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        var task = await owner.AddTaskAsync("Freeze the reviewed implementation");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        _repo.Git("checkout", "-q", "-b", Branch);
        _repo.WriteSpec(task.Id);
        _repo.Write("implementation.txt", "A\n");
        _repo.Write("muthur.project.json", "{\"build\":\"dotnet build\",\"test\":\"dotnet test\"}\n");
        _repo.Commit("implementation A and frozen spec");
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest($"specs/{task.Id}.md", Branch))).EnsureSuccessStatusCode();
        task = await (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch))).ReadTaskAsync();
        _repo.Git("checkout", "-q", "main");
        Assert.Equal("current", task.ProvenanceStatus);
        Assert.NotNull(task.CurrentSubject);
        Assert.Equal(_repo.Git("rev-parse", Branch), task.CurrentSubject.ImplementationSha);
        return (owner, validator, task);
    }

    private static async Task<TaskDto> PassAsync(HttpClient validator, TaskDto task) =>
        await (await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", Report, task.CurrentSubject!.Id))).ReadTaskAsync();

    private string CommitB()
    {
        _repo.Git("checkout", "-q", "-b", "candidate-B", Branch);
        _repo.Write("implementation.txt", "B\n");
        _repo.Commit("unreviewed B");
        var sha = _repo.Git("rev-parse", "HEAD");
        _repo.Git("checkout", "-q", "main");
        return sha;
    }

    [Fact]
    public async Task Approved_A_cannot_land_when_the_task_branch_names_B()
    {
        var (owner, validator, task) = await RoundAsync();
        await PassAsync(validator, task);
        var before = _repo.Git("rev-parse", "main");
        _repo.Git("update-ref", $"refs/heads/{Branch}", CommitB());
        var refused = await owner.PostAsync(Routes.TaskAction(task.Id, "land"), null);
        Assert.Equal("implementation_changed", (await refused.ReadErrorAsync()).Code);
        Assert.Equal(before, _repo.Git("rev-parse", "main"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Movement_after_the_head_check_integrates_only_A(bool checkout)
    {
        var (owner, validator, task) = await RoundAsync();
        await PassAsync(validator, task);
        var b = CommitB();
        if (!checkout) _repo.Git("checkout", "-q", "--detach", "main");
        _barrier.Armed = true;
        var landing = owner.PostAsync(Routes.TaskAction(task.Id, "land"), null);
        await _barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _repo.Git("update-ref", $"refs/heads/{Branch}", b);
        _barrier.Continue.SetResult();
        var landed = await (await landing).ReadTaskAsync();
        Assert.Equal(TaskState.Done, landed.State);
        Assert.Equal("A", _repo.Git("show", "main:implementation.txt"));
        Assert.Equal(task.CurrentSubject!.ImplementationSha, _repo.Git("rev-parse", "main^2"));
        Assert.Equal(b, _repo.Git("rev-parse", Branch));
    }

    [Fact]
    public async Task A_retained_old_round_cannot_vote_after_the_same_validator_reclaims()
    {
        var (owner, validator, task) = await RoundAsync();
        var claim = await (await validator.PostActionAsync(task.Id, "validate-claim", new ClaimValidationRequest("win-validator"))).ReadTaskAsync();
        var oldId = claim.CurrentSubject!.Id;
        (await owner.PostActionAsync(task.Id, "revalidate", new RevalidateRequest("Repeat independent review."))).EnsureSuccessStatusCode();
        var fresh = await (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch))).ReadTaskAsync();
        Assert.NotEqual(oldId, fresh.CurrentSubject!.Id);
        (await validator.PostActionAsync(task.Id, "validate-claim", new ClaimValidationRequest("win-validator", fresh.CurrentSubject.Id))).EnsureSuccessStatusCode();
        var stale = await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", Report, oldId));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_validation_subject", (await stale.ReadErrorAsync()).Code);
        var detail = await owner.GetTaskAsync(task.Id);
        Assert.Equal("pending", Assert.Single(detail.Task.Validations).Verdict);
        var refusal = Assert.Single(detail.Events, e => e.Type == "validation.verdict_refused");
        Assert.Equal(oldId, refusal.Payload.GetProperty("subjectId").GetGuid());
        Assert.False(refusal.Payload.TryGetProperty("evidence", out _));
        await PassAsync(validator, fresh);
    }

    [Fact]
    public async Task Spec_only_changes_require_explicit_reattachment_and_a_new_round()
    {
        var (owner, validator, task) = await RoundAsync();
        await PassAsync(validator, task);
        var cannotAttach = await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/T-1.md", Branch));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cannotAttach.StatusCode);
        (await owner.PostActionAsync(task.Id, "revalidate", new RevalidateRequest("Amend the frozen spec."))).EnsureSuccessStatusCode();
        _repo.Git("checkout", "-q", Branch);
        _repo.Write("specs/T-1.md", "# T-1 — amended spec\nOnly the spec changes.\n");
        _repo.Commit("amend spec only");
        var mismatch = await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch));
        Assert.Equal("spec_changed", (await mismatch.ReadErrorAsync()).Code);
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/T-1.md", Branch))).EnsureSuccessStatusCode();
        var fresh = await (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch))).ReadTaskAsync();
        Assert.NotEqual(task.CurrentSubject!.SpecSha256, fresh.CurrentSubject!.SpecSha256);
        Assert.Equal("missing", Assert.Single(fresh.Validations).EvidenceStatus);
        Assert.Equal(TaskState.Validating, fresh.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OK")]
    [InlineData("pass")]
    [InlineData("yes")]
    [InlineData("done")]
    [InlineData("a short report")]
    public async Task Empty_or_acknowledgment_passes_are_refused_and_counted(string? evidence)
    {
        var (owner, validator, task) = await RoundAsync();
        var response = await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", evidence, task.CurrentSubject!.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("evidence_required", (await response.ReadErrorAsync()).Code);
        var detail = await owner.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.Validating, detail.Task.State);
        Assert.Contains(detail.Events, e => e.Type == "validation.verdict_refused" && e.Payload.GetProperty("code").GetString() == "evidence_required");
        Assert.DoesNotContain(detail.Events, e => e.Type == "validation.passed");
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("blocked")]
    public async Task Every_outcome_requires_an_explicit_subject(string action)
    {
        var (_, validator, task) = await RoundAsync();
        var response = await validator.PostActionAsync(task.Id, action, new VerdictRequest("win-validator", Report));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation_subject_required", (await response.ReadErrorAsync()).Code);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("verdict")]
    [InlineData("land")]
    public async Task Live_brief_drift_invalidates_claim_verdict_and_land(string stage)
    {
        var (owner, validator, task) = await RoundAsync();
        if (stage == "land") await PassAsync(validator, task);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "New checks are required."))).EnsureSuccessStatusCode();
        var response = stage switch
        {
            "claim" => await validator.PostActionAsync(task.Id, "validate-claim", new ClaimValidationRequest("win-validator", task.CurrentSubject!.Id)),
            "verdict" => await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", Report, task.CurrentSubject!.Id)),
            _ => await owner.PostAsync(Routes.TaskAction(task.Id, "land"), null),
        };
        Assert.Equal("validation_subject_changed", (await response.ReadErrorAsync()).Code);
        Assert.Equal("stale", (await owner.GetTaskAsync(task.Id)).Task.ProvenanceStatus);
    }

    [Fact]
    public async Task Unrelated_default_branch_config_does_not_change_the_reviewed_commands()
    {
        var (owner, validator, task) = await RoundAsync();
        _repo.Write("muthur.project.json", "{\"build\":\"unrelated build\"}\n");
        _repo.Commit("unrelated default policy");
        var claim = await (await validator.PostActionAsync(task.Id, "validate-claim", new ClaimValidationRequest("win-validator"))).ReadTaskAsync();
        Assert.Equal("dotnet build", claim.CurrentSubject!.RequiredChecks.Build);
        Assert.Equal(TaskState.Validated, (await PassAsync(validator, task)).State);
        Assert.Equal("current", (await owner.GetTaskAsync(task.Id)).Task.ProvenanceStatus);
    }

    [Fact]
    public async Task Ungated_projects_still_create_and_land_an_exact_subject()
    {
        var (owner, _, task) = await RoundAsync(required: false);
        Assert.Equal(TaskState.Validated, task.State);
        Assert.Empty(task.CurrentSubject!.RequiredValidators);
        var landed = await (await owner.PostAsync(Routes.TaskAction(task.Id, "land"), null)).ReadTaskAsync();
        Assert.Equal(task.CurrentSubject.Id, landed.CurrentSubject!.Id);
        Assert.Equal(task.CurrentSubject.ImplementationSha, _repo.Git("rev-parse", "main^2"));
    }

    [Fact]
    public async Task Legacy_unknown_provenance_requires_recovery_without_rewriting_history()
    {
        var (owner, validator, task) = await RoundAsync();
        await PassAsync(validator, task);
        await _hub.Services.GetRequiredService<Ledger>().MutateAsync(Caller.System, async m =>
        {
            var legacy = await m.Db.Tasks.SingleAsync(t => t.Id == 1);
            legacy.CurrentSubject = null;
            legacy.CurrentSubjectId = null;
            legacy.SpecSha256 = null;
            var row = await m.Db.TaskValidations.SingleAsync();
            row.SubjectId = null;
            m.Record("test.legacy_fixture", legacy.Id);
        });
        var detail = await owner.GetTaskAsync(task.Id);
        Assert.Equal("unknown", detail.Task.ProvenanceStatus);
        Assert.Equal("unknown", Assert.Single(detail.Task.Validations).EvidenceStatus);
        var refused = await owner.PostAsync(Routes.TaskAction(task.Id, "land"), null);
        Assert.Equal("validation_provenance_unknown", (await refused.ReadErrorAsync()).Code);
        var recovered = await (await owner.PostActionAsync(task.Id, "revalidate", new RevalidateRequest("Recover unknown provenance."))).ReadTaskAsync();
        Assert.Null(recovered.CurrentSubject);
        Assert.Equal(Report, Assert.Single(recovered.Validations).Evidence);
        var unknownDigest = await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch));
        Assert.Equal("spec_changed", (await unknownDigest.ReadErrorAsync()).Code);
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/T-1.md", Branch))).EnsureSuccessStatusCode();
        var fresh = await (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch))).ReadTaskAsync();
        Assert.NotEqual(task.CurrentSubject!.Id, fresh.CurrentSubject!.Id);
        Assert.Contains((await owner.GetTaskAsync(task.Id)).Events, e => e.Type == "validation.passed");
    }

    [Fact]
    public async Task Subject_and_report_survive_a_new_hub_and_source_generated_JSON()
    {
        var (_, validator, task) = await RoundAsync();
        await PassAsync(validator, task);
        using var restarted = new HubFactory { DataDir = _hub.DataDir };
        var detail = await restarted.Founder().GetTaskAsync(task.Id);
        Assert.Equal(task.CurrentSubject!.Id, detail.Task.CurrentSubject!.Id);
        Assert.Equal(Report, Assert.Single(detail.Task.Validations).Evidence);
        Assert.Equal("reported", Assert.Single(detail.Task.Validations).EvidenceStatus);
        var json = JsonSerializer.Serialize(detail, MuthurJsonContext.Default.TaskDetailDto);
        var decoded = JsonSerializer.Deserialize(json, MuthurJsonContext.Default.TaskDetailDto)!;
        Assert.Equal(task.CurrentSubject.ImplementationSha, decoded.Task.CurrentSubject!.ImplementationSha);
        var html = await restarted.CreateClient().GetStringAsync($"/tasks/{task.Id}");
        Assert.Contains(task.CurrentSubject.ImplementationSha, html);
        Assert.Contains("Spec SHA-256", html);
        Assert.Contains("reported", html);
    }

    [Fact]
    public async Task Owner_and_policy_mutations_wait_until_integration_is_recorded()
    {
        var (owner, validator, task) = await RoundAsync();
        await PassAsync(validator, task);
        var ownerId = await _hub.Services.GetRequiredService<Ledger>().ReadAsync(
            async (db, _) => (await db.Agents.SingleAsync(a => a.Name == "owner")).Id);
        var caller = new Caller(CallerKind.Agent, ownerId, "owner");
        var lifecycle = _hub.Services.GetRequiredService<LifecycleService>();
        _barrier.Armed = true;
        var landing = owner.PostAsync(Routes.TaskAction(task.Id, "land"), null);
        await _barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // Direct calls reach the writer's WaitAsync before returning. HTTP scheduling alone cannot
        // establish that the competing mutations have actually attempted to enter the critical section.
        var recovery = lifecycle.RevalidateAsync(caller, task.Id, new RevalidateRequest("Race the land."));
        var resubmission = lifecycle.ImplementedAsync(caller, task.Id, new ImplementedRequest(Branch));
        var attachment = _hub.Services.GetRequiredService<TaskService>().SetSpecAsync(
            caller, task.Id, new SetSpecRequest("specs/T-1.md", Branch));
        var policy = _hub.Services.GetRequiredService<RoleService>().DefineAsync(
            Caller.Founder, new DefineRoleRequest("win-validator", "Policy changed after approval."));
        Assert.False(recovery.IsCompleted);
        Assert.False(resubmission.IsCompleted);
        Assert.False(attachment.IsCompleted);
        Assert.False(policy.IsCompleted);
        _barrier.Continue.SetResult();
        Assert.Equal(TaskState.Done, (await (await landing).ReadTaskAsync()).State);
        Assert.Equal("cannot_revalidate", (await Assert.ThrowsAsync<MuthurException>(() => recovery)).Code);
        Assert.Equal("not_in_progress", (await Assert.ThrowsAsync<MuthurException>(() => resubmission)).Code);
        Assert.Equal("not_in_progress", (await Assert.ThrowsAsync<MuthurException>(() => attachment)).Code);
        await policy;
        var detail = await owner.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.Done, detail.Task.State);
        Assert.Contains(detail.Events, e => e.Type == "task.landed" && e.Payload.GetProperty("subject").GetProperty("id").GetGuid() == task.CurrentSubject!.Id);
    }

    [Fact]
    public async Task Migration_preserves_legacy_states_reports_and_events_without_inventing_subjects()
    {
        await using var db = new MuthurDb(new DbContextOptionsBuilder<MuthurDb>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260920145723_TaskDependencies");
        var project = new Project { Id = Guid.NewGuid(), Key = "legacy", Name = "Legacy", RepoPath = _repo.Path };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        foreach (var state in new[] { "Validated", "Done", "Cancelled" })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tasks(project_id,title,body,state,priority,created_at,updated_at) VALUES({project.Id},{state},'',{state},0,0,0)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO task_validations(task_id,validator_key,verdict,evidence,waiting_since) VALUES(1,'win-validator','Yes','historical report',0)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO events(at,actor,type,task_id,payload_json) VALUES(0,'legacy','validation.passed',1,{"{}"})");
        await migrator.MigrateAsync();
        var tasks = await db.Tasks.OrderBy(t => t.Id).ToListAsync();
        Assert.Equal([TaskState.Validated, TaskState.Done, TaskState.Cancelled], tasks.Select(t => t.State));
        Assert.All(tasks, task => { Assert.Null(task.CurrentSubjectId); Assert.Null(task.SpecSha256); });
        var verdict = await db.TaskValidations.SingleAsync();
        Assert.Null(verdict.SubjectId);
        Assert.Equal("historical report", verdict.Evidence);
        Assert.Equal("{}", (await db.Events.SingleAsync()).PayloadJson);
        Assert.Empty(await db.ValidationSubjects.ToListAsync());
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE tasks SET current_subject_id = 'missing' WHERE id = 1"));
    }

    [Fact]
    public async Task Unknown_rounds_are_visible_with_recovery_help_but_never_staffed()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        var (owner, _, task) = await RoundAsync();
        await _hub.Services.GetRequiredService<Ledger>().MutateAsync(Caller.System, async m =>
        {
            var legacy = await m.Db.Tasks.SingleAsync(t => t.Id == 1);
            legacy.CurrentSubject = null;
            legacy.CurrentSubjectId = null;
            (await m.Db.TaskValidations.SingleAsync()).SubjectId = null;
            m.Record("test.legacy_fixture", legacy.Id);
        });
        var pending = await owner.GetFromJsonAsync(Routes.Validations, MuthurJsonContext.Default.IReadOnlyListTaskDto);
        Assert.Empty(pending!);
        var queue = await owner.GetFromJsonAsync(Routes.ValidationQueue, MuthurJsonContext.Default.IReadOnlyListValidationQueueDto);
        Assert.Equal(0, Assert.Single(queue!).Waiting);
        Assert.Empty(await _hub.Services.GetRequiredService<ConductorService>().PlanAsync());
        var html = await _hub.CreateClient().GetStringAsync($"/tasks/{task.Id}");
        Assert.Contains("unknown", html);
        Assert.Contains("revalidate", html);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("mode")]
    [InlineData("roles")]
    [InlineData("path")]
    public async Task Live_project_policy_changes_require_revalidation(string kind)
    {
        var (_, validator, task) = await RoundAsync();
        var change = kind switch
        {
            "branch" => new UpdateProjectRequest(DefaultBranch: "new-default"),
            "mode" => new UpdateProjectRequest(LandMode: LandMode.Pr),
            "roles" => new UpdateProjectRequest(RequiredValidators: []),
            _ => new UpdateProjectRequest(RepoPath: Path.Combine(_repo.Path, "changed-repo")),
        };
        (await _hub.Founder().PutAsJsonAsync(Routes.Project("demo"), change)).EnsureSuccessStatusCode();
        var refused = await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", Report, task.CurrentSubject!.Id));
        Assert.Equal("validation_subject_changed", (await refused.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Malformed_committed_commands_are_refused_before_a_round_is_created()
    {
        var (owner, _, task) = await RoundAsync(required: false);
        (await owner.PostActionAsync(task.Id, "revalidate", new RevalidateRequest("Correct the command policy."))).EnsureSuccessStatusCode();
        _repo.Git("checkout", "-q", Branch);
        _repo.Write("muthur.project.json", "{\"build\":42}");
        _repo.Commit("malformed command policy");
        var response = await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch));
        Assert.Equal("validation_config_invalid", (await response.ReadErrorAsync()).Code);
        Assert.Null((await owner.GetTaskAsync(task.Id)).Task.CurrentSubject);
    }

    private sealed class SubjectBarrier : IProcessRunner
    {
        private readonly ProcessRunner _inner = new();
        public bool Armed { get; set; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            if (Armed && fileName == "git" && arguments.FirstOrDefault() == "merge-base")
            {
                Armed = false;
                Reached.TrySetResult();
                await Continue.Task.WaitAsync(ct);
            }
            return await _inner.RunAsync(fileName, arguments, workingDirectory, stdin, timeout, ct, scrubEnvironment, environment);
        }
    }
}
