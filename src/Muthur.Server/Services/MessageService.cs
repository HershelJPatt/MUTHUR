using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>The internal bus. The inbox is every agent's single wake-up channel: peers, the founder and the hub itself all write to it.</summary>
public sealed class MessageService(Ledger ledger, EventFeed feed, TimeProvider clock, IHostApplicationLifetime lifetime)
{
    public const string SentEvent = "message.sent";
    public const int MaxWaitSeconds = 900;
    public const int MaxBodyLength = 64 * 1024;
    private const int PreviewLength = 140;

    public Task<MessageDto> SendAsync(Caller caller, SendMessageRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (string.IsNullOrWhiteSpace(request.Body))
            throw Fail.Rule("body_required", "A message needs a body.");
        if (request.Body.Length > MaxBodyLength)
            throw Fail.Rule("body_too_long", $"A message body is limited to {MaxBodyLength / 1024} KB. Put large content in a file and reference it.");

        return ledger.MutateAsync(caller, async m =>
        {
            var (kind, key) = await ResolveRecipientAsync(m, request.To, ct);
            int? taskId = request.Task is { Length: > 0 } task ? (await TaskService.LoadAsync(m.Db, task, ct)).Id : null;
            var message = Post(m, caller.AgentId, caller.Name, kind, key, request.Body.Trim(), taskId, request.Blocking);
            await m.Db.SaveChangesAsync(ct);
            return ToDto(message);
        }, ct);
    }

    /// <summary>Adds a message inside an ongoing mutation. Used by services that notify agents as part of a state change.</summary>
    public static Message Post(Mutation m, Guid? fromAgentId, string fromName, Recipient kind, string? key, string body, int? taskId = null, bool blocking = false)
    {
        var message = new Message
        {
            FromAgentId = fromAgentId,
            FromName = fromName,
            ToKind = kind,
            ToKey = key,
            Body = body,
            Blocking = blocking,
            TaskId = taskId,
            CreatedAt = m.Now,
        };
        m.Db.Messages.Add(message);
        m.Record(SentEvent, taskId, new
        {
            from = fromName,
            to = Address(kind, key),
            blocking,
            preview = body.Length > PreviewLength ? body[..PreviewLength] + "…" : body,
        });
        return message;
    }

    /// <summary>From the hub itself, e.g. "T-4 is ready for validation".</summary>
    public static Message PostFromHub(Mutation m, Recipient kind, string? key, string body, int? taskId = null) =>
        Post(m, null, Caller.System.Name, kind, key, body, taskId);

    /// <summary>
    /// Unread messages for the calling agent and the roles it holds; reading marks them read.
    /// With <paramref name="waitSeconds"/> the call blocks until something arrives — an agent waits here instead of polling.
    /// </summary>
    public async Task<InboxDto> InboxAsync(Caller caller, int waitSeconds, bool peek, CancellationToken ct = default)
    {
        caller.RequireAgent();
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        while (true)
        {
            // Subscribe before looking, so a message that lands between the look and the wait still wakes us.
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = feed.Subscribe(events =>
            {
                if (events.Any(e => e.Type == SentEvent)) arrived.TrySetResult();
            });

            var messages = await TakeAsync(caller, peek, ct);
            if (messages.Count > 0) return new InboxDto(messages, TimedOut: false);

            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return new InboxDto([], TimedOut: waitSeconds > 0);

            // Every idle agent sits in this wait. It must end the moment the hub stops, or shutdown stalls
            // for the host's whole grace period with the database and binaries still locked.
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
            try
            {
                await Task.WhenAny(arrived.Task, Task.Delay(remaining, clock, stopping.Token));
            }
            catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            if (lifetime.ApplicationStopping.IsCancellationRequested) return new InboxDto([], TimedOut: true);
        }
    }

