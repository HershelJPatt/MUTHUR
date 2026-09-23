using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class MessagingTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private static async Task<InboxDto> InboxAsync(HttpClient agent, int wait = 0, bool peek = false) =>
        (await agent.GetFromJsonAsync($"{Routes.Inbox}?wait={wait}&peek={peek.ToString().ToLowerInvariant()}", MuthurJsonContext.Default.InboxDto))!;

    private static Task<HttpResponseMessage> SendAsync(HttpClient from, string to, string body, bool blocking = false) =>
        from.PostAsJsonAsync(Routes.Messages, new SendMessageRequest(to, body, blocking));

    [Fact]
    public async Task A_message_is_delivered_once_to_its_recipient_only()
    {
        var alice = await _hub.RegisterAgentAsync("alice");
        var bob = await _hub.RegisterAgentAsync("bob");
        var carol = await _hub.RegisterAgentAsync("carol");

        (await SendAsync(alice, "bob", "the build is red on main")).EnsureSuccessStatusCode();

        Assert.Empty((await InboxAsync(carol)).Messages);
        Assert.Single((await InboxAsync(bob, peek: true)).Messages);
        var delivered = Assert.Single((await InboxAsync(bob)).Messages);
        Assert.Equal("alice", delivered.From);
        Assert.Equal("the build is red on main", delivered.Body);
        Assert.Empty((await InboxAsync(bob)).Messages);
    }

    [Fact]
    public async Task A_message_to_a_role_reaches_whoever_holds_it()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("comms-oncall"))).EnsureSuccessStatusCode();
        var sender = await _hub.RegisterAgentAsync("sender");
        var oncall = await _hub.RegisterAgentAsync("oncall");
        (await SendAsync(sender, "role:comms-oncall", "a user reported a crash")).EnsureSuccessStatusCode();

        Assert.Empty((await InboxAsync(oncall)).Messages); // not holding the role yet

        (await oncall.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();
        var delivered = Assert.Single((await InboxAsync(oncall)).Messages);
        Assert.Equal("role:comms-oncall", delivered.To);
    }

    [Fact]
    public async Task Unknown_recipients_are_rejected()
    {
        var agent = await _hub.RegisterAgentAsync("lonely");
        var response = await SendAsync(agent, "nobody", "hello?");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_waiting_inbox_wakes_when_a_message_arrives()
    {
        var waiter = await _hub.RegisterAgentAsync("waiter");
        var sender = await _hub.RegisterAgentAsync("sender");

        var waiting = InboxAsync(waiter, wait: 600);
        Assert.False(waiting.IsCompleted, "nothing to deliver yet, so the call must still be blocked");

        (await SendAsync(sender, "waiter", "wake up")).EnsureSuccessStatusCode();

        var inbox = await Eventually.CompletesAsync(waiting, "the inbox never woke for the message that was sent");
        Assert.False(inbox.TimedOut);
        Assert.Equal("wake up", Assert.Single(inbox.Messages).Body);
    }

    [Fact]
    public async Task A_waiting_inbox_times_out_empty()
    {
        var waiter = await _hub.RegisterAgentAsync("patient");

        var waiting = InboxAsync(waiter, wait: 60);

        var inbox = await Eventually.AfterAdvancingAsync(waiting, _hub.Clock, TimeSpan.FromSeconds(61),
            "the inbox never timed out, however far the clock was pushed");
        Assert.True(inbox.TimedOut);
        Assert.Empty(inbox.Messages);
    }

    [Fact]
    public async Task Asking_a_founder_blocks_the_task_and_the_answer_unblocks_it_and_wakes_the_asker()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        var task = await agent.AddTaskAsync("Pricing page");
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();

        var asked = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("Monthly or annual billing first?", task.Id, ["monthly", "annual"]));
        asked.EnsureSuccessStatusCode();
        var request = (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;
        Assert.Equal(TaskState.Blocked, (await agent.GetTaskAsync(task.Id)).Task.State);

        // A blocked task's claim does not lapse while the founder is away.
        _hub.Clock.Advance(TimeSpan.FromHours(20));
        var other = await _hub.RegisterAgentAsync("opportunist");
        Assert.Equal(HttpStatusCode.Conflict, (await other.ClaimAsync(task.Id)).StatusCode);

        var byAgent = await agent.PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("annual, obviously"));
        Assert.Equal(HttpStatusCode.Unauthorized, byAgent.StatusCode);

        var waiting = InboxAsync(agent, wait: 600);
        (await _hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("monthly"))).EnsureSuccessStatusCode();

        var inbox = await Eventually.CompletesAsync(waiting, "the asker was never woken by the founder's answer");
        Assert.Contains("monthly", Assert.Single(inbox.Messages).Body);
        var resumed = (await agent.GetTaskAsync(task.Id)).Task;
        Assert.Equal(TaskState.InProgress, resumed.State);
        Assert.True(resumed.ClaimExpires > _hub.Clock.GetUtcNow(), "the owner gets a fresh lease after a long wait");

        var again = await _hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("changed my mind"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>A claimed task whose owner asked <paramref name="question"/> and the founder answered <paramref name="answer"/>.</summary>
    private async Task<(HttpClient Agent, string TaskId)> AnsweredAsync(string question, string answer)
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        var task = await agent.AddTaskAsync("Pricing page");
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var asked = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest(question, task.Id, ["monthly", "annual"]));
        var request = (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;
        (await _hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest(answer))).EnsureSuccessStatusCode();
        Assert.Equal(TaskState.InProgress, (await agent.GetTaskAsync(task.Id)).Task.State);
        return (agent, task.Id);
    }

    /// <summary>
    /// The 2026-09-23 founder-question fixture: the orchestrator asked again after its question was answered yes,
    /// blocking the task a second time. The repeat is refused with the answer in hand and the task stays in progress.
    /// </summary>
    [Theory]
    [InlineData("Should the marker file end with a newline?")]
    [InlineData("  should the MARKER file   end with a newline  ")]
    [InlineData("Should the marker file end with a newline?!")]
    public async Task Asking_again_what_the_task_already_has_an_answer_to_is_refused_with_the_answer(string repeat)
    {
        var (agent, taskId) = await AnsweredAsync("Should the marker file end with a newline?", "Yes, one trailing newline.");

        var again = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest(repeat, taskId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var error = await again.ReadErrorAsync();
        Assert.Equal("question_already_answered", error.Code);
        Assert.Contains("Yes, one trailing newline.", error.Message);
        Assert.Equal(TaskState.InProgress, (await agent.GetTaskAsync(taskId)).Task.State);
    }

    [Fact]
    public async Task A_genuinely_different_second_question_is_still_asked()
    {
        var (agent, taskId) = await AnsweredAsync("Should the marker file end with a newline?", "Yes.");

        var again = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("Should the marker file be committed on main?", taskId));

        again.EnsureSuccessStatusCode();
        Assert.Equal(TaskState.Blocked, (await agent.GetTaskAsync(taskId)).Task.State);
    }

    [Fact]
    public async Task A_waiting_inbox_ends_the_moment_the_hub_stops()
    {
        var waiter = await _hub.RegisterAgentAsync("idle");
        var waiting = InboxAsync(waiter, wait: 900);
        // The hub must be told to stop only once the request is really parked in the long poll: a request
        // dispatched after StopApplication() meets a disposed service provider, not a cancelled wait.
        await Eventually.TrueAsync(() => _hub.Clock.Timers.Contains(TimeSpan.FromSeconds(900)),
            "the inbox never reached its 900-second wait on the hub's clock");
        Assert.False(waiting.IsCompleted);

        _hub.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        var inbox = await Eventually.CompletesAsync(waiting, "the inbox did not end when the hub stopped");
        Assert.True(inbox.TimedOut);
        Assert.Empty(inbox.Messages);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused()
    {
        var agent = await _hub.RegisterAgentAsync("verbose");
        await _hub.RegisterAgentAsync("reader");
        var response = await SendAsync(agent, "reader", new string('x', 64 * 1024 + 1));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("body_too_long", (await response.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Releasing_a_blocked_task_withdraws_its_open_questions()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("quitter");
        var task = await agent.AddTaskAsync("Abandoned question");
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("Which way?", task.Id))).EnsureSuccessStatusCode();

        (await agent.PostActionAsync(task.Id, "release", new ReleaseTaskRequest("giving up"))).EnsureSuccessStatusCode();

        var open = await _hub.CreateClient().GetFromJsonAsync(Routes.Requests, MuthurJsonContext.Default.IReadOnlyListFounderRequestDto);
        Assert.Empty(open!);
    }

    [Fact]
    public async Task A_founder_who_withdraws_a_question_tells_the_asker()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("waiting");
        var task = await agent.AddTaskAsync("Needs a decision");
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var asked = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("Ship on Friday?", task.Id));
        var request = (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;

        (await _hub.Founder().PostAsync(Routes.RequestAction(request.Id, "cancel"), null)).EnsureSuccessStatusCode();

        var told = Assert.Single((await InboxAsync(agent)).Messages);
        Assert.Contains("withdrawn", told.Body);
        Assert.Equal(TaskState.InProgress, (await agent.GetTaskAsync(task.Id)).Task.State);

        // Withdrawing your own question is not news to you.
        var second = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("Never mind?", task.Id));
        var own = (await second.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;
        (await agent.PostAsync(Routes.RequestAction(own.Id, "cancel"), null)).EnsureSuccessStatusCode();
        Assert.Empty((await InboxAsync(agent)).Messages);
    }

    [Fact]
    public async Task Asking_about_a_task_that_is_not_in_progress_says_so()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("early");
        var task = await agent.AddTaskAsync("Still in the backlog");

        var response = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("May I?", task.Id));

        Assert.Equal("not_in_progress", (await response.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task The_hub_tells_validators_and_owners_what_needs_them()
    {
        using var repo = new TestRepo();
        await _hub.AddProjectAsync(repoPath: repo.Path, validators: ["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator"))).EnsureSuccessStatusCode();
        var owner = await _hub.RegisterAgentAsync("owner");
        var validator = await _hub.RegisterAgentAsync("validator");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        var task = await owner.AddTaskAsync("Notify me");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(repo.WriteSpec()))).EnsureSuccessStatusCode();
        repo.BranchWithFile("task/T-1-notify", "n.txt", "n\n");

        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-notify"))).EnsureSuccessStatusCode();
        var forValidator = Assert.Single((await InboxAsync(validator)).Messages);
        Assert.Equal("muthur", forValidator.From);
        Assert.Contains("ready for validation", forValidator.Body);
        Assert.Equal("T-1", forValidator.Task);

        (await validator.PostActionAsync(task.Id, "fail", new VerdictRequest("win-validator", "does not start: reproduce by launching the application", SubjectId: (await validator.GetTaskAsync(task.Id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
        Assert.Contains("failed validation", Assert.Single((await InboxAsync(owner)).Messages).Body);
    }

    /// <summary>
    /// U6.0: the orchestrate procedure now waits on its own inbox after `muthur task implemented` instead of
    /// exiting cold, so a failed validation is fixed and resubmitted in the same session rather than costing a
    /// re-staffed orchestrator. This is the wait, and the fix-and-resubmit round trip, end to end.
    /// </summary>
    [Fact]
    public async Task An_orchestrator_waiting_on_its_inbox_after_implemented_is_woken_by_the_verdict()
    {
        using var repo = new TestRepo();
        await _hub.AddProjectAsync(repoPath: repo.Path, validators: ["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator"))).EnsureSuccessStatusCode();
        // Named the way OrchestratorSessionLauncher.IdentityName does, so the scenario reads as the real one.
        var owner = await _hub.RegisterAgentAsync("orchestrator-t-1");
        var validator = await _hub.RegisterAgentAsync("validator");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        var task = await owner.AddTaskAsync("Ship it");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var specPath = repo.WriteSpec();
        repo.Commit("write the spec");   // on main, so every branch cut from it below carries the spec too
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(specPath))).EnsureSuccessStatusCode();
        repo.BranchWithFile("task/T-1-wrong", "n.txt", "wrong\n");

        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-wrong"))).EnsureSuccessStatusCode();

        // `muthur msg inbox --wait 90` in the same session, not a second claim.
        var waitingForFail = InboxAsync(owner, wait: 600);
        Assert.False(waitingForFail.IsCompleted, "nothing to deliver yet, so the wait must still be blocked");
        var firstSubject = (await validator.GetTaskAsync(task.Id)).Task.CurrentSubject!.Id;
        (await validator.PostActionAsync(task.Id, "fail", new VerdictRequest("win-validator", "n.txt does not match; reproduce with git show", firstSubject))).EnsureSuccessStatusCode();

        var failInbox = await Eventually.CompletesAsync(waitingForFail, "the orchestrator was never woken by its own validation failure");
        Assert.False(failInbox.TimedOut);
        Assert.Contains("failed validation", Assert.Single(failInbox.Messages).Body);
        Assert.Equal(TaskState.InProgress, (await owner.GetTaskAsync(task.Id)).Task.State);

        // Fixed and resubmitted here, in the same session that read the failure — no second `task claim`.
        repo.BranchWithFile("task/T-1-fixed", "n.txt", "n\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-fixed"))).EnsureSuccessStatusCode();

        var waitingForPass = InboxAsync(owner, wait: 600);
        var secondSubject = (await validator.GetTaskAsync(task.Id)).Task.CurrentSubject!.Id;
        (await validator.PostActionAsync(task.Id, "pass", new VerdictRequest("win-validator", "n.txt now matches; reproduce with git show", secondSubject))).EnsureSuccessStatusCode();

        var passInbox = await Eventually.CompletesAsync(waitingForPass, "the orchestrator was never woken by the passing verdict");
        Assert.False(passInbox.TimedOut);
        Assert.Contains("passed every validator", Assert.Single(passInbox.Messages).Body);
        Assert.Equal(TaskState.Validated, (await owner.GetTaskAsync(task.Id)).Task.State);

        var claims = (await owner.GetTaskAsync(task.Id)).Events.Count(e => e.Type == "task.claimed");
        Assert.Equal(1, claims);
    }
}
