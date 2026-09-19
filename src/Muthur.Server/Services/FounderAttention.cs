using Muthur.Contracts;

namespace Muthur.Server.Services;

/// <summary>Everything waiting on the founder, read once so the badge and the page cannot disagree.</summary>
/// <param name="Unread">Messages addressed to the founder that they have not seen.</param>
public sealed record FounderAttentionDto(
    IReadOnlyList<FounderRequestDto> Requests,
    IReadOnlyList<OutboundDto> Outbound,
    int Unread)
{
    public int Total => Requests.Count + Outbound.Count + Unread;
}

public sealed class FounderAttention(RequestService requests, OutboundService outbound, MessageService messages)
{
    public const string AwaitingFounder = "awaiting_founder";

    public async Task<FounderAttentionDto> ReadAsync(CancellationToken ct = default) => new(
        await requests.ListAsync(openOnly: true, ct: ct),
        await outbound.ListAsync(AwaitingFounder, 500, ct),
        await messages.UnreadForFounderAsync(ct));
}
