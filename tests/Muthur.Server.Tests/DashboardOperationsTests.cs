using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>The Operations page — what arrived, what wants to leave, who staffs each tier — rendered by the real dashboard.</summary>
public sealed class DashboardOperationsTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task What_arrived_is_shown_with_its_author_and_counted_as_unclaimed()
    {
        var collector = await _hub.RegisterAgentAsync("collector");
        (await collector.PostAsJsonAsync(Routes.Inbound,
            new AddInboundRequest("email", "m-1", "Export is <b>broken</b>", Author: "customer@example.com"))).EnsureSuccessStatusCode();

        var page = await _hub.CreateClient().GetStringAsync("/operations");

        Assert.Contains("I-1", page);
        Assert.Contains("Export is &lt;b&gt;broken&lt;/b&gt;", page);
        Assert.Contains("customer@example.com", page);
        Assert.Contains("1 unclaimed", page);
    }

    [Fact]
    public async Task A_message_waiting_for_the_founder_is_offered_for_approval_and_counted_in_the_tab_badge()
    {
        var address = Path.Combine(_hub.DataDir, "press-outbox.txt");
        (await _hub.Founder().PutAsJsonAsync(Routes.OutboundTargets,
            new DefineTargetRequest("press", "file", address, RequiresFounderApproval: true))).EnsureSuccessStatusCode();
        var author = await _hub.RegisterAgentAsync("author");
        var peer = await _hub.RegisterAgentAsync("peer");

        var drafted = await author.PostAsJsonAsync(Routes.Outbound, new DraftOutboundRequest("press", "We are shutting down the free tier on March 1."));
        drafted.EnsureSuccessStatusCode();
        var draft = (await drafted.Content.ReadFromJsonAsync(MuthurJsonContext.Default.OutboundDto))!;
        (await peer.PostAsJsonAsync(Routes.OutboundAction(draft.Id, "review"), new ReviewOutboundRequest(true, draft.Sha256))).EnsureSuccessStatusCode();

        var page = await _hub.CreateClient().GetStringAsync("/operations");
        Assert.Contains("O-1", page);
        Assert.Contains("awaiting founder", page);
        Assert.Contains("press", page);
        Assert.Contains(">Approve<", page);
        Assert.Contains(">Decline<", page);
        Assert.Contains("1 need you", page);
        Assert.Contains(draft.Sha256[..12], page);
        Assert.DoesNotContain(address, page);           // the target's address is never shown
        Assert.DoesNotContain("press-outbox.txt", page);
        Assert.Contains("class=\"badge\">1<", await _hub.CreateClient().GetStringAsync("/"));

        (await _hub.Founder().PostAsync(Routes.OutboundAction(draft.Id, "approve"), null)).EnsureSuccessStatusCode();

        Assert.DoesNotContain(">Approve<", await _hub.CreateClient().GetStringAsync("/operations"));
        Assert.DoesNotContain("class=\"badge\"", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task Each_tier_shows_its_candidates_and_the_ones_whose_account_is_out_of_quota()
    {
        var page = await _hub.CreateClient().GetStringAsync("/operations");
        Assert.Contains("implementer", page);
        Assert.Contains("mastermind", page);
        Assert.Contains("utility", page);
        Assert.Contains("claude/", page);

        var tiers = (await _hub.CreateClient().GetFromJsonAsync(Routes.Tiers, MuthurJsonContext.Default.IReadOnlyListTierDto))!;
        var first = tiers.Single(t => t.Tier == "implementer").Candidates[0];
        var registered = await _hub.CreateClient().PostAsJsonAsync(Routes.AgentRegister,
            new RegisterAgentRequest("worker", first.Harness, first.Model, "implementer", first.Account));
        registered.EnsureSuccessStatusCode();
        var worker = _hub.CreateClient((await registered.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse))!.Token);
        (await worker.PostAsJsonAsync(Routes.AgentLimited, new LimitedRequest(_hub.Clock.GetUtcNow().AddHours(2)))).EnsureSuccessStatusCode();

        Assert.Contains("limited in", await _hub.CreateClient().GetStringAsync("/operations"));
    }

    [Fact]
    public async Task A_flagged_message_tells_the_founder_why_it_needs_them()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.OutboundTargets, new DefineTargetRequest("news", "file", Path.Combine(_hub.DataDir, "o.txt")))).EnsureSuccessStatusCode();
        var author = await _hub.RegisterAgentAsync("author");
        var peer = await _hub.RegisterAgentAsync("peer");
        var drafted = await author.PostAsJsonAsync(Routes.Outbound, new DraftOutboundRequest("news", "docker login registry.example.com -u deploy -p Sup3rS3cretValue9"));
        var draft = (await drafted.Content.ReadFromJsonAsync(MuthurJsonContext.Default.OutboundDto))!;
        (await peer.PostAsJsonAsync(Routes.OutboundAction(draft.Id, "review"), new ReviewOutboundRequest(true, draft.Sha256))).EnsureSuccessStatusCode();

        var page = await _hub.CreateClient().GetStringAsync("/operations");

        Assert.Contains(">flagged<", page);
        Assert.Contains("possible-credential-near-password-word", page);
        Assert.Contains(">Approve<", page);
        Assert.Contains("1 need you", page);
    }
}
