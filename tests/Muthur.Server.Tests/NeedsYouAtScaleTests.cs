using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The Needs You page at two hundred rather than at two: ordered by what each thing is costing, grouped so a
/// question five agents asked in the same words is read once, and honest about how long it has waited.
/// Nothing here answers, defers or expires anything — a request that ages out is a decision made by silence.
/// </summary>
public sealed class NeedsYouAtScaleTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private RequestService Requests => _hub.Services.GetRequiredService<RequestService>();
    private FounderAttention Attention => _hub.Services.GetRequiredService<FounderAttention>();

    /// <summary>A claimed task of this agent's, optionally a child of another.</summary>
    private static async Task<string> TaskAsync(HttpClient agent, string title, string? parent = null)
    {
        var added = await agent.PostAsJsonAsync(Routes.Tasks, new AddTaskRequest(title, Parent: parent));
        added.EnsureSuccessStatusCode();
        var task = (await added.Content.ReadFromJsonAsync(MuthurJsonContext.Default.TaskDto))!;
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        return task.Id;
    }

    private static async Task<int> AskAsync(HttpClient agent, string question, string? task = null, params string[] options)
    {
        var asked = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest(question, task, options));
        asked.EnsureSuccessStatusCode();
        return (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!.Id;
    }

    [Fact]
    public async Task Questions_come_back_in_the_order_of_what_they_are_costing()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");

        // Asked first, and costing the least: it blocks nothing.
        var idle = await AskAsync(agent, "Which font?");
        _hub.Clock.Advance(TimeSpan.FromMinutes(1));

        // Blocks a task, but nothing is waiting behind that task.
        var alone = await TaskAsync(agent, "Pricing page");
        var blocking = await AskAsync(agent, "Monthly or annual?", alone);
        _hub.Clock.Advance(TimeSpan.FromMinutes(1));

        // Asked last, and costing the most: two unfinished tasks are stopped behind the one it blocks.
        var parent = await TaskAsync(agent, "Rewrite billing");
        await TaskAsync(agent, "Invoices", parent);
        await TaskAsync(agent, "Receipts", parent);
        var expensive = await AskAsync(agent, "Stripe or Adyen?", parent);

        var open = await Requests.ListAsync(openOnly: true);

        Assert.Equal([expensive, blocking, idle], open.Select(r => r.Id));
        Assert.Equal(2, open[0].Dependents);
        Assert.True(open[0].BlocksTask);
        Assert.Equal(0, open[1].Dependents);
        Assert.True(open[1].BlocksTask);
        Assert.False(open[2].BlocksTask);
    }

    [Fact]
    public async Task A_child_that_is_finished_is_not_waiting_behind_anything()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        var parent = await TaskAsync(agent, "Rewrite billing");
        var cancelled = await TaskAsync(agent, "Invoices", parent);
        await TaskAsync(agent, "Receipts", parent);
        (await agent.PostActionAsync(cancelled, "cancel", new CancelTaskRequest("not needed"))).EnsureSuccessStatusCode();

        await AskAsync(agent, "Stripe or Adyen?", parent);

        var only = Assert.Single(await Requests.ListAsync(openOnly: true));
        Assert.Equal(1, only.Dependents);   // the cancelled child is nobody's cost
    }

    [Fact]
    public async Task Among_equals_the_one_that_has_waited_longest_comes_first()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        var first = await AskAsync(agent, "Which font?");
        _hub.Clock.Advance(TimeSpan.FromHours(3));
        var second = await AskAsync(agent, "Which colour?");

        Assert.Equal([first, second], (await Requests.ListAsync(openOnly: true)).Select(r => r.Id));
    }

    [Fact]
    public async Task An_answered_question_is_not_ranked_it_is_just_history()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        var idle = await AskAsync(agent, "Which font?");
        var parent = await TaskAsync(agent, "Rewrite billing");
        await TaskAsync(agent, "Invoices", parent);
        var expensive = await AskAsync(agent, "Stripe or Adyen?", parent);
        await Requests.AnswerAsync(Caller.Founder, idle, new AnswerRequest("Inter"));
        await Requests.AnswerAsync(Caller.Founder, expensive, new AnswerRequest("Stripe"));

        // Closed requests keep the id order: nothing is waiting on an answered question, so there is no cost.
        Assert.Equal([idle, expensive], (await Requests.ListAsync(openOnly: false)).Select(r => r.Id));
    }

    [Fact]
    public async Task The_badge_says_how_long_as_well_as_how_many()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        Assert.Null((await Attention.ReadAsync()).OldestWaitingSince);

        var asked = _hub.Clock.GetUtcNow();
        await AskAsync(agent, "Which font?");
        _hub.Clock.Advance(TimeSpan.FromHours(5));
        await AskAsync(agent, "Which colour?");

        var attention = await Attention.ReadAsync();
        Assert.Equal(2, attention.Total);
        Assert.Equal(asked, attention.OldestWaitingSince);

        // And it reaches the top bar of every page, beside the count.
        var page = await _hub.CreateClient().GetStringAsync("/");
        Assert.Contains("class=\"badge\">2<", page);
        Assert.Contains("class=\"badge-age\">5h<", page);
    }

    [Fact]
    public async Task An_unread_message_is_counted_but_does_not_age_the_queue()
    {
        // A message blocks nobody. Letting one set the headline age would report the queue as older than it is.
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        (await agent.PostAsJsonAsync(Routes.Messages, new SendMessageRequest("founder", "morning")))
            .EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromDays(2));
        var asked = _hub.Clock.GetUtcNow();
        await AskAsync(agent, "Which font?");

        var attention = await Attention.ReadAsync();

        Assert.Equal(2, attention.Total);
        Assert.Equal(asked, attention.OldestWaitingSince);
    }

    [Fact]
    public async Task A_question_two_agents_asked_in_the_same_words_is_one_group_that_still_shows_both()
    {
        await _hub.AddProjectAsync();
        var first = await _hub.RegisterAgentAsync("top-right");
        var second = await _hub.RegisterAgentAsync("bottom-left");
        await AskAsync(first, "Land onto a red main?", null, "hold", "land");
        await AskAsync(second, "Land onto a red main?", null, "hold", "land");

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");

        Assert.Contains("group-head", page);
        Assert.Contains("2 waiting", page);
        Assert.Contains("answer all 2 at once", page);
        // Grouping is for reading, not for hiding: both askers are still on the page, each with its own row.
        Assert.Contains("top-right", page);
        Assert.Contains("bottom-left", page);
        Assert.Contains("#1", page);
        Assert.Contains("#2", page);
    }

    [Fact]
    public async Task One_question_is_rendered_exactly_as_it_always_was()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        await AskAsync(agent, "Which font?", null, "Inter", "Söhne");

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");

        Assert.DoesNotContain("group-head", page);
        Assert.DoesNotContain("answer all", page);
        Assert.Contains("Which font?", page);
        Assert.Contains(">Inter<", page);
    }

    [Fact]
    public async Task Two_questions_that_merely_look_alike_are_not_one_group()
    {
        // Near-matching would be guessing, and guessing wrong answers a question the founder did not read.
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        await AskAsync(agent, "Land onto a red main?", null, "hold", "land");
        await AskAsync(agent, "Land onto a red main?", null, "hold", "land", "ask me again");

        Assert.DoesNotContain("group-head", await _hub.CreateClient().GetStringAsync("/needs-you"));
    }

    [Fact]
    public async Task A_blocking_question_says_what_it_is_holding_up()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        var parent = await TaskAsync(agent, "Rewrite billing");
        await TaskAsync(agent, "Invoices", parent);
        await TaskAsync(agent, "Receipts", parent);
        await AskAsync(agent, "Stripe or Adyen?", parent);

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");

        Assert.Contains($"blocking {parent}", page);
        Assert.Contains("2 behind it", page);
    }

    [Fact]
    public async Task Answering_a_group_answers_every_request_in_it_one_at_a_time()
    {
        // The service is what the group button calls, once per request, so this asserts the thing that matters:
        // every unblock, every ledger event and every message happens as if they had been clicked separately.
        await _hub.AddProjectAsync();
        var first = await _hub.RegisterAgentAsync("top-right");
        var second = await _hub.RegisterAgentAsync("bottom-left");
        var one = await TaskAsync(first, "Pricing page");
        var two = await TaskAsync(second, "Billing page");
        var askedOne = await AskAsync(first, "Land onto a red main?", one, "hold", "land");
        var askedTwo = await AskAsync(second, "Land onto a red main?", two, "hold", "land");

        foreach (var id in new[] { askedOne, askedTwo })
            await Requests.AnswerAsync(Caller.Founder, id, new AnswerRequest("hold"));

        Assert.Empty(await Requests.ListAsync(openOnly: true));
        var closed = await Requests.ListAsync(openOnly: false);
        Assert.All(closed, r => Assert.Equal("answered", r.Status));
        Assert.All(closed, r => Assert.Equal("hold", r.Answer));

        // Both tasks came back out of 'blocked', and the ledger holds one answer each, not one for the pair.
        Assert.Equal(TaskState.InProgress, (await first.GetTaskAsync(one)).Task.State);
        Assert.Equal(TaskState.InProgress, (await second.GetTaskAsync(two)).Task.State);
        var events = (await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto))!;
        Assert.Equal(2, events.Count(e => e.Type == "request.answered"));
    }
}
