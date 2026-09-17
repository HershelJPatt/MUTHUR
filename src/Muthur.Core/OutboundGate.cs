using System.Security.Cryptography;
using System.Text;
using Muthur.Core.Entities;

namespace Muthur.Core;

/// <summary>
/// The rules between an agent and the outside world. Nothing leaves unless: the target is on the allowlist,
/// no credential is in the text, a different agent approved these exact bytes, and — where the target demands it —
/// the founder did too.
/// </summary>
public static class OutboundGate
{
    public static string Hash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public static void EnsureClean(string body)
    {
        var findings = SecretScanner.Scan(body).Where(f => f.Severity == SecretSeverity.Block).ToList();
        if (findings.Count == 0) return;
        var list = string.Join("; ", findings.Take(5).Select(f => $"{f.Kind}: {f.Excerpt}"));
        throw Fail.Rule("secret_detected", $"The text contains what looks like {findings.Count} credential(s) and cannot leave the machine: {list}. Remove them and draft again.");
    }

    /// <summary>Things that might be credentials. They do not stop a draft, but only the founder can let such a message leave.</summary>
    public static List<string> Flags(string body) =>
        SecretScanner.Scan(body).Where(f => f.Severity == SecretSeverity.Suspect).Select(f => $"{f.Kind}: {f.Excerpt}").Distinct().Take(20).ToList();

    public static bool NeedsFounder(OutboundMessage message) => message.Target is { RequiresFounderApproval: true } || message.Flags.Count > 0;

    public static void EnsureReviewer(OutboundMessage message, Guid? reviewerAgentId, string? reviewerModel, string claimedSha, bool requireCrossProvider)
    {
        var id = "O-" + message.Id;
        if (message.Status != OutboundStatus.PendingReview)
            throw Fail.Rule("not_pending_review", $"{id} is {message.Status}; only a message pending review can be reviewed.");
        if (reviewerAgentId is null)
            throw Fail.Unauthorized("Peer review is done by a registered agent. (The founder approves with: muthur out approve)");
        if (reviewerAgentId == message.AuthorAgentId)
            throw Fail.Rule("self_review", $"You wrote {id}. The exact bytes leaving the machine are reviewed by a different agent.");
        if (!string.Equals(claimedSha?.Trim(), message.BodySha256, StringComparison.OrdinalIgnoreCase))
            throw Fail.Rule("sha_mismatch", $"The hash you reviewed is not the hash of {id}. Read it again (muthur out show {id}) and pass its sha256.");
        if (requireCrossProvider && Provider(reviewerModel) is { } reviewer && reviewer == Provider(message.AuthorModel))
            throw Fail.Rule("same_provider", $"{id} was written on '{reviewer}'. This hub requires review by an agent on a different provider.");
    }

    public static void EnsureSendable(OutboundMessage message)
    {
        var id = "O-" + message.Id;
        if (message.Status == OutboundStatus.AwaitingFounder)
            throw Fail.Rule("awaiting_founder", $"{id} needs the founder's approval (its target demands it, or the text was flagged), and does not have it yet.");
        if (message.Status != OutboundStatus.Approved)
            throw Fail.Rule("not_approved", $"{id} is {message.Status}. Only an approved message can be sent.");
        if (NeedsFounder(message) && message.FounderApprovedAt is null)
            throw Fail.Rule("awaiting_founder", $"{id} needs the founder's approval (its target demands it, or the text was flagged), and does not have it yet.");
        if (Hash(message.Body) != message.BodySha256)
            throw Fail.Rule("body_changed", $"The body of {id} no longer matches the hash that was reviewed. It will not be sent.");
        EnsureClean(message.Body);
    }

    /// <summary>"claude/opus" → "claude".</summary>
    private static string? Provider(string? modelLabel) =>
        string.IsNullOrWhiteSpace(modelLabel) ? null : modelLabel.Split('/')[0].Trim().ToLowerInvariant();
}