    private Task<IReadOnlyList<MessageDto>> TakeAsync(Caller caller, bool peek, CancellationToken ct)
    {
        var agentId = caller.RequireAgent();
        async Task<List<Message>> UnreadAsync(Data.MuthurDb db, DateTimeOffset now)
        {
            var roles = await db.RoleHolds.Where(h => h.AgentId == agentId && h.LeaseExpires > now).Select(h => h.RoleKey).ToListAsync(ct);
            return await db.Messages
                .Where(x => x.ReadAt == null &&
                            ((x.ToKind == Recipient.Agent && x.ToKey == caller.Name) ||
                             (x.ToKind == Recipient.Role && roles.Contains(x.ToKey!))))
                .OrderBy(x => x.Id).ToListAsync(ct);
        }

        if (peek)
            return ledger.ReadAsync<IReadOnlyList<MessageDto>>(async (db, now) => (await UnreadAsync(db, now)).Select(ToDto).ToList(), ct);

        return ledger.MutateAsync<IReadOnlyList<MessageDto>>(caller, async m =>
        {
            var unread = await UnreadAsync(m.Db, m.Now);
            foreach (var message in unread)
            {
                message.ReadAt = m.Now;
                message.ReadBy = caller.Name;
            }
            if (unread.Count > 0)
                m.Record("message.read", payload: new { by = caller.Name, ids = unread.Select(x => x.Id).ToList() });
            return unread.Select(ToDto).ToList();
        }, ct);
    }

    /// <summary>Recent traffic, newest last. <paramref name="founderThread"/> narrows it to messages to or from the founder.</summary>
    public Task<IReadOnlyList<MessageDto>> HistoryAsync(int limit, bool founderThread = false, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<MessageDto>>(async (db, _) =>
        {
            var query = db.Messages.AsQueryable();
            if (founderThread) query = query.Where(x => x.ToKind == Recipient.Founder || (x.FromAgentId == null && x.FromName == Caller.Founder.Name));
            var rows = await query.OrderByDescending(x => x.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
            return rows.OrderBy(x => x.Id).Select(ToDto).ToList();
        }, ct);

    public Task<int> UnreadForFounderAsync(CancellationToken ct = default) =>
        ledger.ReadAsync((db, _) => db.Messages.CountAsync(x => x.ToKind == Recipient.Founder && x.ReadAt == null, ct), ct);

    public Task MarkFounderReadAsync(Caller caller, CancellationToken ct = default)
    {
        if (!caller.IsFounder) throw Fail.Unauthorized("Only the founder reads the founder's messages.");
        return ledger.MutateAsync(caller, async m =>
        {
            var unread = await m.Db.Messages.Where(x => x.ToKind == Recipient.Founder && x.ReadAt == null).ToListAsync(ct);
            foreach (var message in unread)
            {
                message.ReadAt = m.Now;
                message.ReadBy = caller.Name;
            }
            if (unread.Count > 0)
                m.Record("message.read", payload: new { by = caller.Name, ids = unread.Select(x => x.Id).ToList() });
        }, ct);
    }

    private static async Task<(Recipient Kind, string? Key)> ResolveRecipientAsync(Mutation m, string? to, CancellationToken ct)
    {
        var address = (to ?? "").Trim().ToLowerInvariant();
        if (address.Length == 0) throw Fail.Rule("recipient_required", "Pass --to <agent>, --to role:<key> or --to founder.");
        if (address == "founder") return (Recipient.Founder, null);

        if (address.StartsWith("role:", StringComparison.Ordinal))
        {
            var key = address["role:".Length..];
            return await m.Db.Roles.AnyAsync(r => r.Key == key, ct) ? (Recipient.Role, key) : throw Fail.NotFound("Role", key);
        }
        if (await m.Db.Agents.AnyAsync(a => a.Name == address, ct)) return (Recipient.Agent, address);
        if (await m.Db.Roles.AnyAsync(r => r.Key == address, ct)) return (Recipient.Role, address);
        throw Fail.NotFound("Agent or role", address);
    }

    private static string Address(Recipient kind, string? key) => kind switch
    {
        Recipient.Founder => "founder",
        Recipient.Role => "role:" + key,
        _ => key ?? "",
    };

    public static MessageDto ToDto(Message x) =>
        new(x.Id, x.FromName, Address(x.ToKind, x.ToKey), x.Body, x.Blocking, x.TaskId is { } id ? Wire.TaskId(id) : null, x.CreatedAt, x.ReadAt, x.ReadBy);
}
