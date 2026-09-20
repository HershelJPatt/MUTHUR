using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Muthur.Data;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>Decision requests: overseer triage, delegated technical judgment, or protected human decisions.</summary>
public sealed class RequestService(Ledger ledger, LeasePolicy leases)
{
    public Task<FounderRequestDto> AskAsync(Caller caller, AskRequest request, CancellationToken ct = default)
    {
        var agentId = caller.RequireAgent();
        if (request.Kind is not ("triage" or "human" or "technical")) throw Fail.Rule("request_kind", "Request kind must be triage, human or technical.");
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
                    m.Record("task.blocked", task.Id, new { reason = "waiting on a decision", kind = request.Kind });
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
            await OverseerService.Store(m, $"request.kind.{entity.Id}", request.Kind, ct);
            m.Record("request.asked", task?.Id, new { request = entity.Id, agent = caller.Name, entity.Question, entity.Options, kind = request.Kind });
            return ToDto(entity, task?.Title) with { Kind = request.Kind, RouteReason = DefaultReason(request.Kind) };
        }, ct);
    }

    public Task<FounderRequestDto> AnswerAsync(Caller caller, int id, AnswerRequest request, CancellationToken ct = default)
    {
        if (!caller.IsFounder) throw Fail.Unauthorized("Only the founder answers founder requests (pass --founder).");
        if (string.IsNullOrWhiteSpace(request.Answer)) throw Fail.Rule("answer_required", "An answer needs text.");

        return AnswerCoreAsync(caller, id, request, false, ct);
    }

    public Task<FounderRequestDto> DecideAsync(Caller caller, OverseerDecision decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Answer) || string.IsNullOrWhiteSpace(decision.Reason) || string.IsNullOrWhiteSpace(decision.Evidence))
            throw Fail.Rule("decision_evidence", "A delegated decision requires an answer, reasoning and evidence references.");
        var answer = $"{decision.Answer}\n\nReason: {decision.Reason}\n\nEvidence: {decision.Evidence}";
        if (answer.Length > 8000) throw Fail.Rule("decision_size", "Keep the recorded decision within 8000 characters.");
        return AnswerCoreAsync(caller, decision.Request, new AnswerRequest(answer), true, ct);
    }

    public Task<FounderRequestDto> TriageAsync(Caller caller, OverseerTriage request, CancellationToken ct = default)
    {
        if (request.Kind is not ("technical" or "human") || string.IsNullOrWhiteSpace(request.Reason) ||
            string.IsNullOrWhiteSpace(request.Evidence) || request.Reason.Length > 2000 || request.Evidence.Length > 2000)
            throw Fail.Rule("triage_evidence", "Choose technical or human with a reason and evidence (at most 2000 characters each).");
        return ledger.MutateAsync(caller, async m =>
        {
            await OverseerService.RequireActive(m, ct);
            var entity = await m.Db.FounderRequests.SingleOrDefaultAsync(r => r.Id == request.Request, ct)
                ?? throw Fail.NotFound("Request", request.Request.ToString());
            if (entity.Status != RequestStatus.Open) throw Fail.Conflict("request_closed", "Only an open request can be triaged.");
            var current = await WithRouteAsync(m.Db, ToDto(entity, null), ct);
            if (current.Kind == "human") throw Fail.Unauthorized("Explicit or legacy human-only requests cannot be reclassified by the overseer.");
            var reason = $"{request.Reason.Trim()}\nEvidence: {request.Evidence.Trim()}";
            await OverseerService.Store(m, $"request.kind.{entity.Id}", request.Kind, ct);
            await OverseerService.Store(m, $"request.routeReason.{entity.Id}", reason, ct);
            m.Record("request.triaged", entity.TaskId, new { request = entity.Id, from = current.Kind,
                kind = request.Kind, request.Reason, request.Evidence });
            var title = entity.TaskId is { } id ? await m.Db.Tasks.Where(t => t.Id == id).Select(t => t.Title).SingleAsync(ct) : null;
            return ToDto(entity, title) with { Kind = request.Kind, RouteReason = reason };
        }, ct);
    }

    internal static string KindFor(IReadOnlyDictionary<string, string> routes, int id) =>
        routes.GetValueOrDefault($"request.kind.{id}") switch
        {
            "\"triage\"" => "triage",
            "\"technical\"" => "technical",
            _ => "human"
        };
    private static string DefaultReason(string kind) => kind switch
    {
        "triage" => "Awaiting overseer classification and technical direction.",
        "technical" => "Delegated engineering decision; overseer may answer with reasoning and evidence.",
        _ => "Reserved for the founder; legacy or explicit human-only request."
    };
    internal static string ReasonFor(IReadOnlyDictionary<string, string> routes, int id) =>
        routes.TryGetValue($"request.routeReason.{id}", out var text)
            ? JsonSerializer.Deserialize<string>(text) ?? DefaultReason(KindFor(routes, id)) : DefaultReason(KindFor(routes, id));
    private static async Task<FounderRequestDto> WithRouteAsync(MuthurDb db, FounderRequestDto dto, CancellationToken ct)
    {
        var keys = new[] { $"request.kind.{dto.Id}", $"request.routeReason.{dto.Id}" };
        var routes = await db.Meta.Where(x => keys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        return dto with { Kind = KindFor(routes, dto.Id), RouteReason = ReasonFor(routes, dto.Id) };
    }

    private Task<FounderRequestDto> AnswerCoreAsync(Caller caller, int id, AnswerRequest request, bool delegated, CancellationToken ct) =>
        ledger.MutateAsync(caller, async m =>
        {
            if (delegated)
            {
                await OverseerService.RequireActive(m, ct);
                var kind = await m.Db.Meta.Where(x => x.Key == $"request.kind.{id}").Select(x => x.Value).SingleOrDefaultAsync(ct);
                if (kind != "\"technical\"") throw Fail.Unauthorized("Only technical requests can be answered. Classify triage requests first; human-only requests remain protected.");
            }
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
            return await WithRouteAsync(m.Db, ToDto(entity, title), ct);
        }, ct);

    /// <summary>
    /// One answer to several requests asked in the same words — but never one action. Each goes through
    /// <see cref="AnswerAsync"/> on its own, in id order, so every unblock, ledger event and message happens
    /// exactly as if the founder had clicked them one at a time. One that refuses does not stop the rest:
    /// this is a convenience over a queue, not a transaction, and it must not pretend to be one.
    /// <para>
    /// It lives here rather than in the panel because a click is the one thing an unattended validator cannot
    /// make. The button is a call to this method and nothing else, so what the founder gets when they press
    /// it is exactly what a test can establish without a browser.
    /// </para>
    /// </summary>
    /// <returns>The message from each request that refused, by id. Empty when every one was answered.</returns>
    public async Task<IReadOnlyDictionary<int, string>> AnswerManyAsync(
        Caller caller, IEnumerable<int> ids, AnswerRequest request, CancellationToken ct = default)
    {
        var refused = new Dictionary<int, string>();
        foreach (var id in ids.Distinct().Order())
        {
            try
            {
                await AnswerAsync(caller, id, request, ct);
            }
            catch (MuthurException ex)
            {
                refused[id] = ex.Message;
            }
        }
        return refused;
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
            var title = await UnblockAsync(m, entity, ct);
            if (caller.AgentId != entity.AgentId) // someone else closed the asker's question: they should hear about it
                MessageService.Post(m, null, caller.Name, Recipient.Agent, entity.AgentName,
                    $"Your request #{id} (\"{entity.Question}\") was withdrawn without an answer. Decide it yourself within your remit, or ask again with more context.", entity.TaskId);
            return await WithRouteAsync(m.Db, ToDto(entity, title), ct);
        }, ct);

    /// <summary>
    /// Open requests come back in the order of what they are costing, not of when they were asked: a question
    /// three tasks are stopped behind is not the same size of thing as one nobody is waiting on, and at two
    /// hundred of them an id order is no order at all. Closed ones keep the id order — nothing is waiting on
    /// an answered question, so there is nothing to rank.
    /// </summary>
    public Task<IReadOnlyList<FounderRequestDto>> ListAsync(bool openOnly, int limit = 200, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<FounderRequestDto>>(async (db, _) =>
        {
            var query = db.FounderRequests.AsQueryable();
            if (openOnly) query = query.Where(r => r.Status == RequestStatus.Open);
            // The cap on the open queue is applied *after* the ranking, never before it. Taking the newest 200
            // and then sorting them by cost is how the one question that mattered most — the oldest, with three
            // tasks stopped behind it — fell off the page at 210 open. The read is still bounded: Ceiling is an
            // order of magnitude above any queue a founder has, and far above the number they are shown.
            var rows = await query
                .OrderByDescending(r => r.Id)
                .Take(openOnly ? Ceiling : Math.Clamp(limit, 1, 1000))
                .ToListAsync(ct);
            var taskIds = rows.Where(r => r.TaskId != null).Select(r => r.TaskId!.Value).Distinct().ToList();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t, ct);

            // Only tasks that are actually blocked have work stopped behind them, and only their unfinished
            // children are waiting: a done or cancelled child is not costing anybody anything.
            var blocked = tasks.Values.Where(t => t.State == TaskState.Blocked).Select(t => t.Id).ToList();
            var dependents = blocked.Count == 0
                ? []
                : await db.Tasks
                    .Where(t => t.ParentId != null && blocked.Contains(t.ParentId!.Value)
                        && t.State != TaskState.Done && t.State != TaskState.Cancelled)
                    .GroupBy(t => t.ParentId!.Value)
                    .Select(g => new { Parent = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.Parent, x => x.Count, ct);

            var routeKeys = rows.SelectMany(r => new[] { $"request.kind.{r.Id}", $"request.routeReason.{r.Id}" }).ToArray();
            var routes = await db.Meta.Where(x => routeKeys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
            var dtos = rows.Select(r =>
            {
                var task = r.TaskId is { } id ? tasks.GetValueOrDefault(id) : null;
                var blocks = task is { State: TaskState.Blocked };
                return ToDto(r, task?.Title, blocks, blocks ? dependents.GetValueOrDefault(task!.Id) : 0)
                    with { Kind = KindFor(routes, r.Id), RouteReason = ReasonFor(routes, r.Id) };
            });

            return openOnly
                ? dtos.OrderByDescending(r => r.Dependents)
                    .ThenByDescending(r => r.BlocksTask)
                    .ThenBy(r => r.CreatedAt)
                    .ThenBy(r => r.Id)          // a total order, so two reads of the same queue never disagree
                    .Take(Math.Clamp(limit, 1, 1000))
                    .ToList()
                : dtos.OrderBy(r => r.Id).ToList();
        }, ct);

    /// <summary>How many questions are open and when the oldest was asked, whatever the page is showing.</summary>
    /// <remarks>
    /// The count the founder is told must be the number that is waiting, not the number that fitted: a badge
    /// reading 200 while 210 wait is worse than no badge, because it is believable.
    /// </remarks>
    public Task<(int Count, DateTimeOffset? Oldest)> OpenSummaryAsync(CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) =>
        {
            var open = db.FounderRequests.Where(r => r.Status == RequestStatus.Open);
            var count = await open.CountAsync(ct);
            return (count, count == 0 ? null : (DateTimeOffset?)await open.MinAsync(r => r.CreatedAt, ct));
        }, ct);

    /// <summary>The most open requests one read will rank. Far above any real queue, and far above what is shown.</summary>
    private const int Ceiling = 5000;

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

    private static FounderRequestDto ToDto(FounderRequest r, string? taskTitle, bool blocksTask = false, int dependents = 0) =>
        new(r.Id, r.TaskId is { } id ? Wire.TaskId(id) : null, taskTitle, r.AgentName, r.Question, r.Options,
            r.Status.ToString().ToLowerInvariant(), r.Answer, r.CreatedAt, r.AnsweredAt, blocksTask, dependents);
}
