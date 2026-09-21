using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class LifecycleTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private async Task DefineRolesAsync(params string[] keys)
    {
        foreach (var key in keys)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(key, $"Brief for {key}"))).EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> TakeAsync(HttpClient agent, string role) =>
        await agent.PostAsync(Routes.RoleAction(role, "take"), null);

    /// <summary>An owner with a claimed task whose spec is attached and whose branch exists with one new file.</summary>
    private async Task<(HttpClient Owner, string TaskId)> ImplementableTaskAsync(string branch = "task/T-1-feature", string file = "feature.txt", string content = "feature\n")
    {
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec()))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(branch, file, content);
        return (owner, task.Id);
    }

    [Fact]
    public async Task A_task_lands_only_after_every_required_validator_says_yes()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator", "web-validator"]);
        await DefineRolesAsync("win-validator", "web-validator");
        var (owner, id) = await ImplementableTaskAsync();
        var win = await _hub.RegisterAgentAsync("win");
        var web = await _hub.RegisterAgentAsync("web");

        var implemented = await (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).ReadTaskAsync();
        Assert.Equal(TaskState.Validating, implemented.State);
        Assert.Equal(["web-validator", "win-validator"], implemented.Validations.Select(v => v.Validator));
        Assert.All(implemented.Validations, v => Assert.Equal("pending", v.Verdict));

        var early = await owner.PostAsync(Routes.TaskAction(id, "land"), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal("not_validated", (await early.ReadErrorAsync()).Code);

        var noRole = await win.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "Ran the application: expected output observed; reproduce with dotnet test.", SubjectId: (await win.GetTaskAsync(id)).Task.CurrentSubject!.Id));
        Assert.Equal("role_not_held", (await noRole.ReadErrorAsync()).Code);

        (await TakeAsync(win, "win-validator")).EnsureSuccessStatusCode();
        (await TakeAsync(web, "web-validator")).EnsureSuccessStatusCode();
        var afterOne = await (await win.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "ran the app, works; reproduce with dotnet test", SubjectId: (await win.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();
        Assert.Equal(TaskState.Validating, afterOne.State);

        var afterTwo = await (await web.PostActionAsync(id, "pass", new VerdictRequest("web-validator", "Ran the application: expected output observed; reproduce with dotnet test.", SubjectId: (await web.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();
        Assert.Equal(TaskState.Validated, afterTwo.State);

        var landed = await (await owner.PostAsync(Routes.TaskAction(id, "land"), null)).ReadTaskAsync();
        Assert.Equal(TaskState.Done, landed.State);
        Assert.NotNull(landed.DoneAt);
        Assert.Equal("feature", _repo.Git("show", "main:feature.txt"));
        Assert.StartsWith("Land T-1: Build the feature", _repo.Git("log", "-1", "--format=%s", "main"));
        Assert.True(File.Exists(Path.Combine(_repo.Path, "feature.txt")), "the main checkout's working tree follows the merge");
    }

    [Fact]
    public async Task The_owner_cannot_validate_their_own_work()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        await DefineRolesAsync("win-validator");
        var (owner, id) = await ImplementableTaskAsync();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        (await TakeAsync(owner, "win-validator")).EnsureSuccessStatusCode();

        var self = await owner.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "Ran the application: expected output observed; reproduce with dotnet test.", SubjectId: (await owner.GetTaskAsync(id)).Task.CurrentSubject!.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Equal("self_validation", (await self.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task A_failed_validation_returns_the_task_with_evidence_and_the_next_round_starts_clean()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator", "web-validator"]);
        await DefineRolesAsync("win-validator", "web-validator");
        var (owner, id) = await ImplementableTaskAsync();
        var validator = await _hub.RegisterAgentAsync("validator");
        (await TakeAsync(validator, "win-validator")).EnsureSuccessStatusCode();
        (await TakeAsync(validator, "web-validator")).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(id, "pass", new VerdictRequest("web-validator", "Ran the application: expected output observed; reproduce with dotnet test.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();

        var silent = await validator.PostActionAsync(id, "fail", new VerdictRequest("win-validator", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id));
        Assert.Equal("evidence_required", (await silent.ReadErrorAsync()).Code);

        var failed = await (await validator.PostActionAsync(id, "fail", new VerdictRequest("win-validator", "crashes on launch: repro steps…", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();
        Assert.Equal(TaskState.InProgress, failed.State);
        Assert.Equal("owner", failed.Owner);
        Assert.NotNull(failed.ClaimExpires);

        var history = (await owner.GetTaskAsync(id)).Events;
        Assert.Contains(history, e => e.Type == "validation.failed" && e.Payload.GetProperty("evidence").GetString()!.Contains("crashes on launch"));

        var again = await (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).ReadTaskAsync();
        Assert.Equal(TaskState.Validating, again.State);
        Assert.All(again.Validations, v => Assert.Equal("pending", v.Verdict));
    }

    [Fact]
    public async Task A_blocked_validation_returns_the_task_to_its_owner_and_says_so_to_the_founder()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        await DefineRolesAsync("win-validator");
        var (owner, id) = await ImplementableTaskAsync();
        var validator = await _hub.RegisterAgentAsync("validator");
        (await TakeAsync(validator, "win-validator")).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var silent = await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, silent.StatusCode);
        var refusal = await silent.ReadErrorAsync();
        Assert.Equal("evidence_required", refusal.Code);
        Assert.Contains("what stopped you", refusal.Message);

        const string Reason = "No browser in this session, so the dashboard panel could not be opened.\nTried: the CLI, curl, the test suite.";
        var blocked = await (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", Reason, SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();

        Assert.Equal(TaskState.InProgress, blocked.State);
        Assert.Equal("owner", blocked.Owner);
        Assert.NotNull(blocked.ClaimExpires);   // a fresh claim: the task is not instantly up for grabs
        Assert.Equal("blocked", Assert.Single(blocked.Validations).Verdict);

        var detail = await owner.GetTaskAsync(id);
        Assert.Contains(detail.Events, e => e.Type == "validation.blocked"
            && e.Payload.GetProperty("evidence").GetString()!.Contains("No browser in this session"));
        Assert.Contains(detail.Events, e => e.Type == "task.validation_blocked"
            && e.Payload.GetProperty("owner").GetString() == "owner");

        var toOwner = Assert.Single((await owner.GetFromJsonAsync(Routes.Inbox, MuthurJsonContext.Default.InboxDto))!.Messages);
        Assert.Contains("could not be validated by win-validator", toOwner.Body);
        Assert.Contains("not a verdict on the work", toOwner.Body);

        var toFounder = (await _hub.Founder().GetFromJsonAsync(Routes.Messages + "?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!;
        var told = Assert.Single(toFounder, x => x.Body.Contains("needs something a validator could not supply"));
        Assert.Contains("No browser in this session, so the dashboard panel could not be opened.", told.Body);
        Assert.DoesNotContain("Tried:", told.Body);   // one line, not the whole report
        Assert.Contains("It is back with owner.", told.Body);
    }

    /// <summary>
    /// T-45: eight sessions ended in 'blocked' for want of a browser, three of them on one task. The second
    /// session had nothing available to it that the first did not, so a second block stops the staffing rather
    /// than spending the budget rediscovering the same prerequisite every round the owner re-implements.
    /// </summary>
    [Fact]
    public async Task A_second_block_flags_the_task_for_a_human_and_says_so_once()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        await DefineRolesAsync("win-validator");
        var (owner, id) = await ImplementableTaskAsync();
        var validator = await _hub.RegisterAgentAsync("validator");
        (await TakeAsync(validator, "win-validator")).EnsureSuccessStatusCode();

        // One block is the system working: a session found a missing prerequisite and the owner may well fix it.
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        var once = await (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "No browser here. Tried opening the dashboard URL.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();
        Assert.Null(once.AttendedReason);

        // Two is a class of thing. The owner re-implements, the next session hits the same wall.
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        var twice = await (await validator.PostActionAsync(id, "blocked",
            new VerdictRequest("win-validator", "Still no browser.\nTried: the CLI, curl, the suite.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();

        Assert.Equal("Blocked twice without reaching a verdict. Latest: Still no browser.", twice.AttendedReason);
        var detail = await owner.GetTaskAsync(id);
        Assert.Contains(detail.Events, e => e.Type == "task.attended"
            && e.Payload.GetProperty("source").GetString() == "validation_blocked"
            && e.Payload.GetProperty("blocks").GetInt32() == 2);

        // The founder is told once that it is an incident, and once that it is now a standing fact — not the
        // same sentence twice, and the second names the way out.
        var toFounder = (await _hub.Founder().GetFromJsonAsync(Routes.Messages + "?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!;
        Assert.Single(toFounder, x => x.Body.Contains("needs something a validator could not supply"));
        var named = Assert.Single(toFounder, x => x.Body.Contains("blocked twice without a verdict"));
        Assert.Contains("Still no browser.", named.Body);
        Assert.DoesNotContain("Tried:", named.Body);
        Assert.Contains($"muthur task attended {id} --clear", named.Body);

        // A third block is the flag working, not news: nothing further reaches the founder.
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "Still no browser. Tried opening the dashboard URL.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
        var after = (await _hub.Founder().GetFromJsonAsync(Routes.Messages + "?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!;
        Assert.Equal(toFounder.Count, after.Count);
    }

    /// <summary>
    /// A human already said why a human is needed. That sentence is better than the generated one, and
    /// overwriting it would lose the reason — so the count decides the message, never the flag's text.
    /// </summary>
    [Fact]
    public async Task A_second_block_does_not_overwrite_an_attended_reason_somebody_wrote()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        await DefineRolesAsync("win-validator");
        var (owner, id) = await ImplementableTaskAsync();
        var validator = await _hub.RegisterAgentAsync("validator");
        (await TakeAsync(validator, "win-validator")).EnsureSuccessStatusCode();
        const string Written = "The collision pill renders inside Virtualize, which a prerender does not emit.";
        (await owner.PostActionAsync(id, "attended", new AttendedRequest(Written))).EnsureSuccessStatusCode();

        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "No browser here. Tried opening the dashboard URL.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        var twice = await (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "Still no browser. Tried opening the dashboard URL.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();

        Assert.Equal(Written, twice.AttendedReason);

        // The founder is still told, because that is the count's business and not the flag's.
        var toFounder = (await _hub.Founder().GetFromJsonAsync(Routes.Messages + "?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!;
        Assert.Single(toFounder, x => x.Body.Contains("blocked twice without a verdict"));
    }

    /// <summary>
    /// The thing that must never happen: a validator that could not run must not be counted as one that approved.
    /// </summary>
    [Fact]
    public async Task A_task_one_validator_blocked_is_not_validated_however_the_others_voted()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator", "web-validator"]);
        await DefineRolesAsync("win-validator", "web-validator");
        var (owner, id) = await ImplementableTaskAsync();
        var validator = await _hub.RegisterAgentAsync("validator");
        (await TakeAsync(validator, "win-validator")).EnsureSuccessStatusCode();
        (await TakeAsync(validator, "web-validator")).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        (await validator.PostActionAsync(id, "pass", new VerdictRequest("web-validator", "ran it, works; reproduce with dotnet test", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
        var blocked = await (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "no Windows box in this session", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).ReadTaskAsync();

        Assert.Equal(TaskState.InProgress, blocked.State);
        Assert.NotEqual(TaskState.Validated, blocked.State);
        Assert.Equal(["blocked", "yes"], blocked.Validations.Select(v => v.Verdict).Order());

        // And it cannot be nudged over the line afterwards: the task has left validation.
        var late = await validator.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "changed my mind", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id));
        Assert.Equal("stale_validation_subject", (await late.ReadErrorAsync()).Code);
        Assert.Equal(TaskState.InProgress, (await owner.GetTaskAsync(id)).Task.State);

        var land = await owner.PostAsync(Routes.TaskAction(id, "land"), null);
        Assert.Equal("not_validated", (await land.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Blocking_needs_the_role_and_is_not_open_to_the_owner_either()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        await DefineRolesAsync("win-validator");
        var (owner, id) = await ImplementableTaskAsync();
        var stranger = await _hub.RegisterAgentAsync("stranger");
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var noRole = await stranger.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "cannot run it: dashboard URL is inaccessible", SubjectId: (await stranger.GetTaskAsync(id)).Task.CurrentSubject!.Id));
        Assert.Equal("role_not_held", (await noRole.ReadErrorAsync()).Code);

        (await TakeAsync(owner, "win-validator")).EnsureSuccessStatusCode();
        var self = await owner.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "cannot run it: dashboard URL is inaccessible", SubjectId: (await owner.GetTaskAsync(id)).Task.CurrentSubject!.Id));
        Assert.Equal("self_validation", (await self.ReadErrorAsync()).Code);

        var unknown = await stranger.PostActionAsync(id, "blocked", new VerdictRequest("nobody-validator", "cannot run it: dashboard URL is inaccessible", SubjectId: (await stranger.GetTaskAsync(id)).Task.CurrentSubject!.Id));
        Assert.Equal("validator_not_required", (await unknown.ReadErrorAsync()).Code);

        Assert.Equal(TaskState.Validating, (await owner.GetTaskAsync(id)).Task.State);
    }

    [Fact]
    public async Task Implemented_needs_a_spec_and_a_real_branch()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("No shortcuts");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();

        var ghost = await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/does-not-exist"));
        Assert.Equal("branch_missing", (await ghost.ReadErrorAsync()).Code);

        var onMain = await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("main"));
        Assert.Equal("branch_is_default", (await onMain.ReadErrorAsync()).Code);

        _repo.BranchWithFile("task/T-1-x", "x.txt", "x\n");
        var noSpec = await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-x"));
        Assert.Equal("spec_required", (await noSpec.ReadErrorAsync()).Code);

        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec()))).EnsureSuccessStatusCode();
        _repo.Git("checkout", "-q", "task/T-1-x");
        _repo.Commit("commit the attached frozen spec");
        _repo.Git("checkout", "-q", "main");
        var ok = await (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-x"))).ReadTaskAsync();
        Assert.Equal(TaskState.Validated, ok.State); // the project requires no validators
    }

    [Fact]
    public async Task A_merge_conflict_sends_the_task_back_and_leaves_main_untouched()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var (owner, id) = await ImplementableTaskAsync(file: "README.md", content: "# from the branch\n");
        _repo.Write("README.md", "# from main\n");
        _repo.Commit("main moves on");
        var mainBefore = _repo.Git("rev-parse", "main");
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var land = await owner.PostAsync(Routes.TaskAction(id, "land"), null);

        Assert.Equal(HttpStatusCode.Conflict, land.StatusCode);
        var error = await land.ReadErrorAsync();
        Assert.Equal("merge_conflict", error.Code);
        Assert.Contains("README.md", error.Message);
        Assert.Equal(mainBefore, _repo.Git("rev-parse", "main"));
        Assert.Equal("", _repo.Git("status", "--porcelain", "--untracked-files=no"));
        Assert.Equal(TaskState.InProgress, (await owner.GetTaskAsync(id)).Task.State);
    }

    [Fact]
    public async Task A_dirty_checkout_refuses_the_land_but_keeps_the_task_validated()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var (owner, id) = await ImplementableTaskAsync();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        _repo.Write("README.md", "# uncommitted edit\n");

        var land = await owner.PostAsync(Routes.TaskAction(id, "land"), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, land.StatusCode);
        Assert.Equal("dirty_checkout", (await land.ReadErrorAsync()).Code);
        Assert.Equal(TaskState.Validated, (await owner.GetTaskAsync(id)).Task.State);
    }

    [Fact]
    public async Task Landing_works_when_nobody_has_the_default_branch_checked_out()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var (owner, id) = await ImplementableTaskAsync();
        _repo.Git("checkout", "-q", "-b", "somewhere-else");
        var mainBefore = _repo.Git("rev-parse", "main");
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var landed = await (await owner.PostAsync(Routes.TaskAction(id, "land"), null)).ReadTaskAsync();

        Assert.Equal(TaskState.Done, landed.State);
        Assert.Equal("feature", _repo.Git("show", "main:feature.txt"));
        Assert.Equal(mainBefore, _repo.Git("rev-parse", "main^1"));
        Assert.Equal("somewhere-else", _repo.Git("branch", "--show-current"));
    }

    [Fact]
    public async Task Pr_mode_pushes_the_branch_and_opens_a_pull_request_instead_of_merging()
    {
        using var remote = new TestRepo();
        remote.Git("config", "receive.denyCurrentBranch", "ignore");
        _repo.Git("remote", "add", "origin", remote.Path);
        await _hub.AddProjectAsync(repoPath: _repo.Path, landMode: LandMode.Pr);
        var (owner, id) = await ImplementableTaskAsync();
        var mainBefore = _repo.Git("rev-parse", "main");
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();

        var landed = await (await owner.PostAsync(Routes.TaskAction(id, "land"), null)).ReadTaskAsync();

        Assert.Equal(TaskState.Done, landed.State);
        Assert.Equal("https://github.com/example/repo/pull/1", landed.PrUrl);
        Assert.Equal(mainBefore, _repo.Git("rev-parse", "main"));
        var pinned = $"muthur-approved/{id}/{landed.CurrentSubject!.Id:D}";
        Assert.Equal(landed.CurrentSubject.ImplementationSha, remote.Git("rev-parse", pinned));
        Assert.Equal(("main", pinned, "T-1: Build the feature"), Assert.Single(_hub.PullRequests.Opened));
        Assert.Contains(landed.CurrentSubject.Id.ToString("D"), Assert.Single(_hub.PullRequests.Bodies));
        Assert.Contains(landed.CurrentSubject.ImplementationSha, Assert.Single(_hub.PullRequests.Bodies));
    }

    [Fact]
    public async Task Only_the_owner_or_founder_may_land()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var (owner, id) = await ImplementableTaskAsync();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        var other = await _hub.RegisterAgentAsync("other");

        var denied = await other.PostAsync(Routes.TaskAction(id, "land"), null);

        Assert.Equal("not_owner", (await denied.ReadErrorAsync()).Code);
    }
}
