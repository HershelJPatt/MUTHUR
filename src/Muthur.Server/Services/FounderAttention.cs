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

    /// <summary>
    /// When the longest-waiting thing started waiting, or null when nothing is. "210 waiting" and "210
    /// waiting, the oldest since Tuesday" are different facts, and only the second says how bad it is.
    /// <para>
    /// Unread messages count towards <see cref="Total"/> but deliberately not towards this: a message blocks
    /// nobody, and letting one set the headline age would report the queue as older than it is.
    /// </para>
    /// </summary>
    public DateTimeOffset? OldestWaitingSince
    {
        get
        {
            var waiting = Requests.Select(r => r.CreatedAt).Concat(Outbound.Select(o => o.CreatedAt)).ToList();
            return waiting.Count == 0 ? null : waiting.Min();
        }
    }
}

public sealed class FounderAttention(RequestService requests, OutboundService outbound, MessageService messages)
{
    public const string AwaitingFounder = "awaiting_founder";

    public async Task<FounderAttentionDto> ReadAsync(CancellationToken ct = default) => new(
        await requests.ListAsync(openOnly: true, ct: ct),
        await outbound.ListAsync(AwaitingFounder, 500, ct),
        await messages.UnreadForFounderAsync(ct));
}
