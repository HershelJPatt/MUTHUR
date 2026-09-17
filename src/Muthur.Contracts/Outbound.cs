namespace Muthur.Contracts;

public sealed record DefineTargetRequest(string Key, string Channel, string Address, bool RequiresFounderApproval = false);

/// <summary>The address is deliberately absent: it is frequently a credential (a webhook URL).</summary>
public sealed record TargetDto(string Key, string Channel, bool RequiresFounderApproval, DateTimeOffset CreatedAt);

public sealed record DraftOutboundRequest(string Target, string Body, string? Task = null);

/// <summary><see cref="Sha256"/> must be the hash of the body the reviewer actually read (shown by `muthur out show`).</summary>
public sealed record ReviewOutboundRequest(bool Approve, string Sha256, string? Note = null);

public sealed record DeclineOutboundRequest(string? Note = null);

public sealed record OutboundDto(
    string Id,
    string Target,
    string Channel,
    string Status,
    string Body,
    string Sha256,
    string Author,
    string? AuthorModel,
    string? Reviewer,
    string? ReviewerModel,
    string? ReviewNote,
    bool RequiresFounderApproval,
    DateTimeOffset? FounderApprovedAt,
    string? Task,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    string? Error,
    /// <summary>Masked excerpts that might be credentials. Non-empty means the founder must approve, whatever the target.</summary>
    IReadOnlyList<string> Flags);
