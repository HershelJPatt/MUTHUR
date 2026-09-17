using System.Net;
using System.Net.Http.Json;
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
        await Task.Delay(150);
        Assert.False(waiting.IsCompleted, "nothing to deliver yet, so the call must still be blocked");

        (await SendAsync(sender, "waiter", "wake up")).EnsureSuccessStatusCode();

        var inbox = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(inbox.TimedOut);
        Assert.Equal("wake up", Assert.Single(inbox.Messages).Body);
    }

    [Fact]
    public async Task A_waiting_inbox_times_out_empty()
    {
        var waiter = await _hub.RegisterAgentAsync("patient");

        var waiting = InboxAsync(waiter, wait: 60);
        await Task.Delay(150);
        _hub.Clock.Advance(TimeSpan.FromSeconds(61));

        var inbox = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
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

        var inbox = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("monthly", Assert.Single(inbox.Messages).Body);
        var resumed = (await agent.GetTaskAsync(task.Id)).Task;
        Assert.Equal(TaskState.InProgress, resumed.State);
        Assert.True(resumed.ClaimExpires > _hub.Clock.GetUtcNow(), "the owner gets a fresh lease after a long wait");

        var again = await _hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("changed my mind"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
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
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/T-1.md"))).EnsureSuccessStatusCode();
        repo.BranchWithFile("task/T-1-notify", "n.txt", "n\n");

        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-notify"))).EnsureSuccessStatusCode();
        var forValidator = Assert.Single((await InboxAsync(validator)).Messages);
        Assert.Equal("muthur", forValidator.From);
        Assert.Contains("ready for validation", forValidator.Body);
        Assert.Equal("T-1", forValidator.Task);

        (await validator.PostActionAsync(task.Id, "fail", new VerdictRequest("win-validator", "does not start"))).EnsureSuccessStatusCode();
        Assert.Contains("failed validation", Assert.Single((await InboxAsync(owner)).Messages).Body);
    }
}
