using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed partial class ConductorService
{
    private sealed record WorkerReservation(string Id, WorkerAdmissionRequest Request, Guid? OwnerId, bool Released, DateTimeOffset AdmittedAt, string? OwnerName = null);

    /// <summary>
    /// Whether this reservation rides in its orchestrator's seat: the owner is the conductor-started orchestrator of
    /// the task and that session is still running. An orchestrator waiting on `worker run` is a session sitting in
    /// a foreground call, spending nothing, so its first worker is the seat's real occupant rather than a second
    /// one. The sim showed what the other reading costs: two orchestrators fill a two-seat ceiling and neither
    /// can admit the worker it exists to run, so nothing lands until one of them stalls.
    /// </summary>
    private bool Nested(WorkerReservation reservation)
    {
        if (reservation.OwnerName != OrchestratorSessionLauncher.IdentityName(reservation.Request.Task)) return false;
        lock (_running) return _running.Contains(OrchestratorKey(reservation.Request.Task));
    }

    /// <summary>Unreleased worker reservations that occupy a seat of their own: one nested worker per running orchestrator is free.</summary>
    private int SeatedWorkers(IEnumerable<WorkerReservation> reservations)
    {
        var nestedFor = new HashSet<string>(StringComparer.Ordinal);
        var seated = 0;
        foreach (var r in reservations.Where(r => !r.Released))
            if (!(Nested(r) && nestedFor.Add(r.Request.Task))) seated++;
        return seated;
    }

    private static async Task<List<WorkerReservation>> WorkerReservationsAsync(MuthurDb db, CancellationToken ct)
    {
        var events = await db.Events.Where(e => e.Type == "worker.admitted" || e.Type == "worker.released")
            .OrderBy(e => e.Seq).ToListAsync(ct);
        var reservations = new Dictionary<string, WorkerReservation>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var id = Text(e.PayloadJson, "reservationId")!;
            if (e.Type == "worker.released")
            {
                if (reservations.TryGetValue(id, out var previous)) reservations[id] = previous with { Released = true };
                continue;
            }
            reservations.Add(id, new(id, new(Text(e.PayloadJson, "task")!, Text(e.PayloadJson, "tier")!,
                Text(e.PayloadJson, "harness")!, Text(e.PayloadJson, "model")!, Text(e.PayloadJson, "account")!,
                Text(e.PayloadJson, "runId")!, Text(e.PayloadJson, "inputHash")!), e.ActorAgentId, false, e.At, Text(e.PayloadJson, "ownerName")));
        }
        return [.. reservations.Values];
    }

    public async Task<WorkerAdmissionDto> AdmitWorkerAsync(Caller caller, WorkerAdmissionRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (new[] { request.Task, request.Tier, request.Harness, request.Model, request.Account, request.RunId, request.InputHash }
                .Any(string.IsNullOrWhiteSpace) || !Guid.TryParseExact(request.RunId, "N", out _) || request.InputHash.Length != 64 ||
            request.InputHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw Fail.Rule("worker_request_invalid", "Supply nonempty worker identifiers and a run ID in GUID N format.");
        if (Volatile.Read(ref _probeStopping) != 0 || _sessions.IsCancellationRequested)
            throw Fail.Rule("worker_admission_disabled", "The conductor is stopping.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessions.Token);
        var token = lifetime.Token;
        await _pass.WaitAsync(token);
        try
        {
            return await ledger.MutateAsync(caller, async m =>
            {
                var reservations = await WorkerReservationsAsync(m.Db, token);
                var previous = reservations.SingleOrDefault(r => r.Request.RunId == request.RunId);
                if (previous is not null)
                {
                    if (previous.OwnerId != caller.AgentId)
                        throw Fail.Unauthorized("This run ID belongs to another caller.");
                    if (previous.Request != request)
                        throw Fail.Conflict("worker_run_conflict", "This run ID was already used with different inputs.");
                    return new WorkerAdmissionDto(previous.Id, previous.Request.Task, request.RunId, false);
                }
                var task = await TaskService.LoadAsync(m.Db, request.Task, token);
                if (task.State != TaskState.InProgress)
                    throw Fail.Rule("worker_request_invalid", "Full-worker admission requires an in-progress task, including for Founder.");
                var agent = caller.IsAgent
                    ? await m.Db.Agents.SingleOrDefaultAsync(a => a.Id == caller.AgentId, token) : null;
                if (!caller.IsFounder && (agent?.Tier != "mastermind" || task.OwnerAgentId != agent.Id || task.State != TaskState.InProgress))
                    throw Fail.Unauthorized("Only the current mastermind owner or Founder may admit a worker.");
                var enabled = await m.Db.Meta.Where(e => e.Key == MetaEntry.ConductorEnabled).Select(e => e.Value).SingleOrDefaultAsync(token);
                if (Volatile.Read(ref _probeStopping) != 0 || _sessions.IsCancellationRequested ||
                    !(enabled is null ? options.ConductorEnabled : enabled == "true"))
                    throw Fail.Rule("worker_admission_disabled", "The conductor is not admitting workers.");
                if (!harnesses.HasWorkerCandidate(request))
                    throw Fail.Rule("worker_candidate_unavailable", "The exact worker candidate is not configured in this tier.");
                if (await m.Db.AccountLimits.AnyAsync(a => a.Account == request.Account && a.LimitedUntil > m.Now, token))
                    throw Fail.Rule("worker_account_limited", "The worker account is limited.");
                var ceiling = (await CeilingAsync(m.Db, m.Now, token)).Sessions;
                var proposed = new WorkerReservation("", request, caller.AgentId, false, m.Now, agent?.Name);
                var ridesInItsSeat = Nested(proposed) && !reservations.Any(r => !r.Released && r.Request.Task == request.Task && Nested(r));
                if (!ridesInItsSeat &&
                    RunningCount + SeatedWorkers(reservations) + (await ProbeReservationsAsync(m.Db, token)).Count(r => !r.Released) >= ceiling)
                    throw Fail.Rule("worker_capacity_exhausted", "The shared session ceiling is full.");
                if ((await BudgetBlockedAsync(m.Db, m.Now, token)).Contains(OrchestratorKey(Wire.TaskId(task.Id))))
                    throw Fail.Rule("worker_budget_exhausted", "The task's daily orchestrator session budget is exhausted.");
                token.ThrowIfCancellationRequested();
                var reservationId = Guid.NewGuid().ToString("N");
                m.Record("worker.admitted", task.Id, new
                {
                    reservationId, request.RunId, ownerAgentId = caller.AgentId, ownerName = agent?.Name ?? caller.Name,
                    request.InputHash, request.Task, request.Tier, request.Harness, request.Model, request.Account, role = OrchestratorRole,
                });
                return new WorkerAdmissionDto(reservationId, request.Task, request.RunId, true);
            }, token);
        }
        finally { _pass.Release(); }
    }

    public async Task ReleaseWorkerAsync(Caller caller, WorkerReleaseRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (string.IsNullOrWhiteSpace(request.ReservationId))
            throw Fail.Rule("worker_request_invalid", "Supply a reservation ID.");
        if (!request.CleanupConfirmed)
            throw Fail.Rule("worker_cleanup_unconfirmed", "Confirm the process tree is gone before releasing this reservation.");
        await _pass.WaitAsync(ct);
        try
        {
            await ledger.MutateAsync(caller, async m =>
            {
                var reservation = (await WorkerReservationsAsync(m.Db, ct)).SingleOrDefault(r => r.Id == request.ReservationId)
                    ?? throw Fail.NotFound("Worker reservation", request.ReservationId);
                if (!caller.IsFounder && reservation.OwnerId != caller.AgentId)
                    throw Fail.Unauthorized("Only the original caller or Founder may release a worker.");
                if (!reservation.Released)
                    m.Record("worker.released", payload: new { reservationId = reservation.Id });
            }, ct);
        }
        finally { _pass.Release(); }
    }

    public Task<IReadOnlyList<WorkerReservationDto>> WorkerReservationsAsync(Caller caller, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        return ledger.ReadAsync<IReadOnlyList<WorkerReservationDto>>(async (db, _) =>
            (await WorkerReservationsAsync(db, ct)).Where(r => !r.Released && (caller.IsFounder || r.OwnerId == caller.AgentId))
                .Select(r => new WorkerReservationDto(r.Id, r.Request, r.AdmittedAt)).ToList(), ct);
    }
}
