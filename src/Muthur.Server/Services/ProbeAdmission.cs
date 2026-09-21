using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed partial class ConductorService
{
    private int _probeStopping;

    private sealed record ProbeReservation(string Id, ProbeAdmissionRequest Request, Guid? OwnerId, bool Released);

    private static async Task<List<ProbeReservation>> ProbeReservationsAsync(MuthurDb db, CancellationToken ct)
    {
        var events = await db.Events.Where(e => e.Type == "worker.probe_admitted" || e.Type == "worker.probe_released")
            .OrderBy(e => e.Seq).ToListAsync(ct);
        var reservations = new Dictionary<string, ProbeReservation>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var id = Text(e.PayloadJson, "reservationId")!;
            if (e.Type == "worker.probe_released")
            {
                if (reservations.TryGetValue(id, out var previous)) reservations[id] = previous with { Released = true };
                continue;
            }
            reservations.Add(id, new(id, new(Text(e.PayloadJson, "task")!, Text(e.PayloadJson, "tier")!,
                Text(e.PayloadJson, "harness")!, Text(e.PayloadJson, "model")!, Text(e.PayloadJson, "account")!,
                Text(e.PayloadJson, "runId")!), e.ActorAgentId, false));
        }
        return [.. reservations.Values];
    }

    public async Task<ProbeAdmissionDto> AdmitProbeAsync(Caller caller, ProbeAdmissionRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (new[] { request.Task, request.Tier, request.Harness, request.Model, request.Account, request.RunId }
                .Any(string.IsNullOrWhiteSpace) || !Guid.TryParseExact(request.RunId, "N", out _))
            throw Fail.Rule("probe_request_invalid", "Supply nonempty probe identifiers and a run ID in GUID N format.");
        if (Volatile.Read(ref _probeStopping) != 0 || _sessions.IsCancellationRequested)
            throw Fail.Rule("probe_admission_disabled", "The conductor is stopping.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessions.Token);
        var token = lifetime.Token;
        await _pass.WaitAsync(token);
        try
        {
            return await ledger.MutateAsync(caller, async m =>
            {
                var task = await TaskService.LoadAsync(m.Db, request.Task, token);
                var agent = caller.IsAgent
                    ? await m.Db.Agents.SingleOrDefaultAsync(a => a.Id == caller.AgentId, token) : null;
                if (!caller.IsFounder && (agent?.Tier != "mastermind" || task.OwnerAgentId != agent.Id || task.State != TaskState.InProgress))
                    throw Fail.Unauthorized("Only the current mastermind owner or Founder may admit a probe.");
                var reservations = await ProbeReservationsAsync(m.Db, token);
                var previous = reservations.SingleOrDefault(r => r.Request.RunId == request.RunId);
                if (previous is not null)
                {
                    if (previous.OwnerId != caller.AgentId)
                        throw Fail.Unauthorized("This run ID belongs to another caller.");
                    if (previous.Request != request)
                        throw Fail.Conflict("probe_run_conflict", "This run ID was already used with different inputs.");
                    return new ProbeAdmissionDto(previous.Id, previous.Request.Task, request.RunId, false);
                }
                var enabled = await m.Db.Meta.Where(e => e.Key == MetaEntry.ConductorEnabled).Select(e => e.Value).SingleOrDefaultAsync(token);
                if (Volatile.Read(ref _probeStopping) != 0 || _sessions.IsCancellationRequested ||
                    !(enabled is null ? options.ConductorEnabled : enabled == "true"))
                    throw Fail.Rule("probe_admission_disabled", "The conductor is not admitting probes.");
                if (!harnesses.HasProbeCandidate(request))
                    throw Fail.Rule("probe_candidate_unavailable", "The exact probe candidate is not configured in this tier.");
                if (await m.Db.AccountLimits.AnyAsync(a => a.Account == request.Account && a.LimitedUntil > m.Now, token))
                    throw Fail.Rule("probe_account_limited", "The probe account is limited.");
                var ceiling = (await CeilingAsync(m.Db, m.Now, token)).Sessions;
                if (RunningCount + reservations.Count(r => !r.Released) >= ceiling)
                    throw Fail.Rule("probe_capacity_exhausted", "The shared session ceiling is full.");
                if ((await BudgetBlockedAsync(m.Db, m.Now, token)).Contains(OrchestratorKey(Wire.TaskId(task.Id))))
                    throw Fail.Rule("probe_budget_exhausted", "The task's daily orchestrator session budget is exhausted.");
                token.ThrowIfCancellationRequested();
                var reservationId = Guid.NewGuid().ToString("N");
                m.Record("worker.probe_admitted", task.Id, new
                {
                    reservationId, request.RunId, ownerAgentId = caller.AgentId, ownerName = agent?.Name ?? caller.Name,
                    request.Task, request.Tier, request.Harness, request.Model, request.Account, role = OrchestratorRole,
                });
                return new ProbeAdmissionDto(reservationId, request.Task, request.RunId, true);
            }, token);
        }
        finally { _pass.Release(); }
    }

    public async Task ReleaseProbeAsync(Caller caller, ProbeReleaseRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (string.IsNullOrWhiteSpace(request.ReservationId))
            throw Fail.Rule("probe_request_invalid", "Supply a reservation ID.");
        await _pass.WaitAsync(ct);
        try
        {
            await ledger.MutateAsync(caller, async m =>
            {
                var reservation = (await ProbeReservationsAsync(m.Db, ct)).SingleOrDefault(r => r.Id == request.ReservationId)
                    ?? throw Fail.NotFound("Probe reservation", request.ReservationId);
                if (!caller.IsFounder && reservation.OwnerId != caller.AgentId)
                    throw Fail.Unauthorized("Only the original caller or Founder may release a probe.");
                if (!reservation.Released)
                    m.Record("worker.probe_released", payload: new { reservationId = reservation.Id });
            }, ct);
        }
        finally { _pass.Release(); }
    }
}
