using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>The "Needs you" page and the tab badge, driven through the real hub and rendered by the real dashboard.</summary>
public sealed class DashboardNeedsYouTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task An_open_request_is_offered_for_answering_and_counted_in_the_tab_badge()
    {
        await _hub.AddProjectAsync();
        var agent = await _hub.RegisterAgentAsync("asker");
        var task = await agent.AddTaskAsync("Pricing page");
        (await agent.ClaimAsync(task.Id)).EnsureSuccessStatusCode();

        var asked = await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("Monthly or annual first?", task.Id, ["monthly", "annual"]));
        asked.EnsureSuccessStatusCode();
        var request = (await asked.Content.ReadFromJsonAsync(MuthurJsonContext.Default.FounderRequestDto))!;

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");
        Assert.Contains("Monthly or annual first?", page);
        Assert.Contains("asker", page);
        Assert.Contains("Pricing page", page);
        Assert.Contains(">monthly<", page);
        Assert.Contains(">annual<", page);
        Assert.Contains("1 open", page);
        Assert.Contains("class=\"badge\">1<", await _hub.CreateClient().GetStringAsync("/"));

        (await _hub.Founder().PostAsJsonAsync(Routes.RequestAction(request.Id, "answer"), new AnswerRequest("monthly"))).EnsureSuccessStatusCode();

        Assert.Contains("nothing is waiting on you", await _hub.CreateClient().GetStringAsync("/needs-you"));
        Assert.DoesNotContain("class=\"badge\"", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task A_message_to_the_founder_shows_in_the_thread_and_stays_unread_until_the_page_is_interactive()
    {
        var agent = await _hub.RegisterAgentAsync("asker");
        (await agent.PostAsJsonAsync(Routes.Messages, new SendMessageRequest("founder", "deploy is <b>done</b>"))).EnsureSuccessStatusCode();

        Assert.Contains("deploy is &lt;b&gt;done&lt;/b&gt;", await _hub.CreateClient().GetStringAsync("/needs-you"));
        Assert.Contains("class=\"badge\">1<", await _hub.CreateClient().GetStringAsync("/"));

        // Prerendering the page is not the founder reading it, so the badge must survive a second look.
        Assert.Contains("class=\"badge\">1<", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task A_message_waiting_for_the_founder_is_on_the_page_the_badge_points_at()
    {
        var draft = await AwaitingOutboundAsync("press", "We are shutting down the free tier on March 1.");

        var attention = await _hub.Services.GetRequiredService<FounderAttention>().ReadAsync();
        Assert.Equal(1, attention.Total);
        Assert.Equal(draft.Id, Assert.Single(attention.Outbound).Id);
        Assert.Empty(attention.Requests);

        // The defect this task fixes: the badge read 1 while the page's whole answer was "nothing is waiting on you".
        var page = await _hub.CreateClient().GetStringAsync("/needs-you");
        Assert.Contains(draft.Id, page);
        Assert.Contains("We are shutting down the free tier on March 1.", page);
        Assert.Contains(">Approve<", page);
        Assert.Contains(">Decline<", page);
        Assert.Contains("1 waiting", page);
        Assert.DoesNotContain("nothing is waiting to be sent", page);
        Assert.Contains("class=\"badge\">1<", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task A_request_and_a_waiting_message_both_render_with_the_requests_first()
    {
        var agent = await _hub.RegisterAgentAsync("asker");
        (await agent.PostAsJsonAsync(Routes.Requests, new AskRequest("Monthly or annual first?"))).EnsureSuccessStatusCode();
        var draft = await AwaitingOutboundAsync("press", "The beta opens on Tuesday.");

        var attention = await _hub.Services.GetRequiredService<FounderAttention>().ReadAsync();
        Assert.Equal(2, attention.Total);

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");
        Assert.Contains("Monthly or annual first?", page);
        Assert.Contains(draft.Id, page);
        Assert.True(page.IndexOf("Monthly or annual first?", StringComparison.Ordinal) < page.IndexOf(draft.Id, StringComparison.Ordinal),
            "what the founder is asked comes before what wants to leave");
        Assert.Contains("class=\"badge\">2<", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task Approving_a_waiting_message_takes_it_off_the_page_and_out_of_the_badge()
    {
        var draft = await AwaitingOutboundAsync("press", "The beta opens on Tuesday.");

        // The panel's Approve button calls OutboundService.FounderApproveAsync, which is the route below; the existing
        // Operations tests drive it the same way, because a prerendered page cannot be clicked.
        Assert.Contains(">Approve<", await _hub.CreateClient().GetStringAsync("/needs-you"));
        (await _hub.Founder().PostAsync(Routes.OutboundAction(draft.Id, "approve"), null)).EnsureSuccessStatusCode();

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");
        Assert.DoesNotContain(">Approve<", page);
        Assert.DoesNotContain("The beta opens on Tuesday.", page);   // the id survives in the thread; the message itself has gone
        Assert.Contains("nothing is waiting to be sent", page);
        Assert.Equal(0, (await _hub.Services.GetRequiredService<FounderAttention>().ReadAsync()).Total);
        Assert.DoesNotContain("class=\"badge\"", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task An_empty_hub_says_so_in_both_halves_and_wears_no_badge()
    {
        Assert.Equal(0, (await _hub.Services.GetRequiredService<FounderAttention>().ReadAsync()).Total);

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");
        Assert.Contains("nothing is waiting on you", page);
        Assert.Contains("nothing is waiting to be sent", page);
        Assert.DoesNotContain("class=\"badge\"", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task A_message_that_is_not_waiting_on_the_founder_stays_on_the_gate_page()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.OutboundTargets,
            new DefineTargetRequest("press", "file", Path.Combine(_hub.DataDir, "press-outbox.txt")))).EnsureSuccessStatusCode();
        var draft = await ReviewedOutboundAsync("press", "The beta opens on Tuesday.");

        var operations = await _hub.CreateClient().GetStringAsync("/operations");
        Assert.Contains(draft.Id, operations);
        Assert.Contains("The beta opens on Tuesday.", operations);
        Assert.Contains("nothing is waiting to be sent", await _hub.CreateClient().GetStringAsync("/needs-you"));
        Assert.DoesNotContain("The beta opens on Tuesday.", await _hub.CreateClient().GetStringAsync("/needs-you"));
        Assert.Equal(0, (await _hub.Services.GetRequiredService<FounderAttention>().ReadAsync()).Total);
    }

    /// <summary>A peer-approved message against a target only the founder can release: the gate's "awaiting_founder".</summary>
    private async Task<OutboundDto> AwaitingOutboundAsync(string target, string body)
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.OutboundTargets,
            new DefineTargetRequest(target, "file", Path.Combine(_hub.DataDir, $"{target}-outbox.txt"), RequiresFounderApproval: true))).EnsureSuccessStatusCode();
        return await ReviewedOutboundAsync(target, body);
    }

    private async Task<OutboundDto> ReviewedOutboundAsync(string target, string body)
    {
        var author = await _hub.RegisterAgentAsync("author");
        var peer = await _hub.RegisterAgentAsync("peer");
        var drafted = await author.PostAsJsonAsync(Routes.Outbound, new DraftOutboundRequest(target, body));
        drafted.EnsureSuccessStatusCode();
        var draft = (await drafted.Content.ReadFromJsonAsync(MuthurJsonContext.Default.OutboundDto))!;
        (await peer.PostAsJsonAsync(Routes.OutboundAction(draft.Id, "review"), new ReviewOutboundRequest(true, draft.Sha256))).EnsureSuccessStatusCode();
        return draft;
    }
}
