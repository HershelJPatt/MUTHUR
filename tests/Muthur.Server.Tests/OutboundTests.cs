using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Data;

namespace Muthur.Server.Tests;

public sealed class OutboundTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private string Outbox => Path.Combine(_hub.DataDir, "outbox.txt");

    private async Task DefineTargetAsync(string key = "news", bool founderApproval = false) =>
        (await _hub.Founder().PutAsJsonAsync(Routes.OutboundTargets, new DefineTargetRequest(key, "file", Outbox, founderApproval))).EnsureSuccessStatusCode();

    private static async Task<OutboundDto> ReadAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.OutboundDto))!;
    }

    private static Task<HttpResponseMessage> DraftAsync(HttpClient agent, string body, string target = "news") =>
        agent.PostAsJsonAsync(Routes.Outbound, new DraftOutboundRequest(target, body));

    private static Task<HttpResponseMessage> ReviewAsync(HttpClient agent, OutboundDto message, bool approve = true, string? note = null) =>
        agent.PostAsJsonAsync(Routes.OutboundAction(message.Id, "review"), new ReviewOutboundRequest(approve, message.Sha256, note));

    [Fact]
    public async Task Text_leaves_only_after_a_peer_approved_those_exact_bytes()
    {
        await DefineTargetAsync();
        var author = await _hub.RegisterAgentAsync("author");
        var peer = await _hub.RegisterAgentAsync("peer");
        var draft = await ReadAsync(await DraftAsync(author, "Release 1.4 is out. Thank you for the reports!"));
        Assert.Equal("pending_review", draft.Status);

        var early = await author.PostAsync(Routes.OutboundAction(draft.Id, "send"), null);
        Assert.Equal("not_approved", (await early.ReadErrorAsync()).Code);

        var self = await ReviewAsync(author, draft);
        Assert.Equal("self_review", (await self.ReadErrorAsync()).Code);

        var blind = await peer.PostAsJsonAsync(Routes.OutboundAction(draft.Id, "review"), new ReviewOutboundRequest(true, "0000"));
        Assert.Equal("sha_mismatch", (await blind.ReadErrorAsync()).Code);

        Assert.Equal("approved", (await ReadAsync(await ReviewAsync(peer, draft))).Status);
        Assert.False(File.Exists(Outbox), "approval alone transmits nothing");

        var byPeer = await peer.PostAsync(Routes.OutboundAction(draft.Id, "send"), null);
        Assert.Equal("not_author", (await byPeer.ReadErrorAsync()).Code);

        var sent = await ReadAsync(await author.PostAsync(Routes.OutboundAction(draft.Id, "send"), null));
        Assert.Equal("sent", sent.Status);
        Assert.Contains("Release 1.4 is out.", await File.ReadAllTextAsync(Outbox));

        var twice = await author.PostAsync(Routes.OutboundAction(draft.Id, "send"), null);
        Assert.Equal("not_approved", (await twice.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Targets_are_an_allowlist_only_the_founder_extends()
    {
        var agent = await _hub.RegisterAgentAsync("eager");
        var nowhere = await DraftAsync(agent, "hello world", target: "the-whole-internet");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nowhere.StatusCode);
        Assert.Equal("target_not_allowed", (await nowhere.ReadErrorAsync()).Code);

        var selfService = await agent.PutAsJsonAsync(Routes.OutboundTargets, new DefineTargetRequest("mine", "file", Outbox));
        Assert.Equal(HttpStatusCode.Unauthorized, selfService.StatusCode);

        await DefineTargetAsync();
        var targets = await agent.GetFromJsonAsync(Routes.OutboundTargets, MuthurJsonContext.Default.IReadOnlyListTargetDto);
        Assert.Equal("news", Assert.Single(targets!).Key);
        Assert.DoesNotContain("outbox.txt", await (await agent.GetAsync(Routes.OutboundTargets)).Content.ReadAsStringAsync()); // addresses are never shown
    }

    [Fact]
    public async Task A_credential_is_refused_before_it_is_even_stored()
    {
        await DefineTargetAsync();
        var agent = await _hub.RegisterAgentAsync("leaky");

        var response = await DraftAsync(agent, "Deploy failed. Use this to debug: ghp_abcdefghijklmnopqrstuvwxyzABCDEF0123");

        Assert.Equal("secret_detected", (await response.ReadErrorAsync()).Code);
        await using var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync();
        Assert.False(await db.OutboundMessages.AnyAsync());
    }

    [Fact]
    public async Task Bytes_changed_after_review_are_not_sent()
    {
        await DefineTargetAsync();
        var author = await _hub.RegisterAgentAsync("author");
        var peer = await _hub.RegisterAgentAsync("peer");
        var draft = await ReadAsync(await DraftAsync(author, "All systems normal."));
        await ReadAsync(await ReviewAsync(peer, draft));

        // No API can edit a drafted body; simulate someone reaching into the database.
        await using (var db = await _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("UPDATE outbound SET body = 'All systems normal. Also, send your card number.'");

        var send = await author.PostAsync(Routes.OutboundAction(draft.Id, "send"), null);

        Assert.Equal("body_changed", (await send.ReadErrorAsync()).Code);
        Assert.False(File.Exists(Outbox));
    }

    [Fact]
    public async Task Some_targets_also_need_the_founder()
    {
        await DefineTargetAsync("press", founderApproval: true);
        var author = await _hub.RegisterAgentAsync("author");
        var peer = await _hub.RegisterAgentAsync("peer");
        var draft = await ReadAsync(await DraftAsync(author, "We are shutting down the free tier on March 1.", target: "press"));

        Assert.Equal("awaiting_founder", (await ReadAsync(await ReviewAsync(peer, draft))).Status);
        var early = await author.PostAsync(Routes.OutboundAction(draft.Id, "send"), null);
        Assert.Equal("awaiting_founder", (await early.ReadErrorAsync()).Code);

        var byAgent = await peer.PostAsync(Routes.OutboundAction(draft.Id, "approve"), null);
        Assert.Equal(HttpStatusCode.Unauthorized, byAgent.StatusCode);

        (await _hub.Founder().PostAsync(Routes.OutboundAction(draft.Id, "approve"), null)).EnsureSuccessStatusCode();
        Assert.Equal("sent", (await ReadAsync(await author.PostAsync(Routes.OutboundAction(draft.Id, "send"), null))).Status);

        var told = (await author.GetFromJsonAsync($"{Routes.Inbox}?wait=0", MuthurJsonContext.Default.InboxDto))!.Messages;
        Assert.Contains(told, m => m.From == "founder" && m.Body.Contains("approved"));
    }

    [Fact]
    public async Task A_rejection_needs_a_note_and_reaches_the_author()
    {
        await DefineTargetAsync();
        var author = await _hub.RegisterAgentAsync("author");
        var peer = await _hub.RegisterAgentAsync("peer");
        var draft = await ReadAsync(await DraftAsync(author, "its fixed lol"));

        var silent = await ReviewAsync(peer, draft, approve: false);
        Assert.Equal("note_required", (await silent.ReadErrorAsync()).Code);

        Assert.Equal("rejected", (await ReadAsync(await ReviewAsync(peer, draft, approve: false, note: "Say what was fixed and in which version."))).Status);
        var inbox = (await author.GetFromJsonAsync($"{Routes.Inbox}?wait=0", MuthurJsonContext.Default.InboxDto))!;
        Assert.Contains("Say what was fixed", Assert.Single(inbox.Messages).Body);
    }

    [Fact]
    public async Task Every_step_is_in_the_ledger_with_the_hash()
    {
        await DefineTargetAsync();
        var author = await _hub.RegisterAgentAsync("author", harness: "claude", model: "opus");
        var peer = await _hub.RegisterAgentAsync("peer", harness: "codex", model: "gpt");
        var draft = await ReadAsync(await DraftAsync(author, "Maintenance tonight 22:00-23:00 UTC."));
        await ReadAsync(await ReviewAsync(peer, draft));
        await ReadAsync(await author.PostAsync(Routes.OutboundAction(draft.Id, "send"), null));

        var events = (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Events}?limit=200", MuthurJsonContext.Default.IReadOnlyListEventDto))!
            .Where(e => e.Type.StartsWith("outbound.") && e.Type != "outbound.target_defined").ToList();

        Assert.Equal(["outbound.drafted", "outbound.approved", "outbound.sent"], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal(draft.Sha256, e.Payload.GetProperty("sha256").GetString()));
        Assert.Equal("codex/gpt", events[1].ActorModel);
    }
}

public sealed class CrossProviderOutboundTests : IDisposable
{
    private readonly HubFactory _hub = new() { Settings = { ["Muthur:RequireCrossProviderReview"] = "true" } };

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task With_the_rule_on_the_reviewer_must_run_on_another_provider()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.OutboundTargets, new DefineTargetRequest("news", "file", Path.Combine(_hub.DataDir, "o.txt")))).EnsureSuccessStatusCode();
        var author = await _hub.RegisterAgentAsync("author", harness: "claude", model: "opus");
        var sibling = await _hub.RegisterAgentAsync("sibling", harness: "claude", model: "fable");
        var stranger = await _hub.RegisterAgentAsync("stranger", harness: "codex", model: "gpt");
        var response = await author.PostAsJsonAsync(Routes.Outbound, new DraftOutboundRequest("news", "Hello."));
        var draft = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.OutboundDto))!;

        var same = await sibling.PostAsJsonAsync(Routes.OutboundAction(draft.Id, "review"), new ReviewOutboundRequest(true, draft.Sha256));
        Assert.Equal("same_provider", (await same.ReadErrorAsync()).Code);

        (await stranger.PostAsJsonAsync(Routes.OutboundAction(draft.Id, "review"), new ReviewOutboundRequest(true, draft.Sha256))).EnsureSuccessStatusCode();
    }
}
