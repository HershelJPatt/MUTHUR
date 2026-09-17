using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class IngestTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private async Task<IReadOnlyList<InboundDto>> InboundAsync(string status = "unclaimed") =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Inbound}?status={status}", MuthurJsonContext.Default.IReadOnlyListInboundDto))!;

    private Task<PollResultDto> PollAsync() => _hub.Services.GetRequiredService<IngestService>().PollAsync();

    [Fact]
    public async Task What_happened_while_the_hub_was_off_is_caught_up_from_the_cursor_without_duplicates()
    {
        await _hub.AddProjectAsync(ingest: ["fake:acme/widgets"]);
        _hub.Source.Add("issue/1", "Crash on start", updatedAt: "2026-01-01T10:00:00Z");

        Assert.Equal(1, (await PollAsync()).NewItems);
        Assert.Null(_hub.Source.LastCursorSeen); // the very first poll has no cursor
        Assert.Equal("2026-01-01T10:00:00Z", (await SourcesAsync()).Single().Cursor);

        // The hub goes away; two more issues are opened; it comes back and polls with the cursor it stored.
        _hub.Source.Add("issue/2", "Typo in docs", updatedAt: "2026-01-01T11:00:00Z");
        _hub.Source.Add("issue/3", "Feature: dark mode", updatedAt: "2026-01-02T09:00:00Z");

        var caughtUp = await PollAsync();
        Assert.Equal(2, caughtUp.NewItems);
        Assert.Equal("2026-01-01T10:00:00Z", _hub.Source.LastCursorSeen);
        Assert.Equal(0, (await PollAsync()).NewItems); // the source re-reports the newest item; it is not duplicated

        var inbound = await InboundAsync();
        Assert.Equal(["I-1", "I-2", "I-3"], inbound.Select(i => i.Id));
        Assert.All(inbound, i => Assert.Equal("demo", i.Project));
        Assert.Equal("2026-01-02T09:00:00Z", (await SourcesAsync()).Single().Cursor);
    }

    [Fact]
    public async Task A_failing_source_keeps_its_cursor_and_the_failure_is_visible_until_it_recovers()
    {
        await _hub.AddProjectAsync(ingest: ["fake:acme/widgets"]);
        _hub.Source.Add("issue/1", "First", updatedAt: "2026-01-01T10:00:00Z");
        await PollAsync();

        _hub.Source.FailWith = "gh: not logged in";
        var failed = await PollAsync();
        Assert.Contains("not logged in", Assert.Single(failed.Errors));
        var source = (await SourcesAsync()).Single();
        Assert.Equal("2026-01-01T10:00:00Z", source.Cursor);
        Assert.Contains("not logged in", source.LastError);
        await PollAsync(); // still failing: recorded once, not on every pass

        _hub.Source.FailWith = null;
        await PollAsync();
        Assert.Null((await SourcesAsync()).Single().LastError);

        var events = (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Events}?limit=200", MuthurJsonContext.Default.IReadOnlyListEventDto))!;
        Assert.Single(events, e => e.Type == "ingest.failing");
        Assert.Single(events, e => e.Type == "ingest.recovered");
    }

    [Fact]
    public async Task An_unknown_scheme_is_reported_not_thrown()
    {
        await _hub.AddProjectAsync(ingest: ["carrier-pigeon:coop-7"]);
        var result = await PollAsync();
        Assert.Contains("No ingest adapter", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task One_agent_claims_an_item_and_turns_it_into_a_task()
    {
        await _hub.AddProjectAsync();
        var intake = await _hub.RegisterAgentAsync("intake");
        var other = await _hub.RegisterAgentAsync("other");
        var pushed = await intake.PostAsJsonAsync(Routes.Inbound,
            new AddInboundRequest("email", "msg-42", "Invoice export is broken", "Steps: …", "https://mail.example/42", "customer@example.com"));
        pushed.EnsureSuccessStatusCode();

        var again = await intake.PostAsJsonAsync(Routes.Inbound, new AddInboundRequest("email", "msg-42", "Invoice export is broken (resent)"));
        Assert.Equal("I-1", (await again.Content.ReadFromJsonAsync(MuthurJsonContext.Default.InboundDto))!.Id);
        Assert.Single(await InboundAsync());

        var results = await Task.WhenAll(intake.PostAsync(Routes.InboundAction("I-1", "claim"), null), other.PostAsync(Routes.InboundAction("I-1", "claim"), null));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var winner = results[0].StatusCode == HttpStatusCode.OK ? intake : other;
        var loser = winner == intake ? other : intake;

        var denied = await loser.PostAsJsonAsync(Routes.InboundAction("I-1", "convert"), new ConvertInboundRequest());
        Assert.Equal("not_claimer", (await denied.ReadErrorAsync()).Code);

        var converted = await winner.PostAsJsonAsync(Routes.InboundAction("I-1", "convert"), new ConvertInboundRequest(Priority: 2));
        var item = (await converted.Content.ReadFromJsonAsync(MuthurJsonContext.Default.InboundDto))!;
        Assert.Equal("converted", item.Status);

        var task = (await winner.GetTaskAsync(item.Task!)).Task;
        Assert.Equal("Invoice export is broken", task.Title);
        Assert.Equal(2, task.Priority);
        Assert.Equal(TaskState.Backlog, task.State);
        Assert.Contains("customer@example.com", task.Body);
        Assert.Contains("https://mail.example/42", task.Body);
        Assert.Empty(await InboundAsync());
    }

    [Fact]
    public async Task Dismissing_needs_a_reason_and_the_intake_role_is_told_about_new_items()
    {
        await _hub.AddProjectAsync();
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(InboundService.IntakeRole))).EnsureSuccessStatusCode();
        var comms = await _hub.RegisterAgentAsync("comms");
        (await comms.PostAsync(Routes.RoleAction(InboundService.IntakeRole, "take"), null)).EnsureSuccessStatusCode();

        (await comms.PostAsJsonAsync(Routes.Inbound, new AddInboundRequest("discord", "m-1", "is the api down?"))).EnsureSuccessStatusCode();

        var inbox = (await comms.GetFromJsonAsync($"{Routes.Inbox}?wait=0", MuthurJsonContext.Default.InboxDto))!;
        Assert.Contains("I-1", Assert.Single(inbox.Messages).Body);

        var silent = await comms.PostAsJsonAsync(Routes.InboundAction("I-1", "dismiss"), new DismissInboundRequest(" "));
        Assert.Equal("reason_required", (await silent.ReadErrorAsync()).Code);

        (await comms.PostAsJsonAsync(Routes.InboundAction("I-1", "dismiss"), new DismissInboundRequest("answered in channel: no outage"))).EnsureSuccessStatusCode();
        Assert.Equal("answered in channel: no outage", (await InboundAsync("dismissed")).Single().Resolution);
    }

    [Fact]
    public void GitHub_issues_are_parsed_and_pull_requests_skipped()
    {
        const string json = """
            [
              { "number": 7, "title": "Bug", "body": "it breaks", "html_url": "https://github.com/o/r/issues/7", "updated_at": "2026-03-01T08:00:00Z", "user": { "login": "alice" } },
              { "number": 8, "title": "A PR", "body": null, "html_url": "https://github.com/o/r/pull/8", "updated_at": "2026-03-02T08:00:00Z", "user": { "login": "bob" }, "pull_request": {} },
              { "number": 9, "title": "No body", "body": null, "html_url": "https://github.com/o/r/issues/9", "updated_at": "2026-03-03T08:00:00Z", "user": { "login": "carol" } }
            ]
            """;

        var fetch = GitHubIssuesSource.Parse(json, "2026-02-01T00:00:00Z");

        Assert.Equal(["issue/7", "issue/9"], fetch.Items.Select(i => i.ExternalId));
        Assert.Equal("alice", fetch.Items[0].Author);
        Assert.Equal("", fetch.Items[1].Body);
        Assert.Equal("2026-03-03T08:00:00Z", fetch.Cursor);
        Assert.Equal("2026-02-01T00:00:00Z", GitHubIssuesSource.Parse("[]", "2026-02-01T00:00:00Z").Cursor);
    }

    private async Task<IReadOnlyList<IngestSourceDto>> SourcesAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync(Routes.IngestSources, MuthurJsonContext.Default.IReadOnlyListIngestSourceDto))!;
}
