using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>An item as a source reports it, before the hub has seen it.</summary>
public sealed record IncomingItem(string ExternalId, string Title, string Body, string? Url, string? Author);

/// <summary>Everything that arrives from outside lands here first; an agent then decides what it becomes.</summary>
public sealed class InboundService(Ledger ledger)
{
    /// <summary>The role that is told when something new arrives, if the organization has defined it.</summary>
    public const string IntakeRole = "comms-oncall";

    /// <summary>Push from any tool. Idempotent on (Source, ExternalId).</summary>
    public Task<InboundDto> AddAsync(Caller caller, AddInboundRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        var source = (request.Source ?? "").Trim().ToLowerInvariant();
        var externalId = (request.ExternalId ?? "").Trim();
        if (source.Length == 0 || externalId.Length == 0 || string.IsNullOrWhiteSpace(request.Title))
            throw Fail.Rule("inbound_incomplete", "An inbound item needs a source, an external id and a title.");

        return ledger.MutateAsync(caller, async m =>
        {
            Guid? projectId = request.Project is { Length: > 0 } ? (await ProjectService.FindAsync(m.Db, request.Project, ct)).Id : null;
            var items = await StoreAsync(m, source, projectId,
                [new IncomingItem(externalId, request.Title.Trim(), request.Body ?? "", request.Url, request.Author)], ct);
            var item = items.Count > 0
                ? items[0]
                : await m.Db.Inbound.Include(i => i.Project).Include(i => i.ClaimedBy).SingleAsync(i => i.Source == source && i.ExternalId == externalId, ct);
            await m.Db.SaveChangesAsync(ct);
            return ToDto(item);
        }, ct);
    }

    /// <summary>Stores the items the hub has not seen yet, records them, and tells the intake role. Returns the new ones.</summary>
    public static async Task<List<InboundItem>> StoreAsync(Mutation m, string source, Guid? projectId, IReadOnlyList<IncomingItem> incoming, CancellationToken ct)
    {
        if (incoming.Count == 0) return [];
        var ids = incoming.Select(i => i.ExternalId).ToList();
        var known = await m.Db.Inbound.Where(i => i.Source == source && ids.Contains(i.ExternalId)).Select(i => i.ExternalId).ToListAsync(ct);

        var created = new List<InboundItem>();
        foreach (var item in incoming.Where(i => !known.Contains(i.ExternalId)).DistinctBy(i => i.ExternalId))
        {
            created.Add(new InboundItem
            {
                ProjectId = projectId,
                Source = source,
                ExternalId = item.ExternalId,
                Title = item.Title,
                Body = item.Body,
                Url = item.Url,
                Author = item.Author,
                Status = InboundStatus.Unclaimed,
                ReceivedAt = m.Now,
                UpdatedAt = m.Now,
            });
        }
        if (created.Count == 0) return created;

        m.Db.Inbound.AddRange(created);
        await m.Db.SaveChangesAsync(ct); // assigns the display ids the events carry
        foreach (var item in created)
            m.Record("inbound.received", payload: new { inbound = Wire.InboundId(item.Id), source, item.Title, item.Author, item.Url });

        if (await m.Db.Roles.AnyAsync(r => r.Key == IntakeRole, ct))
        {
            var summary = created.Count == 1
                ? $"New inbound {Wire.InboundId(created[0].Id)} from {source}: \"{created[0].Title}\"."
                : $"{created.Count} new inbound items from {source} ({Wire.InboundId(created[0].Id)}…{Wire.InboundId(created[^1].Id)}).";
            MessageService.PostFromHub(m, Recipient.Role, IntakeRole, summary + " Triage: muthur inbound list");
        }
        return created;
    }

    public Task<IReadOnlyList<InboundDto>> ListAsync(string? status, string? project, int limit, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<InboundDto>>(async (db, _) =>
        {
            var query = db.Inbound.Include(i => i.Project).Include(i => i.ClaimedBy).AsQueryable();
            if (!string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = ParseStatus(status ?? "unclaimed");
                query = query.Where(i => i.Status == wanted);
            }
            if (!string.IsNullOrWhiteSpace(project))
            {
                var key = project.Trim().ToLowerInvariant();
                query = query.Where(i => i.Project!.Key == key);
            }
            var rows = await query.OrderBy(i => i.Id).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);
            return rows.Select(ToDto).ToList();
        }, ct);

