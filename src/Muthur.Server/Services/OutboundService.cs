using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>A way out of the machine. Credentials for it live in the target's address, which only this process reads.</summary>
public interface IOutboundChannel
{
    string Name { get; }

    /// <summary>Channel-specific limits (length, format), checked when drafting so problems surface before review.</summary>
    void Validate(string address, string body);

    /// <summary>Throws <see cref="ChannelException"/> with a reason that is safe to show to agents when delivery fails.</summary>
    Task SendAsync(string address, string body, CancellationToken ct = default);
}

/// <summary>A delivery failure whose message contains nothing about the target's address (which is often a credential).</summary>
public sealed class ChannelException(string safeReason) : Exception(safeReason);

public sealed partial class OutboundService(Ledger ledger, IEnumerable<IOutboundChannel> channels, MuthurOptions options, ILogger<OutboundService> logger)
{
    /// <summary>Agents who volunteered to review outbound traffic are told when something is waiting, if the role exists.</summary>
    public const string ReviewerRole = "outbound-reviewer";

    private readonly SemaphoreSlim _sending = new(1, 1);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex KeyPattern();

    // ---- the allowlist -------------------------------------------------------------------------------------------

    public Task<TargetDto> DefineTargetAsync(Caller caller, DefineTargetRequest request, CancellationToken ct = default)
    {
        if (!caller.IsFounder)
            throw Fail.Unauthorized("Outbound targets are the organization's allowlist; only the founder defines them (pass --founder).");
        var key = (request.Key ?? "").Trim().ToLowerInvariant();
        if (!KeyPattern().IsMatch(key)) throw Fail.Rule("invalid_key", "Target keys are 1-48 chars of a-z, 0-9 or '-'.");
        var channel = FindChannel(request.Channel);
        if (string.IsNullOrWhiteSpace(request.Address)) throw Fail.Rule("address_required", "A target needs an address.");
        channel.Validate(request.Address.Trim(), "address check"); // a malformed address should fail now, not when an agent first drafts to it

        return ledger.MutateAsync(caller, async m =>
        {
            var target = await m.Db.OutboundTargets.SingleOrDefaultAsync(t => t.Key == key, ct);
            var created = target is null;
            if (target is null)
            {
                target = new OutboundTarget { Id = Guid.NewGuid(), Key = key, Channel = channel.Name, Address = "", CreatedAt = m.Now };
                m.Db.OutboundTargets.Add(target);
            }
            target.Channel = channel.Name;
            target.Address = request.Address.Trim();
            target.RequiresFounderApproval = request.RequiresFounderApproval;
            m.Record(created ? "outbound.target_defined" : "outbound.target_updated", payload: new { target = key, target.Channel, target.RequiresFounderApproval });
            return ToDto(target);
        }, ct);
    }

    public Task<IReadOnlyList<TargetDto>> TargetsAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<TargetDto>>(async (db, _) =>
            (await db.OutboundTargets.OrderBy(t => t.Key).ToListAsync(ct)).Select(ToDto).ToList(), ct);

    // ---- draft → review → (founder) → send -----------------------------------------------------------------------

    public Task<OutboundDto> DraftAsync(Caller caller, DraftOutboundRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (string.IsNullOrWhiteSpace(request.Body)) throw Fail.Rule("body_required", "There is nothing to send.");
        OutboundGate.EnsureClean(request.Body); // refused before it is ever stored: a secret must not end up in the database either

        return ledger.MutateAsync(caller, async m =>
        {
            var key = (request.Target ?? "").Trim().ToLowerInvariant();
            var target = await m.Db.OutboundTargets.SingleOrDefaultAsync(t => t.Key == key, ct)
                ?? throw Fail.Rule("target_not_allowed", $"'{key}' is not an allowed outbound target. Allowed: muthur out targets. New targets are added by the founder.");
            FindChannel(target.Channel).Validate(target.Address, request.Body);
            int? taskId = request.Task is { Length: > 0 } task ? (await TaskService.LoadAsync(m.Db, task, ct)).Id : null;

            var message = new OutboundMessage
            {
                TargetId = target.Id,
                Target = target,
                TaskId = taskId,
                Body = request.Body,
                BodySha256 = OutboundGate.Hash(request.Body),
                Status = OutboundStatus.PendingReview,
                AuthorAgentId = caller.AgentId,
                AuthorName = caller.Name,
                AuthorModel = caller.Model,
                CreatedAt = m.Now,
            };
            m.Db.OutboundMessages.Add(message);
            await m.Db.SaveChangesAsync(ct);
            m.Record("outbound.drafted", taskId, new { outbound = Id(message), target = key, author = caller.Name, sha256 = message.BodySha256, chars = message.Body.Length });

            if (await m.Db.Roles.AnyAsync(r => r.Key == ReviewerRole, ct))
                MessageService.PostFromHub(m, Recipient.Role, ReviewerRole, $"{Id(message)} by {caller.Name} to '{key}' is waiting for review: muthur out show {Id(message)}", taskId);
            return ToDto(message);
        }, ct);
    }

