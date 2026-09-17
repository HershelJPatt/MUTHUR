using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Core.Tests;

public sealed class SecretScannerTests
{
    [Theory]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE here", "aws-access-key")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyzABCDEF0123 ok", "github-token")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz0123456789", "github-token")]
    [InlineData("key: sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789", "api-key")]
    [InlineData("OPENAI sk-proj-abcdefghijklmnopqrstuvwxyz012345", "api-key")]
    [InlineData("xoxb-" + "1234567890-abcdefghijklmnop", "slack-token")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk", "private-key")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "jwt")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345", "bearer-token")]
    [InlineData("Server=db;User Id=sa;Password=Sup3rS3cret!;", "connection-string-secret")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abcd1234abcd1234abcd==", "connection-string-secret")]
    [InlineData("https://x.blob.core.windows.net/c/f?sv=2022&sig=abcdefghijklmnopqrstuvwxyz%2B0123", "sas-signature")]
    [InlineData("api_key = \"zzzzzzzzzzzzzzzz\"", "credential-assignment")]
    [InlineData("the password: hunter2hunter2", "credential-assignment")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz", "webhook-url")]
    public void Credentials_are_found(string text, string expectedKind) =>
        Assert.Contains(SecretScanner.Scan(text), f => f.Kind == expectedKind);

    [Theory]
    [InlineData("Release 1.4 is out. Thanks to everyone who reported the login bug!")]
    [InlineData("We rotated the password last week; no action needed on your side.")]
    [InlineData("See https://github.com/acme/widgets/issues/42 and commit 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b.")]
    [InlineData("The token bucket algorithm limits requests; the secret is good caching.")]
    public void Ordinary_text_passes(string text) => Assert.Empty(SecretScanner.Scan(text));

    [Fact]
    public void Findings_never_repeat_the_secret()
    {
        const string secret = "ghp_abcdefghijklmnopqrstuvwxyzABCDEF0123";
        var finding = Assert.Single(SecretScanner.Scan($"use {secret} please"));
        Assert.DoesNotContain(secret, finding.Excerpt);
        Assert.StartsWith("ghp_ab", finding.Excerpt);
    }
}

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
