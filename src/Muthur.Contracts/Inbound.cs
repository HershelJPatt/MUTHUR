namespace Muthur.Contracts;

/// <summary>Push an item into the hub from any tool (an email script, a chat bridge). Re-sending the same (Source, ExternalId) is a no-op.</summary>
public sealed record AddInboundRequest(
    string Source,
    string ExternalId,
    string Title,
    string? Body = null,
    string? Url = null,
    string? Author = null,
    string? Project = null);

public sealed record ConvertInboundRequest(int Priority = 0, string? Title = null);

public sealed record DismissInboundRequest(string Reason);

public sealed record InboundDto(
    string Id,
    string? Project,
    string Source,
    string ExternalId,
    string Title,
    string Body,
    string? Url,
    string? Author,
    string Status,
    string? ClaimedBy,
    string? Task,
    string? Resolution,
    DateTimeOffset ReceivedAt);

public sealed record IngestSourceDto(string Source, string Project, string? Cursor, DateTimeOffset? LastPolled, string? LastError);

public sealed record PollResultDto(int Sources, int NewItems, IReadOnlyList<string> Errors);
