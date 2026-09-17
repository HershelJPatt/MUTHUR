using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>Questions only a founder can answer. Asking blocks the task; the answer unblocks it and wakes the asker.</summary>
public sealed class RequestService(Ledger ledger, LeasePolicy leases)
{
    public Task<FounderRequestDto> AskAsync(Caller caller, AskRequest request, CancellationToken ct = default)
    {
        var agentId = caller.RequireAgent();
        if (string.IsNullOrWhiteSpace(request.Question))
            throw Fail.Rule("question_required", "Ask a concrete question; offer options when you can.");

        return ledger.MutateAsync(caller, async m =>
        {
            WorkTask? task = null;
            if (request.Task is { Length: > 0 } id)
            {
                task = await TaskService.LoadAsync(m.Db, id, ct);
                if (task.State is not (TaskState.InProgress or TaskState.Blocked))
                    throw Fail.Rule("not_in_progress", $"{Wire.TaskId(task.Id)} is '{task.State.ToWire()}'; only in-progress work can be blocked on a founder.");
                TaskService.RequireOwnerOrFounder(task, caller);
                if (task.State == TaskState.InProgress)
                {
                    task.State = TaskState.Blocked;
                    task.UpdatedAt = m.Now;
                    m.Record("task.blocked", task.Id, new { reason = "waiting on a founder" });
                }
            }

            var entity = new FounderRequest
            {
                TaskId = task?.Id,
                AgentId = agentId,
                AgentName = caller.Name,
                Question = request.Question.Trim(),
                Options = (request.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).ToList(),
                Status = RequestStatus.Open,
                CreatedAt = m.Now,
            };
            m.Db.FounderRequests.Add(entity);
            await m.Db.SaveChangesAsync(ct);
            m.Record("request.asked", task?.Id, new { request = entity.Id, agent = caller.Name, entity.Question, entity.Options });
            return ToDto(entity, task?.Title);
        }, ct);
    }

    public Task<FounderRequestDto> AnswerAsync(Caller caller, int id, AnswerRequest request, CancellationToken ct = default)
    {
        if (!caller.IsFounder) throw Fail.Unauthorized("Only the founder answers founder requests (pass --founder).");
        if (string.IsNullOrWhiteSpace(request.Answer)) throw Fail.Rule("answer_required", "An answer needs text.");

        return ledger.MutateAsync(caller, async m =>
        {
            var entity = await m.Db.FounderRequests.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw Fail.NotFound("Request", id.ToString());
            if (entity.Status != RequestStatus.Open)
                throw Fail.Conflict("request_closed", $"Request {id} is already {entity.Status.ToString().ToLowerInvariant()}.");

            entity.Status = RequestStatus.Answered;
            entity.Answer = request.Answer.Trim();
            entity.AnsweredAt = m.Now;
            m.Record("request.answered", entity.TaskId, new { request = id, entity.Answer });

            var title = await UnblockAsync(m, entity, ct);
            MessageService.Post(m, null, caller.Name, Recipient.Agent, entity.AgentName,
                $"Answer to your request #{id} (\"{entity.Question}\"): {entity.Answer}", entity.TaskId);
            return ToDto(entity, title);
        }, ct);
    }

    public Task<FounderRequestDto> CancelAsync(Caller caller, int id, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var entity = await m.Db.FounderRequests.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw Fail.NotFound("Request", id.ToString());
            if (!caller.IsFounder && caller.AgentId != entity.AgentId)
                throw Fail.Rule("not_asker", $"Request {id} was asked by '{entity.AgentName}'; only they or the founder may withdraw it.");
            if (entity.Status != RequestStatus.Open)
                throw Fail.Conflict("request_closed", $"Request {id} is already {entity.Status.ToString().ToLowerInvariant()}.");

            entity.Status = RequestStatus.Cancelled;
            entity.AnsweredAt = m.Now;
            m.Record("request.cancelled", entity.TaskId, new { request = id });
            return ToDto(entity, await UnblockAsync(m, entity, ct));
        }, ct);

    public Task<IReadOnlyList<FounderRequestDto>> ListAsync(bool openOnly, int limit = 200, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<FounderRequestDto>>(async (db, _) =>
        {
            var query = db.FounderRequests.AsQueryable();
            if (openOnly) query = query.Where(r => r.Status == RequestStatus.Open);
            var rows = await query.OrderByDescending(r => r.Id).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);
            var taskIds = rows.Where(r => r.TaskId != null).Select(r => r.TaskId!.Value).Distinct().ToList();
            var titles = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
            return rows.OrderBy(r => r.Id).Select(r => ToDto(r, r.TaskId is { } t ? titles.GetValueOrDefault(t) : null)).ToList();
        }, ct);

    /// <summary>A task that leaves its owner's hands takes its open questions with it; nobody is waiting for the answer any more.</summary>
    public static async Task WithdrawForTaskAsync(Mutation m, int taskId, string reason, CancellationToken ct)
    {
        var open = await m.Db.FounderRequests.Where(r => r.TaskId == taskId && r.Status == RequestStatus.Open).ToListAsync(ct);
        foreach (var request in open)
        {
            request.Status = RequestStatus.Cancelled;
            request.AnsweredAt = m.Now;
            m.Record("request.cancelled", taskId, new { request = request.Id, reason });
        }
    }

    /// <summary>The task resumes once no open request remains on it. Returns the task title for the DTO.</summary>
    private async Task<string?> UnblockAsync(Mutation m, FounderRequest closed, CancellationToken ct)
    {
        if (closed.TaskId is not { } taskId) return null;
        var task = await m.Db.Tasks.SingleAsync(t => t.Id == taskId, ct);
        var stillOpen = await m.Db.FounderRequests.AnyAsync(r => r.TaskId == taskId && r.Id != closed.Id && r.Status == RequestStatus.Open, ct);
        if (task.State == TaskState.Blocked && !stillOpen)
        {
            task.State = TaskState.InProgress;
            task.ClaimExpires = m.Now + leases.ClaimLease; // the owner may have been away a long time; give them a fresh lease
            task.UpdatedAt = m.Now;
            m.Record("task.unblocked", taskId, new { request = closed.Id });
        }
        return task.Title;
    }

    private static FounderRequestDto ToDto(FounderRequest r, string? taskTitle) =>
        new(r.Id, r.TaskId is { } id ? Wire.TaskId(id) : null, taskTitle, r.AgentName, r.Question, r.Options,
            r.Status.ToString().ToLowerInvariant(), r.Answer, r.CreatedAt, r.AnsweredAt);
}
