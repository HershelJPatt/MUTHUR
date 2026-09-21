using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class PushTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new(integrationChecks: true);

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task Landed_work_reaches_the_remote_on_the_next_pass()
    {
        using var remote = new TestRepo(integrationChecks: true);
        Connect(remote);
        await LandAsync();
        var local = _repo.Git("rev-parse", "main");
        Assert.NotEqual(local, remote.Git("rev-parse", "main"));

        var pass = await PushAsync();

        Assert.Equal(1, pass.Attempted);
        Assert.Equal(1, pass.Pushed);
        Assert.Empty(pass.Errors);
        Assert.Equal(local, remote.Git("rev-parse", "main"));
        var pushed = Assert.Single(await EventsAsync(), e => e.Type == "project.pushed");
        Assert.Equal("demo", pushed.Payload.GetProperty("project").GetString());
        Assert.Equal("main", pushed.Payload.GetProperty("branch").GetString());
        Assert.Equal(local, pushed.Payload.GetProperty("commit").GetString());
        var project = await ProjectAsync();
        Assert.Equal(local, project.LastPushedCommit);
        Assert.Equal(_hub.Clock.GetUtcNow(), project.LastPushAt);
        Assert.Equal(project.LastPushAt, project.LastPushAttemptAt);
        Assert.Null(project.LastPushError);
    }

    [Fact]
    public async Task A_second_pass_with_nothing_ahead_does_nothing()
    {
        using var remote = new TestRepo(integrationChecks: true);
        Connect(remote);
        await LandAsync();
        Assert.Equal(1, (await PushAsync()).Pushed);
        var attempt = (await ProjectAsync()).LastPushAttemptAt;
        var events = await EventsAsync();
        _hub.Clock.Advance(TimeSpan.FromSeconds(30));

        var pass = await PushAsync();

        Assert.Equal(0, pass.Pushed);
        Assert.Empty(pass.Errors);
        Assert.Single(await EventsAsync(), e => e.Type == "project.pushed");
        Assert.Equal(events.Count, (await EventsAsync()).Count);
        Assert.Equal(attempt, (await ProjectAsync()).LastPushAttemptAt);
    }

    [Fact]
    public async Task A_project_with_no_remote_is_left_alone()
    {
        await LandAsync();

        var pass = await PushAsync();

        Assert.Equal(0, pass.Pushed);
        Assert.Empty(pass.Errors);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type.StartsWith("project.push", StringComparison.Ordinal));
        Assert.DoesNotContain((await DoctorAsync()).Checks, c => c.Category == "push" && c.Subject == "demo");
        var project = await ProjectAsync();
        Assert.Null(project.LastPushedCommit);
        Assert.Null(project.LastPushAt);
        Assert.Null(project.LastPushAttemptAt);
        Assert.Null(project.LastPushError);
    }

    [Fact]
    public async Task A_remote_that_moved_is_reported_and_never_forced()
    {
        using var remote = new TestRepo(integrationChecks: true);
        Connect(remote);
        AdvanceRemote(remote);
        var remoteBefore = remote.Git("rev-parse", "main");
        await LandAsync();

        var pass = await PushAsync();

        Assert.Equal(0, pass.Pushed);
        Assert.StartsWith("demo: ", Assert.Single(pass.Errors));
        var failing = Assert.Single(await EventsAsync(), e => e.Type == "project.push_failing");
        Assert.Equal("demo", failing.Payload.GetProperty("project").GetString());
        Assert.Contains("'demo'", Assert.Single(await FounderMessagesAsync()).Body);
        Assert.Equal(remoteBefore, remote.Git("rev-parse", "main"));
        var check = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "push" && c.Subject == "demo");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.StartsWith("Last push of 'main' failed: ", check.Detail);
        Assert.Null(check.LastSuccess);
        Assert.NotNull((await ProjectAsync()).LastPushError);
    }

    [Fact]
    public async Task A_failing_push_waits_out_the_backoff()
    {
        using var remote = new TestRepo(integrationChecks: true);
        Connect(remote);
        AdvanceRemote(remote);
        await LandAsync();
        Assert.Single((await PushAsync()).Errors);
        var attempt = (await ProjectAsync()).LastPushAttemptAt;

        var waiting = await PushAsync();

        Assert.Equal(0, waiting.Attempted);
        Assert.Empty(waiting.Errors);
        Assert.Equal(attempt, (await ProjectAsync()).LastPushAttemptAt);
        _hub.Clock.Advance(PushService.RetryAfter);
        var retry = await PushAsync();
        Assert.Equal(1, retry.Attempted);
        Assert.Single(retry.Errors);
        Assert.Equal(_hub.Clock.GetUtcNow(), (await ProjectAsync()).LastPushAttemptAt);
        Assert.Single(await EventsAsync(), e => e.Type == "project.push_failing");
        Assert.Contains("'demo'", Assert.Single(await FounderMessagesAsync()).Body);
    }

    [Fact]
    public async Task A_push_that_starts_working_again_says_so()
    {
        using var remote = new TestRepo(integrationChecks: true);
        Connect(remote);
        AdvanceRemote(remote);
        await LandAsync();
        Assert.Single((await PushAsync()).Errors);
        _repo.Git("checkout", "-q", "main");
        _repo.Git("fetch", "origin");
        _repo.Git("merge", "--no-edit", "origin/main");
        _hub.Clock.Advance(PushService.RetryAfter);

        var pass = await PushAsync();

        Assert.Equal(1, pass.Pushed);
        Assert.Empty(pass.Errors);
        Assert.Equal(_repo.Git("rev-parse", "main"), remote.Git("rev-parse", "main"));
        Assert.Single(await EventsAsync(), e => e.Type == "project.push_recovered");
        var messages = await FounderMessagesAsync();
        Assert.Equal(2, messages.Count);
        Assert.Single(messages, m => m.Body == "'demo' is reaching the remote again: 'main' is pushed.");
        var check = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "push" && c.Subject == "demo");
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal("'main' is on the remote.", check.Detail);
        Assert.Equal(_hub.Clock.GetUtcNow(), check.LastSuccess);
        Assert.Null((await ProjectAsync()).LastPushError);
    }

    [Fact]
    public async Task Pr_mode_projects_are_left_to_their_humans()
    {
        using var remote = new TestRepo(integrationChecks: true);
        Connect(remote);
        var remoteBefore = remote.Git("rev-parse", "main");
        _repo.Write("feature.txt", "feature\n");
        _repo.Commit("local main moves ahead");
        await _hub.AddProjectAsync(repoPath: _repo.Path, landMode: LandMode.Pr);

        var pass = await PushAsync();

        Assert.Equal(0, pass.Attempted);
        Assert.Equal(0, pass.Pushed);
        Assert.Empty(pass.Errors);
        Assert.NotEqual(remoteBefore, _repo.Git("rev-parse", "main"));
        Assert.Equal(remoteBefore, remote.Git("rev-parse", "main"));
        Assert.DoesNotContain((await DoctorAsync()).Checks, c => c.Category == "push");
    }

    private void Connect(TestRepo remote)
    {
        remote.Git("config", "receive.denyCurrentBranch", "ignore");
        _repo.Git("remote", "add", "origin", remote.Path);
        _repo.Git("fetch", "origin");
        // Separately initialized repositories need one shared initial commit, regardless of the wall clock.
        _repo.Git("reset", "--hard", "origin/main");
    }

    private static void AdvanceRemote(TestRepo remote)
    {
        using var other = new TestRepo(integrationChecks: true);
        var clone = Path.Combine(other.Path, "clone");
        other.Git("clone", "-q", remote.Path, clone);
        TestRepo.Run(clone, "config", "user.name", "Test");
        TestRepo.Run(clone, "config", "user.email", "test@example.invalid");
        TestRepo.Run(clone, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(clone, "remote.txt"), "someone else's work\n");
        TestRepo.Run(clone, "add", "remote.txt");
        TestRepo.Run(clone, "commit", "-q", "-m", "remote moves independently");
        remote.Git("fetch", clone, "main");
        remote.Git("merge", "--ff-only", "FETCH_HEAD");
    }

    private async Task LandAsync()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        using var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec()))).EnsureSuccessStatusCode();
        const string Branch = "task/T-1-feature";
        _repo.BranchWithFile(Branch, "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(Branch))).EnsureSuccessStatusCode();
        await _hub.PassIntegrationAsync(_repo, task.Id);
        var landed = await (await owner.PostAsync(Routes.TaskAction(task.Id, "land"), null)).ReadTaskAsync();
        Assert.Equal(TaskState.Done, landed.State);
    }

    private Task<PushPass> PushAsync() => _hub.Services.GetRequiredService<PushService>().PushAsync();

    private Task<Project> ProjectAsync() => _hub.Services.GetRequiredService<Ledger>().ReadAsync((db, _) => db.Projects.SingleAsync());

    private async Task<IReadOnlyList<EventDto>> EventsAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Events}?limit=200", MuthurJsonContext.Default.IReadOnlyListEventDto))!;

    private async Task<IReadOnlyList<MessageDto>> FounderMessagesAsync() =>
        (await _hub.Founder().GetFromJsonAsync($"{Routes.Messages}?founder=true", MuthurJsonContext.Default.IReadOnlyListMessageDto))!;

    private async Task<DoctorDto> DoctorAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe=false", MuthurJsonContext.Default.DoctorDto))!;
}
