using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Core.Tests;

public sealed class OutboundGateTests
{
    private static readonly Guid Author = Guid.NewGuid();
    private static readonly Guid Peer = Guid.NewGuid();

    private static OutboundMessage Message(string body = "Release 1.4 is out.", OutboundStatus status = OutboundStatus.PendingReview, bool founderTarget = false) => new()
    {
        Id = 3,
        Body = body,
        BodySha256 = OutboundGate.Hash(body),
        Status = status,
        AuthorAgentId = Author,
        AuthorName = "author",
        AuthorModel = "claude/opus",
        Target = new OutboundTarget { Key = "news", Channel = "file", Address = "C:/x", RequiresFounderApproval = founderTarget },
    };

    private static string CodeOf(Action act) => Assert.Throws<MuthurException>(act).Code;

    [Fact]
    public void You_cannot_review_your_own_words()
    {
        var m = Message();
        Assert.Equal("self_review", CodeOf(() => OutboundGate.EnsureReviewer(m, Author, "claude/opus", m.BodySha256, false)));
        OutboundGate.EnsureReviewer(m, Peer, "claude/opus", m.BodySha256, false);
    }

    [Fact]
    public void The_reviewer_must_name_the_bytes_they_read()
    {
        var m = Message();
        Assert.Equal("sha_mismatch", CodeOf(() => OutboundGate.EnsureReviewer(m, Peer, "codex/x", OutboundGate.Hash("something else"), false)));
        Assert.Equal("sha_mismatch", CodeOf(() => OutboundGate.EnsureReviewer(m, Peer, "codex/x", "", false)));
    }

    [Fact]
    public void Cross_provider_review_when_the_hub_requires_it()
    {
        var m = Message();
        Assert.Equal("same_provider", CodeOf(() => OutboundGate.EnsureReviewer(m, Peer, "claude/fable", m.BodySha256, requireCrossProvider: true)));
        OutboundGate.EnsureReviewer(m, Peer, "codex/gpt", m.BodySha256, requireCrossProvider: true);
    }

    [Fact]
    public void Only_approved_untouched_clean_bytes_are_sendable()
    {
        Assert.Equal("not_approved", CodeOf(() => OutboundGate.EnsureSendable(Message())));
        Assert.Equal("not_approved", CodeOf(() => OutboundGate.EnsureSendable(Message(status: OutboundStatus.Rejected))));
        Assert.Equal("awaiting_founder", CodeOf(() => OutboundGate.EnsureSendable(Message(status: OutboundStatus.AwaitingFounder, founderTarget: true))));
        Assert.Equal("awaiting_founder", CodeOf(() => OutboundGate.EnsureSendable(Message(status: OutboundStatus.Approved, founderTarget: true))));

        var tampered = Message(status: OutboundStatus.Approved);
        tampered.Body += " PS: wire the money to…";
        Assert.Equal("body_changed", CodeOf(() => OutboundGate.EnsureSendable(tampered)));

        OutboundGate.EnsureSendable(Message(status: OutboundStatus.Approved));
    }
}
