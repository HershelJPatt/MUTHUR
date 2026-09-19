using Muthur.Contracts;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>Everything waiting on the founder, read once so the badge and the page cannot disagree.</summary>
/// <param name="Requests">The open questions worth showing, best-ranked first. Capped; the counts below are not.</param>
/// <param name="Unread">Messages addressed to the founder that they have not seen.</param>
/// <param name="RequestsWaiting">How many questions are open in all, which is what the founder is told.</param>
/// <param name="OutboundWaiting">How many messages are awaiting the founder in all.</param>
/// <param name="OldestWaitingSince">
/// When the longest-waiting thing started waiting, or null when nothing is. "210 waiting" and "210 waiting,
/// the oldest since Tuesday" are different facts, and only the second says how bad it is. Read across
/// everything that is waiting rather than across the shown page, for the same reason the counts are.
/// <para>
/// Unread messages count towards <see cref="Total"/> but deliberately not towards this: a message blocks
/// nobody, and letting one set the headline age would report the queue as older than it is.
/// </para>
/// </param>
public sealed record FounderAttentionDto(
    IReadOnlyList<FounderRequestDto> Requests,
    IReadOnlyList<OutboundDto> Outbound,
    int Unread,
    int RequestsWaiting,
    int OutboundWaiting,
    DateTimeOffset? OldestWaitingSince)
{
    /// <summary>
    /// Counted from what is waiting, never from the lists above: a badge reading 200 while 210 wait is worse
    /// than no badge at all, because it is believable.
    /// </summary>
    public int Total => RequestsWaiting + OutboundWaiting + Unread;
}

public sealed class FounderAttention(RequestService requests, OutboundService outbound, MessageService messages)
{
    public const string AwaitingFounder = "awaiting_founder";

    public async Task<FounderAttentionDto> ReadAsync(CancellationToken ct = default)
    {
        var openRequests = await requests.ListAsync(openOnly: true, ct: ct);
        var awaiting = await outbound.ListAsync(AwaitingFounder, 500, ct);
        var requestSummary = await requests.OpenSummaryAsync(ct);
        var outboundSummary = await outbound.SummaryAsync(OutboundStatus.AwaitingFounder, ct);

        DateTimeOffset? oldest = (requestSummary.Oldest, outboundSummary.Oldest) switch
        {
            ({ } r, { } o) => r < o ? r : o,
            ({ } r, null) => r,
            (null, { } o) => o,
            _ => null,
        };

        return new FounderAttentionDto(
            openRequests, awaiting, await messages.UnreadForFounderAsync(ct),
            requestSummary.Count, outboundSummary.Count, oldest);
    }
}