    public Task<InboundDto> GetAsync(string id, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) => ToDto(await LoadAsync(db, id, ct)), ct);

    /// <summary>One agent takes responsibility for the item. Exactly one wins a race, like a task claim.</summary>
    public Task<InboundDto> ClaimAsync(Caller caller, string id, CancellationToken ct = default)
    {
        var agentId = caller.RequireAgent();
        return ledger.MutateAsync(caller, async m =>
        {
            var item = await LoadAsync(m.Db, id, ct);
            if (item.Status == InboundStatus.Claimed && item.ClaimedByAgentId == agentId) return ToDto(item);
            if (item.Status != InboundStatus.Unclaimed)
                throw Fail.Conflict("inbound_taken", $"{Wire.InboundId(item.Id)} is already {item.Status.ToString().ToLowerInvariant()}" +
                    (item.ClaimedBy is null ? "." : $" by '{item.ClaimedBy.Name}'."));

            item.Status = InboundStatus.Claimed;
            item.ClaimedByAgentId = agentId;
            item.ClaimedBy = await m.Db.Agents.SingleAsync(a => a.Id == agentId, ct);
            item.UpdatedAt = m.Now;
            m.Record("inbound.claimed", payload: new { inbound = Wire.InboundId(item.Id), agent = caller.Name });
            return ToDto(item);
        }, ct);
    }

    /// <summary>Turns the item into a ledger task in the backlog. The item keeps a link to it.</summary>
    public Task<InboundDto> ConvertAsync(Caller caller, string id, ConvertInboundRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        return ledger.MutateAsync(caller, async m =>
        {
            var item = await LoadAsync(m.Db, id, ct);
            RequireHandler(item, caller);
            if (item.Status is InboundStatus.Converted or InboundStatus.Dismissed)
                throw Fail.Conflict("inbound_closed", $"{Wire.InboundId(item.Id)} is already {item.Status.ToString().ToLowerInvariant()}.");
            var project = item.Project ?? await ProjectService.FindAsync(m.Db, null, ct);

            var task = new WorkTask
            {
                ProjectId = project.Id,
                Title = string.IsNullOrWhiteSpace(request.Title) ? item.Title : request.Title.Trim(),
                Body = $"{item.Body}\n\n---\nFrom {item.Source} {item.ExternalId}" + (item.Author is null ? "" : $" by {item.Author}") + (item.Url is null ? "" : $"\n{item.Url}"),
                State = TaskState.Backlog,
                Priority = request.Priority,
                CreatedAt = m.Now,
                UpdatedAt = m.Now,
            };
            m.Db.Tasks.Add(task);
            await m.Db.SaveChangesAsync(ct);

            item.Status = InboundStatus.Converted;
            item.TaskId = task.Id;
            item.UpdatedAt = m.Now;
            m.Record("task.added", task.Id, new { task.Title, project = project.Key, task.Priority, inbound = Wire.InboundId(item.Id) });
            m.Record("inbound.converted", task.Id, new { inbound = Wire.InboundId(item.Id), by = caller.Name });
            return ToDto(item);
        }, ct);
    }

    public Task<InboundDto> DismissAsync(Caller caller, string id, DismissInboundRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw Fail.Rule("reason_required", "Say why this needs no action; the ledger keeps it.");
        return ledger.MutateAsync(caller, async m =>
        {
            var item = await LoadAsync(m.Db, id, ct);
            RequireHandler(item, caller);
            if (item.Status is InboundStatus.Converted or InboundStatus.Dismissed)
                throw Fail.Conflict("inbound_closed", $"{Wire.InboundId(item.Id)} is already {item.Status.ToString().ToLowerInvariant()}.");
            item.Status = InboundStatus.Dismissed;
            item.Resolution = request.Reason.Trim();
            item.UpdatedAt = m.Now;
            m.Record("inbound.dismissed", payload: new { inbound = Wire.InboundId(item.Id), by = caller.Name, reason = item.Resolution });
            return ToDto(item);
        }, ct);
    }

    /// <summary>A claimed item is handled by its claimer (or the founder); an unclaimed one by anyone identified.</summary>
    private static void RequireHandler(InboundItem item, Caller caller)
    {
        if (caller.IsFounder || item.ClaimedByAgentId is null || item.ClaimedByAgentId == caller.AgentId) return;
        throw Fail.Rule("not_claimer", $"{Wire.InboundId(item.Id)} is claimed by '{item.ClaimedBy?.Name}'.");
    }

    private static async Task<InboundItem> LoadAsync(Data.MuthurDb db, string id, CancellationToken ct)
    {
        if (!Wire.TryParseInboundId(id, out var number))
            throw Fail.Rule("invalid_inbound_id", $"'{id}' is not an inbound id (expected I-<number>).");
        return await db.Inbound.Include(i => i.Project).Include(i => i.ClaimedBy).SingleOrDefaultAsync(i => i.Id == number, ct)
            ?? throw Fail.NotFound("Inbound item", Wire.InboundId(number));
    }

    private static InboundStatus ParseStatus(string text) =>
        Enum.TryParse<InboundStatus>(text, ignoreCase: true, out var status)
            ? status
            : throw Fail.Rule("invalid_status", "Status is one of: unclaimed, claimed, converted, dismissed, all.");

    private static InboundDto ToDto(InboundItem i) =>
        new(Wire.InboundId(i.Id), i.Project?.Key, i.Source, i.ExternalId, i.Title, i.Body, i.Url, i.Author,
            i.Status.ToString().ToLowerInvariant(), i.ClaimedBy?.Name, i.TaskId is { } t ? Wire.TaskId(t) : null, i.Resolution, i.ReceivedAt);
}
