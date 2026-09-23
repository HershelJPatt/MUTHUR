using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class IntegrationServiceTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly InspectionBarrier _barrier = new();
    private readonly HubFactory _hub;
    public IntegrationServiceTests() => _hub = new HubFactory { Processes = _barrier };
    private HttpClient _owner = null!;
    private HttpClient _validator = null!;
    private bool _initialized;
    private IntegrationService Service => _hub.Services.GetRequiredService<IntegrationService>();
    private Ledger Ledger => _hub.Services.GetRequiredService<Ledger>();

    public void Dispose() { _barrier.Continue.TrySetResult(); _hub.Dispose(); _repo.Dispose(); }

    private async Task<TaskDto> ApprovedAsync(bool approve = true, bool checks = true)
    {
        if (!_initialized)
        {
            await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "Review the implementation independently."))).EnsureSuccessStatusCode();
            _owner = await _hub.RegisterAgentAsync("owner", tier: "mastermind");
            _validator = await _hub.RegisterAgentAsync("validator", tier: "mastermind");
            (await _validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
            _initialized = true;
        }
        var task = await _owner.AddTaskAsync("Integration candidate");
        (await _owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var branch = "task/" + task.Id;
        _repo.Git("checkout", "-q", "-b", branch, "main");
        _repo.WriteSpec(task.Id);
        _repo.Write(task.Id + ".txt", "implementation");
        if (checks) _repo.Write("muthur.project.json", "{\"build\":\"build-command\",\"test\":\"test-command\"}");
        _repo.Commit("implementation and spec");
        (await _owner.PostActionAsync(task.Id, "spec", new SetSpecRequest($"specs/{task.Id}.md", branch))).EnsureSuccessStatusCode();
        task = await (await _owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).ReadTaskAsync();
        _repo.Git("checkout", "-q", "main");
        if (approve) task = await (await _validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", "Reviewed committed source and tests; reproduction recorded in fixture.", task.CurrentSubject!.Id))).ReadTaskAsync();
        return task;
    }

    private async Task<IntegrationAssignmentDto> ClaimAsync(TaskDto task, HttpClient? client = null) =>
        await ReadAsync<IntegrationAssignmentDto>(await (client ?? _validator).PostAsJsonAsync(Routes.TaskIntegrationAction(task.Id, "claim"), new IntegrationClaimRequest()));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<HttpResponseMessage> PostAsync<T>(TaskDto task, string action, T value, HttpClient? client = null) =>
        (client ?? _validator).PostAsJsonAsync(Routes.TaskIntegrationAction(task.Id, action), value);

    private IntegrationCandidateRequest Candidate(IntegrationCandidateDto c, bool reverse = false)
    {
        var tree = _repo.Git("rev-parse", c.ImplementationSha + "^{tree}");
        var sha = _repo.Git("commit-tree", tree, "-p", reverse ? c.ImplementationSha : c.TargetSha,
            "-p", reverse ? c.TargetSha : c.ImplementationSha, "-m", "candidate");
        return new(c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, sha, tree);
    }

    private async Task<IntegrationCandidateDto> RegisterAsync(TaskDto task, IntegrationCandidateDto c) =>
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "candidate", Candidate(c)));

    private IntegrationEvidenceDto Evidence(IntegrationCandidateDto c)
    {
        var start = _hub.Clock.GetUtcNow();
        _hub.Clock.Advance(TimeSpan.FromSeconds(1));
        return new(c.AssignmentId, c.Id, c.SubjectId, c.CandidateSha!,
            [new("build", "build-command", 0, 100, "build.log", new string('a', 64)),
             new("test", "test-command", 0, 100, "test.log", new string('b', 64))], start, _hub.Clock.GetUtcNow(), true, null);
    }

    private static async Task ErrorAsync(string code, HttpResponseMessage response) => Assert.Equal(code, (await response.ReadErrorAsync()).Code);

    [Fact]
    public async Task Exact_candidate_pass_is_immutable_and_does_not_promote_or_execute_commands()
    {
        var task = await ApprovedAsync();
        var original = _repo.Git("rev-parse", "main");
        var assigned = (await ClaimAsync(task)).Candidate;
        Assert.Null(assigned.StartedAt);
        Assert.NotEqual(assigned.Id, assigned.AssignmentId);
        Assert.Equal(task.CurrentSubject!.ImplementationSha, assigned.ImplementationSha);
        Assert.Equal(original, assigned.TargetSha);
        Assert.Equal(task.CurrentSubject.RequiredChecks, assigned.RequiredChecks);
        Assert.Equal(assigned.Id, (await ClaimAsync(task)).Candidate.Id);
        var registered = await RegisterAsync(task, assigned);
        Assert.Equal("checking", registered.State);
        Assert.Null(registered.StartedAt);
        var report = Evidence(registered);
        var passed = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", report));
        Assert.Equal("passed", passed.State);
        Assert.NotNull(passed.CompletedAt);
        Assert.Equal(64, passed.EvidenceSha256!.Length);
        var replay = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", report));
        Assert.Equal(passed.EvidenceSha256, replay.EvidenceSha256);
        Assert.Equal(passed.LeaseExpires, replay.LeaseExpires);
        await ErrorAsync("stale_integration_candidate", await PostAsync(task, "verdict", report with { CleanupMessage = "different" }));
        await ErrorAsync("integration_busy", await _hub.Founder().PostAsJsonAsync(Routes.TaskIntegrationAction(task.Id, "claim"), new IntegrationClaimRequest()));
        Assert.Equal(original, _repo.Git("rev-parse", "main"));
        var detail = await _owner.GetTaskAsync(task.Id);
        Assert.Equal("passed", detail.Task.CurrentIntegrationCandidate!.State);
        Assert.Single(detail.Events, e => e.Type == "integration.passed");
        // The commands deliberately do not exist. A hub that executes them cannot pass this fixture.
        Assert.Equal(TaskState.Validated, detail.Task.State);
    }

    [Theory]
    [InlineData("parents")]
    [InlineData("tree")]
    [InlineData("target")]
    [InlineData("implementation")]
    [InlineData("unknown")]
    [InlineData("included")]
    public async Task Candidate_structure_is_verified(string error)
    {
        var task = await ApprovedAsync();
        var c = (await ClaimAsync(task)).Candidate;
        var request = Candidate(c, error == "parents");
        request = error switch
        {
            "tree" => request with { TreeSha = new string('0', 40) },
            "target" => request with { TargetSha = c.ImplementationSha },
            "implementation" => request with { ImplementationSha = c.TargetSha },
            "unknown" => request with { CandidateSha = new string('0', 40) },
            "included" => request with { AlreadyIncluded = true },
            _ => request,
        };
        await ErrorAsync("integration_candidate_invalid", await PostAsync(task, "candidate", request));
        Assert.Null((await Service.ShowAsync(Caller.Founder, task.Id)).Current!.CandidateSha);
    }

    [Fact]
    public async Task Identical_registration_is_idempotent_and_different_commit_conflicts()
    {
        var task = await ApprovedAsync();
        var c = (await ClaimAsync(task)).Candidate;
        var request = Candidate(c);
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "candidate", request));
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "candidate", request));
        var other = _repo.Git("commit-tree", request.TreeSha, "-p", c.TargetSha, "-p", c.ImplementationSha, "-m", "different candidate");
        await ErrorAsync("integration_candidate_invalid", await PostAsync(task, "candidate", request with { CandidateSha = other }));
        Assert.Single((await _owner.GetTaskAsync(task.Id)).Events, e => e.Type == "integration.candidate_recorded");
    }

    [Fact]
    public async Task Already_included_requires_verified_ancestry_and_still_needs_reports()
    {
        var task = await ApprovedAsync();
        _repo.Git("update-ref", "refs/heads/main", task.CurrentSubject!.ImplementationSha);
        var c = (await ClaimAsync(task)).Candidate;
        var tree = _repo.Git("rev-parse", c.TargetSha + "^{tree}");
        var registered = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "candidate", new IntegrationCandidateRequest(c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, c.TargetSha, tree, true)));
        Assert.Equal("checking", registered.State);
        Assert.Equal("passed", (await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", Evidence(registered)))).State);
    }

    [Theory]
    [InlineData("checkout")]
    [InlineData("digest")]
    [InlineData("command")]
    [InlineData("missing")]
    [InlineData("order")]
    [InlineData("negative")]
    [InlineData("artifact")]
    [InlineData("timestamps")]
    [InlineData("candidate")]
    [InlineData("skipped")]
    public async Task Malformed_evidence_cannot_pass(string error)
    {
        var task = await ApprovedAsync();
        var c = await RegisterAsync(task, (await ClaimAsync(task)).Candidate);
        var r = Evidence(c);
        var checks = r.Checks.ToArray();
        if (error == "digest") checks[0] = checks[0] with { ArtifactSha256 = "wrong" };
        if (error == "command") checks[0] = checks[0] with { Command = "unreviewed" };
        if (error == "order") Array.Reverse(checks);
        if (error == "negative") checks[0] = checks[0] with { DurationMilliseconds = -1 };
        if (error == "artifact") checks[0] = checks[0] with { ArtifactReference = " " };
        if (error == "skipped") checks[1] = checks[1] with { Skipped = true, ExitCode = null, ArtifactSha256 = null };
        r = r with { Checks = error == "missing" ? [] : checks };
        if (error == "checkout") r = r with { CheckoutSha = c.TargetSha };
        if (error == "candidate") r = r with { CandidateId = Guid.NewGuid() };
        if (error == "timestamps") r = r with { EndedAt = r.StartedAt };
        await ErrorAsync("integration_evidence_incomplete", await PostAsync(task, "verdict", r));
        Assert.Equal("checking", (await Service.ShowAsync(Caller.Founder, task.Id)).Current!.State);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("cleanup")]
    public async Task Failed_checks_return_owner_preserve_verdict_and_release_queue(string phase)
    {
        var task = await ApprovedAsync();
        var next = await ApprovedAsync();
        var c = await RegisterAsync(task, (await ClaimAsync(task)).Candidate);
        var report = Evidence(c);
        var checks = report.Checks.ToArray();
        if (phase == "build")
        {
            checks[0] = checks[0] with { ExitCode = 1 };
            checks[1] = checks[1] with { Skipped = true, ExitCode = null, ArtifactSha256 = null };
        }
        if (phase == "test") checks[1] = checks[1] with { ExitCode = 2 };
        report = report with { Checks = checks, CleanupSucceeded = phase != "cleanup" };
        var result = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", report));
        Assert.Equal("failed", result.State);
        Assert.Equal(phase == "cleanup" ? "integration_cleanup_failed" : "integration_check_failed", result.FailureCode);
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", report));
        var detail = await _owner.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.InProgress, detail.Task.State);
        Assert.Equal("yes", Assert.Single(detail.Task.Validations).Verdict);
        Assert.Equal(next.Id, (await ClaimAsync(next)).Candidate.TaskId);
    }

    [Fact]
    public async Task Failure_is_idempotent_and_never_converts_to_pass()
    {
        var task = await ApprovedAsync();
        var c = (await ClaimAsync(task)).Candidate;
        var request = new IntegrationFailureRequest(c.AssignmentId, c.SubjectId, "construction", "merge_conflict", "Resolve colliding changes.", Files: ["file.cs"], LandedSince: ["T-2"]);
        var failed = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "failure", request));
        Assert.Equal("failed", failed.State);
        Assert.Null(failed.CandidateSha);
        Assert.NotNull(failed.CompletedAt);
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "failure", request));
        await ErrorAsync("stale_integration_candidate", await PostAsync(task, "failure", request with { Message = "different" }));
        Assert.Single((await _owner.GetTaskAsync(task.Id)).Events, e => e.Type == "integration.failed");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("branch")]
    [InlineData("subject")]
    [InlineData("policy")]
    public async Task Drift_is_durably_invalidated_before_refusal(string drift)
    {
        var task = await ApprovedAsync();
        var c = (await ClaimAsync(task)).Candidate;
        if (drift == "main") { _repo.Write("new-main.txt", "change"); _repo.Commit("target moved"); }
        if (drift == "branch") _repo.Git("update-ref", "refs/heads/task/" + task.Id, c.TargetSha);
        if (drift == "subject") (await _owner.PostActionAsync(task.Id, "revalidate", new RevalidateRequest("New validation round required."))).EnsureSuccessStatusCode();
        if (drift == "policy") (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "Changed review policy."))).EnsureSuccessStatusCode();
        await ErrorAsync(drift == "main" ? "integration_target_changed" : "stale_integration_candidate", await PostAsync(task, "renew", new IntegrationRenewRequest(c.AssignmentId, c.SubjectId)));
        var history = await Service.ShowAsync(Caller.Founder, task.Id);
        Assert.Equal("invalidated", history.Current!.State);
        Assert.NotNull(history.Current.CompletedAt);
        Assert.Single((await _owner.GetTaskAsync(task.Id)).Events, e => e.Type == "integration.invalidated");
    }

    [Fact]
    public async Task Expiry_cooldown_fifo_and_attempt_cap_survive_fresh_service_contexts()
    {
        var task = await ApprovedAsync();
        var next = await ApprovedAsync();
        var options = _hub.Services.GetRequiredService<MuthurOptions>();
        options.ConductorMaxAttempts = 2;
        await ErrorAsync("integration_not_next", await PostAsync(next, "claim", new IntegrationClaimRequest()));
        var first = (await ClaimAsync(task)).Candidate;
        _hub.Clock.Advance(TimeSpan.FromMinutes(options.ConductorOrchestratorMinutes + 2));
        var renewedService = new IntegrationService(Ledger, _hub.Services.GetRequiredService<ITaskLander>(), options, _hub.Services.GetRequiredService<LeasePolicy>());
        await renewedService.SweepAsync();
        Assert.Equal("interrupted", (await renewedService.ShowAsync(Caller.Founder, task.Id)).Current!.State);
        await ErrorAsync("integration_retry_cooldown", await PostAsync(task, "claim", new IntegrationClaimRequest(), _hub.Founder()));
        var other = (await ClaimAsync(next, _hub.Founder())).Candidate;
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(next, "failure", new IntegrationFailureRequest(other.AssignmentId, other.SubjectId, "launch", "runner_failed", "Launch unavailable."), _hub.Founder()));
        _hub.Clock.Advance(TimeSpan.FromMinutes(options.ConductorStallProbeMinutes + 1));
        var second = (await ClaimAsync(task, _hub.Founder())).Candidate;
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.Id, second.Id);
        await ErrorAsync("stale_integration_candidate", await PostAsync(task, "failure", new IntegrationFailureRequest(first.AssignmentId, first.SubjectId, "launch", "runner_failed", "Late failure.")));
        _hub.Clock.Advance(TimeSpan.FromMinutes(options.ConductorOrchestratorMinutes + 2));
        await renewedService.SweepAsync();
        Assert.Equal(TaskState.InProgress, (await _owner.GetTaskAsync(task.Id)).Task.State);
        Assert.Equal(2, (await renewedService.ShowAsync(Caller.Founder, task.Id)).History.Count);
    }

    [Fact]
    public async Task Independent_approval_required_and_owner_or_unqualified_caller_cannot_claim()
    {
        var task = await ApprovedAsync(approve: false);
        await ErrorAsync("stale_integration_candidate", await PostAsync(task, "claim", new IntegrationClaimRequest()));
        await ErrorAsync("integration_runner_forbidden", await PostAsync(task, "claim", new IntegrationClaimRequest(), _owner));
        var stranger = await _hub.RegisterAgentAsync("integration-runner-spoof", tier: "mastermind");
        await ErrorAsync("integration_runner_forbidden", await PostAsync(task, "claim", new IntegrationClaimRequest(), stranger));
    }

    [Fact]
    public async Task Missing_checks_are_not_a_passing_noop()
    {
        var task = await ApprovedAsync(checks: false);
        await ErrorAsync("integration_checks_missing", await PostAsync(task, "claim", new IntegrationClaimRequest()));
    }

    [Fact]
    public async Task Registered_runner_is_restricted_to_persisted_assignment_and_cannot_be_taken_over()
    {
        var task = await ApprovedAsync();
        var next = await ApprovedAsync();
        var agents = _hub.Services.GetRequiredService<AgentService>();
        var registration = await agents.RegisterIntegrationRunnerAsync(new("deterministic-runner", "local", "none"));
        var caller = (await agents.AuthenticateAsync(registration.Token))!;
        Assert.True(caller.IntegrationRunner);
        var runner = _hub.CreateClient(registration.Token);
        var assignment = await Service.AssignRunnerAsync(caller, task.Id);
        Assert.Equal(registration.Agent.Id, assignment.Candidate.AssignedAgentId);
        Assert.Equal(HttpStatusCode.OK, (await runner.GetAsync(Routes.TaskIntegration(task.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await runner.GetAsync(Routes.TaskIntegration(next.Id))).StatusCode);
        foreach (var path in new[] { Routes.Task(task.Id), Routes.Agents, Routes.Tasks, Routes.Events })
            Assert.Equal(HttpStatusCode.Unauthorized, (await runner.GetAsync(path)).StatusCode);
        foreach (var path in new[] { Routes.TaskAction(task.Id, "claim"), Routes.TaskAction(task.Id, "implemented"), Routes.TaskAction(task.Id, "land"),
            Routes.TaskAction(task.Id, "pass"), Routes.RoleAction("win-validator", "take"), Routes.AgentRegister,
            Routes.TaskIntegrationAction(task.Id, "claim"), Routes.TaskIntegrationAction(next.Id, "renew") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await runner.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await runner.PostAsJsonAsync(Routes.Agents + "/heartbeat", new HeartbeatRequest())).StatusCode);
        await ErrorAsync("integration_runner_forbidden", await _hub.Founder().PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest("deterministic-runner", "local", "none")));
        await ErrorAsync("integration_runner_forbidden", await PostAsync(task, "renew", new IntegrationRenewRequest(assignment.Candidate.AssignmentId, assignment.Candidate.SubjectId)));
        var exception = await Assert.ThrowsAsync<MuthurException>(() => Service.ClaimAsync(caller, task.Id));
        Assert.Equal("integration_runner_forbidden", exception.Code);
        var fake = new Caller(CallerKind.Agent, Guid.NewGuid(), caller.Name, IntegrationRunner: true);
        exception = await Assert.ThrowsAsync<MuthurException>(() => Service.AssignRunnerAsync(fake, task.Id));
        Assert.Equal("integration_runner_forbidden", exception.Code);
    }

    [Fact]
    public async Task Start_metadata_is_exact_idempotent_and_renew_does_not_change_start_or_attempt()
    {
        var task = await ApprovedAsync();
        var c = (await ClaimAsync(task)).Candidate;
        var request = new IntegrationStartRequest(c.AssignmentId, c.SubjectId, 123, _hub.Clock.GetUtcNow().AddTicks(-1),
            Path.Combine(_repo.Path, ".worktrees", $"integration-{c.Id:N}"), Path.Combine(_repo.Path, ".work", "integration", $"{c.Id:N}"));
        foreach (var wrong in new[] { request with { ProcessId = 0 }, request with { ProcessStartedAt = _hub.Clock.GetUtcNow().AddSeconds(1) },
            request with { OwnedWorktreePath = _repo.Path }, request with { ArtifactsDirectory = Path.GetTempPath() } })
            await ErrorAsync("integration_candidate_invalid", await PostAsync(task, "started", wrong));
        var started = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "started", request));
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "started", request));
        Assert.Equal(123, started.RunnerProcessId);
        Assert.NotNull(started.StartedAt);
        await ErrorAsync("integration_candidate_invalid", await PostAsync(task, "started", request with { ProcessId = 124 }));
        _hub.Clock.Advance(TimeSpan.FromSeconds(5));
        var renewed = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "renew", new IntegrationRenewRequest(c.AssignmentId, c.SubjectId)));
        Assert.Equal(started.StartedAt, renewed.StartedAt);
        Assert.Equal(started.Attempt, renewed.Attempt);
        Assert.True(renewed.LeaseExpires > started.LeaseExpires);
        Assert.Single((await _owner.GetTaskAsync(task.Id)).Events, e => e.Type == "integration.started");
    }

    [Fact]
    public async Task Commit_inspection_handles_root_and_normal_commits_and_fails_closed()
    {
        var project = new Project { Id = Guid.NewGuid(), Key = "fixture", Name = "Fixture", RepoPath = _repo.Path };
        var git = new GitLander(new Muthur.Launch.ProcessRunner(), new FakePullRequestOpener());
        var root = _repo.Git("rev-parse", "main");
        var first = await git.InspectCommitAsync(project, root);
        Assert.NotNull(first);
        Assert.Empty(first.Parents);
        _repo.Write("next.txt", "next");
        _repo.Commit("normal commit");
        var normal = await git.InspectCommitAsync(project, _repo.Git("rev-parse", "main"));
        Assert.NotNull(normal);
        Assert.Equal(root, Assert.Single(normal.Parents));
        Assert.True(await git.IsAncestorAsync(project, root, normal.Sha));
        Assert.False(await git.IsAncestorAsync(project, normal.Sha, root));
        Assert.Null(await git.InspectCommitAsync(project, "--bad-option"));
        Assert.Null(await git.InspectCommitAsync(project, new string('0', 40)));
    }
    [Fact]
    public async Task Target_movement_while_git_inspection_waits_is_invalidated_before_registration_returns()
    {
        var task = await ApprovedAsync();
        var c = (await ClaimAsync(task)).Candidate;
        var request = Candidate(c);
        _barrier.Armed = true;
        var pending = PostAsync(task, "candidate", request);
        await _barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _repo.Write("movement.txt", "target advanced during inspection");
        _repo.Commit("target movement");
        _barrier.Continue.SetResult();
        await ErrorAsync("integration_target_changed", await pending);
        Assert.Equal("invalidated", (await Service.ShowAsync(Caller.Founder, task.Id)).Current!.State);
    }

    [Fact]
    public async Task Passed_report_replay_after_target_movement_is_refused_without_reusing_evidence()
    {
        var task = await ApprovedAsync();
        var c = await RegisterAsync(task, (await ClaimAsync(task)).Candidate);
        var report = Evidence(c);
        var passed = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", report));
        _repo.Write("movement.txt", "new target");
        _repo.Commit("advance target");
        await ErrorAsync("integration_target_changed", await PostAsync(task, "verdict", report));
        var history = await Service.ShowAsync(Caller.Founder, task.Id);
        Assert.Equal("invalidated", history.Current!.State);
        Assert.Equal(passed.EvidenceSha256, history.Current.EvidenceSha256);
    }

    [Fact]
    public async Task Expired_reports_cannot_close_assignment_and_cap_refusal_commits_owner_recovery()
    {
        var task = await ApprovedAsync();
        var options = _hub.Services.GetRequiredService<MuthurOptions>();
        options.ConductorMaxAttempts = 1;
        var c = await RegisterAsync(task, (await ClaimAsync(task)).Candidate);
        var report = Evidence(c);
        _hub.Clock.Advance(TimeSpan.FromMinutes(options.ConductorOrchestratorMinutes + 2));
        await ErrorAsync("stale_integration_candidate", await PostAsync(task, "verdict", report));
        Assert.Equal("interrupted", (await Service.ShowAsync(Caller.Founder, task.Id)).Current!.State);
        await ErrorAsync("integration_retry_exhausted", await PostAsync(task, "claim", new IntegrationClaimRequest(), _hub.Founder()));
        Assert.Equal(TaskState.InProgress, (await _owner.GetTaskAsync(task.Id)).Task.State);
    }

    [Fact]
    public async Task Historical_attempt_cap_cannot_recover_a_newly_approved_subject()
    {
        var task = await ApprovedAsync();
        _hub.Services.GetRequiredService<MuthurOptions>().ConductorMaxAttempts = 1;
        var old = (await ClaimAsync(task)).Candidate;
        (await _owner.PostActionAsync(task.Id, "revalidate", new RevalidateRequest("A fresh independent round is required."))).EnsureSuccessStatusCode();
        task = await (await _owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/" + task.Id))).ReadTaskAsync();
        task = await (await _validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", "Independently checked the replacement round and recorded fixture evidence.", task.CurrentSubject!.Id))).ReadTaskAsync();
        var next = (await ClaimAsync(task)).Candidate;
        Assert.NotEqual(old.SubjectId, next.SubjectId);
        Assert.Equal(1, next.Attempt);
        Assert.Equal(TaskState.Validated, (await _owner.GetTaskAsync(task.Id)).Task.State);
        Assert.Equal("invalidated", (await Service.ShowAsync(Caller.Founder, task.Id)).History.Single(c => c.Id == old.Id).State);
        await ErrorAsync("stale_integration_candidate", await PostAsync(task, "renew", new IntegrationRenewRequest(old.AssignmentId, old.SubjectId)));
        var after = (await _owner.GetTaskAsync(task.Id)).Task;
        Assert.Equal(TaskState.Validated, after.State);
        Assert.Equal(next.Id, after.CurrentIntegrationCandidate!.Id);
        Assert.Equal(next.LeaseExpires, after.CurrentIntegrationCandidate.LeaseExpires);
    }

    [Fact]
    public async Task Landing_requires_tested_candidate_refuses_checked_out_target_and_promotes_exactly_once()
    {
        var task = await ApprovedAsync();
        await ErrorAsync("integration_required", await _owner.PostActionAsync(task.Id, "land", new { }));
        var candidate = await RegisterAsync(task, (await ClaimAsync(task)).Candidate);
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", Evidence(candidate)));
        await ErrorAsync("integration_target_checked_out", await _owner.PostActionAsync(task.Id, "land", new { }));
        Assert.Equal(candidate.TargetSha, _repo.Git("rev-parse", "main"));
        _repo.Git("checkout", "--detach", "-q", "main");
        (await _owner.PostActionAsync(task.Id, "land", new { })).EnsureSuccessStatusCode();
        Assert.Equal(candidate.CandidateSha, _repo.Git("rev-parse", "main"));
        (await _owner.PostActionAsync(task.Id, "land", new { })).EnsureSuccessStatusCode();
        Assert.Single((await _owner.GetTaskAsync(task.Id)).Events, e => e.Type == "integration.promoted");
        Assert.Single((await _owner.GetTaskAsync(task.Id)).Events, e => e.Type == "task.landed");
    }

    [Fact]
    public async Task Durable_intent_recovers_after_git_update_before_ledger_finalization()
    {
        var task = await ApprovedAsync();
        var candidate = await RegisterAsync(task, (await ClaimAsync(task)).Candidate);
        await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", Evidence(candidate)));
        await ErrorAsync("integration_target_checked_out", await _owner.PostActionAsync(task.Id, "land", new { }));
        Assert.NotNull((await Service.ShowAsync(Caller.Founder, task.Id)).Current!.PromotionIntentAt);
        _repo.Git("checkout", "--detach", "-q", "main");
        _repo.Git("update-ref", "refs/heads/main", candidate.CandidateSha!, candidate.TargetSha);
        (await _owner.PostActionAsync(task.Id, "land", new { })).EnsureSuccessStatusCode();
        Assert.Equal(TaskState.Done, (await _owner.GetTaskAsync(task.Id)).Task.State);
        Assert.Equal(candidate.CandidateSha, _repo.Git("rev-parse", "main"));
    }

    [Fact]
    public async Task Terminal_registration_replay_preserves_evidence_and_lease()
    {
        var task = await ApprovedAsync();
        var assigned = (await ClaimAsync(task)).Candidate;
        var request = Candidate(assigned);
        var candidate = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "candidate", request));
        var passed = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "verdict", Evidence(candidate)));
        _hub.Clock.Advance(TimeSpan.FromSeconds(2));
        var replay = await ReadAsync<IntegrationCandidateDto>(await PostAsync(task, "candidate", request));
        Assert.Equal(passed.LeaseExpires, replay.LeaseExpires);
        Assert.Equal(passed.EvidenceSha256, replay.EvidenceSha256);
        Assert.Equal("passed", replay.State);
        await ErrorAsync("integration_candidate_invalid", await PostAsync(task, "candidate", request with { TreeSha = new string('a', 40) }));
    }

    private sealed class InspectionBarrier : Muthur.Launch.IProcessRunner
    {
        private readonly Muthur.Launch.ProcessRunner _inner = new();
        public bool Armed { get; set; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Muthur.Launch.ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            if (Armed && fileName == "git" && arguments.Contains("--no-patch"))
            {
                Armed = false;
                Reached.TrySetResult();
                await Continue.Task.WaitAsync(ct);
            }
            return await _inner.RunAsync(fileName, arguments, workingDirectory, stdin, timeout, ct, scrubEnvironment, environment);
        }
    }}