    public Task<OutboundDto> ReviewAsync(Caller caller, string id, ReviewOutboundRequest request, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var message = await LoadAsync(m.Db, id, ct);
            OutboundGate.EnsureReviewer(message, caller.AgentId, caller.Model, request.Sha256, options.RequireCrossProviderReview);
            if (!request.Approve && string.IsNullOrWhiteSpace(request.Note))
                throw Fail.Rule("note_required", "A rejection needs a note saying what to change.");

            message.ReviewerAgentId = caller.AgentId;
            message.ReviewerName = caller.Name;
            message.ReviewerModel = caller.Model;
            message.ReviewNote = request.Note?.Trim();
            message.ReviewedAt = m.Now;
            message.Status = !request.Approve ? OutboundStatus.Rejected
                : message.Target!.RequiresFounderApproval ? OutboundStatus.AwaitingFounder
                : OutboundStatus.Approved;
            m.Record(request.Approve ? "outbound.approved" : "outbound.rejected", message.TaskId,
                new { outbound = Id(message), reviewer = caller.Name, sha256 = message.BodySha256, note = message.ReviewNote });

            if (message.AuthorAgentId is not null)
                MessageService.PostFromHub(m, Recipient.Agent, message.AuthorName, message.Status switch
                {
                    OutboundStatus.Rejected => $"{Id(message)} was rejected by {caller.Name}: {message.ReviewNote}",
                    OutboundStatus.AwaitingFounder => $"{Id(message)} passed review and now waits for the founder's approval.",
                    _ => $"{Id(message)} passed review. Send it: muthur out send {Id(message)}",
                }, message.TaskId);
            return ToDto(message);
        }, ct);

    public Task<OutboundDto> FounderApproveAsync(Caller caller, string id, bool approve, string? note, CancellationToken ct = default)
    {
        if (!caller.IsFounder) throw Fail.Unauthorized("Only the founder gives founder approval (pass --founder).");
        return ledger.MutateAsync(caller, async m =>
        {
            var message = await LoadAsync(m.Db, id, ct);
            if (message.Status != OutboundStatus.AwaitingFounder)
                throw Fail.Rule("not_awaiting_founder", $"{Id(message)} is {message.Status}; there is nothing for the founder to decide.");
            message.Status = approve ? OutboundStatus.Approved : OutboundStatus.Rejected;
            message.FounderApprovedAt = approve ? m.Now : null;
            if (!approve) message.ReviewNote = $"Founder: {note ?? "declined"}";
            m.Record(approve ? "outbound.founder_approved" : "outbound.founder_declined", message.TaskId, new { outbound = Id(message), sha256 = message.BodySha256, note });

            if (message.AuthorAgentId is not null)
                MessageService.Post(m, null, caller.Name, Recipient.Agent, message.AuthorName,
                    approve ? $"{Id(message)} is approved. Send it: muthur out send {Id(message)}" : $"{Id(message)} was declined: {note ?? "no reason given"}", message.TaskId);
            return ToDto(message);
        }, ct);
    }

    /// <summary>The only code path that transmits. Everything is re-checked here, whatever earlier steps concluded.</summary>
    public async Task<OutboundDto> SendAsync(Caller caller, string id, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        await _sending.WaitAsync(ct);
        try
        {
            var message = await ledger.ReadAsync((db, _) => LoadAsync(db, id, ct), ct);
            if (!caller.IsFounder && caller.AgentId != message.AuthorAgentId)
                throw Fail.Rule("not_author", $"{Id(message)} was written by '{message.AuthorName}'; only they or the founder send it.");
            OutboundGate.EnsureSendable(message);

            string? error = null;
            try
            {
                await FindChannel(message.Target!.Channel).SendAsync(message.Target.Address, message.Body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Whatever went wrong out there, the message is "failed", never stuck in "approved". Agents get a fixed
                // reason by kind of failure; an OS or HTTP error message can name the address or part of it, so the
                // detail goes to the hub log only.
                error = SafeReason(ex);
                logger.LogWarning(ex, "Delivery of {Outbound} to target {Target} failed.", Id(message), message.Target!.Key);
            }

            var dto = await ledger.MutateAsync(caller, async m =>
            {
                var current = await LoadAsync(m.Db, id, ct);
                current.Status = error is null ? OutboundStatus.Sent : OutboundStatus.Failed;
                current.SentAt = error is null ? m.Now : null;
                current.Error = error;
                m.Record(error is null ? "outbound.sent" : "outbound.failed", current.TaskId,
                    new { outbound = Id(current), target = current.Target!.Key, sha256 = current.BodySha256, error });
                return ToDto(current);
            }, ct);
            return error is null ? dto : throw Fail.Conflict("delivery_failed", $"{Id(message)} could not be delivered: {error}. It is marked failed; retry with: muthur out retry {Id(message)}");
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>A failed delivery may be retried without a new review: the bytes are the same ones that were approved.</summary>
    public async Task<OutboundDto> RetryAsync(Caller caller, string id, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        await ledger.MutateAsync(caller, async m =>
        {
            var message = await LoadAsync(m.Db, id, ct);
            if (!caller.IsFounder && caller.AgentId != message.AuthorAgentId)
                throw Fail.Rule("not_author", $"{Id(message)} was written by '{message.AuthorName}'; only they or the founder retry it.");
            if (message.Status != OutboundStatus.Failed)
                throw Fail.Rule("not_failed", $"{Id(message)} is {message.Status}; only a failed delivery can be retried.");
            message.Status = OutboundStatus.Approved;
            m.Record("outbound.retrying", message.TaskId, new { outbound = Id(message) });
        }, ct);
        return await SendAsync(caller, id, ct);
    }

    public Task<IReadOnlyList<OutboundDto>> ListAsync(string? status, int limit, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<OutboundDto>>(async (db, _) =>
        {
            var query = db.OutboundMessages.Include(o => o.Target).AsQueryable();
            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = Enum.TryParse<OutboundStatus>(status.Replace("-", "").Replace("_", ""), ignoreCase: true, out var s)
                    ? s
                    : throw Fail.Rule("invalid_status", "Status is one of: pendingreview, rejected, awaitingfounder, approved, sent, failed, all.");
                query = query.Where(o => o.Status == wanted);
            }
            var rows = await query.OrderByDescending(o => o.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
            return rows.OrderBy(o => o.Id).Select(ToDto).ToList();
        }, ct);

    public Task<OutboundDto> GetAsync(string id, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) => ToDto(await LoadAsync(db, id, ct)), ct);

    private IOutboundChannel FindChannel(string? name) =>
        channels.FirstOrDefault(c => string.Equals(c.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw Fail.Rule("unknown_channel", $"'{name}' is not a channel. Known: {string.Join(", ", channels.Select(c => c.Name))}.");

    private static async Task<OutboundMessage> LoadAsync(Data.MuthurDb db, string id, CancellationToken ct)
    {
        var digits = id.StartsWith("O-", StringComparison.OrdinalIgnoreCase) ? id[2..] : id;
        if (!int.TryParse(digits, out var number) || number <= 0)
            throw Fail.Rule("invalid_outbound_id", $"'{id}' is not an outbound id (expected O-<number>).");
        return await db.OutboundMessages.Include(o => o.Target).SingleOrDefaultAsync(o => o.Id == number, ct)
            ?? throw Fail.NotFound("Outbound message", "O-" + number);
    }

    private static string Id(OutboundMessage message) => "O-" + message.Id;

    private static string SafeReason(Exception ex) => ex switch
    {
        ChannelException => ex.Message,
        UnauthorizedAccessException => "access to the target was denied",
        DirectoryNotFoundException or FileNotFoundException or PathTooLongException => "the target path is unavailable",
        IOException => "the target could not be written",
        HttpRequestException { StatusCode: { } status } => $"the target answered HTTP {(int)status}",
        HttpRequestException => "the target could not be reached",
        TaskCanceledException or TimeoutException or OperationCanceledException => "the target timed out",
        _ => "delivery failed unexpectedly (details are in the hub log)",
    };

    private static TargetDto ToDto(OutboundTarget t) => new(t.Key, t.Channel, t.RequiresFounderApproval, t.CreatedAt);

    private static OutboundDto ToDto(OutboundMessage o) =>
        new(Id(o), o.Target?.Key ?? "", o.Target?.Channel ?? "", Status(o.Status), o.Body, o.BodySha256, o.AuthorName, o.AuthorModel,
            o.ReviewerName, o.ReviewerModel, o.ReviewNote, o.Target?.RequiresFounderApproval ?? false, o.FounderApprovedAt,
            o.TaskId is { } t ? Wire.TaskId(t) : null, o.CreatedAt, o.SentAt, o.Error);

    private static string Status(OutboundStatus status) => status switch
    {
        OutboundStatus.PendingReview => "pending_review",
        OutboundStatus.AwaitingFounder => "awaiting_founder",
        _ => status.ToString().ToLowerInvariant(),
    };
}
