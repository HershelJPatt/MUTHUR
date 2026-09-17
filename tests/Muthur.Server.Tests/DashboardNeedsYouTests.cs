using System.Net.Http.Json;
using Muthur.Contracts;

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
}
