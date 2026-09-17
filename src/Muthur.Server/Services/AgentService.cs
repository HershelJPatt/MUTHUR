using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed partial class AgentService(Ledger ledger, LeasePolicy leases, TimeProvider clock)
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTouch = new();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,47}$")]
    private static partial Regex NamePattern();

    public async Task<RegisterAgentResponse> RegisterAsync(Caller caller, RegisterAgentRequest request, CancellationToken ct = default)
    {
        var name = (request.Name ?? "").Trim().ToLowerInvariant();
        if (!NamePattern().IsMatch(name) || name is "founder" or "muthur" or "anonymous")
            throw Fail.Rule("invalid_name", "Agent names are 1-48 chars of a-z, 0-9, '.', '_' or '-', and may not be a reserved name.");
        if (string.IsNullOrWhiteSpace(request.Harness) || string.IsNullOrWhiteSpace(request.Model))
            throw Fail.Rule("harness_required", "Both --harness and --model are required: the ledger records which model did what.");

        var token = Tokens.Generate();
        var agent = await ledger.MutateAsync(caller, async m =>
        {
            var existing = await m.Db.Agents.SingleOrDefaultAsync(a => a.Name == name, ct);
            if (existing is not null && !(caller.IsFounder || caller.AgentId == existing.Id))
                throw Fail.Conflict("agent_exists", $"Agent '{name}' is already registered. Re-register as that agent, or with --founder to take the name over.");

            var agent = existing ?? new Agent { Id = Guid.NewGuid(), Name = name, TokenHash = "", Harness = "", Model = "", RegisteredAt = m.Now };
            agent.TokenHash = Tokens.Hash(token);
            agent.Harness = request.Harness.Trim().ToLowerInvariant();
            agent.Model = request.Model.Trim();
            agent.Tier = request.Tier?.Trim().ToLowerInvariant();
            agent.Account = request.Account?.Trim();
            agent.LastHeartbeat = m.Now;
            if (existing is null) m.Db.Agents.Add(agent);

            m.Record(existing is null ? "agent.registered" : "agent.reregistered", payload: new { agent = name, agent.Harness, agent.Model, agent.Tier, agent.Account });
            return agent;
        }, ct);

        return new RegisterAgentResponse(agent.ToDto(leases, clock.GetUtcNow(), [], 0), token);
    }

    /// <summary>Token → caller. Any authenticated request also counts as a sign of life (throttled).</summary>
    public async Task<Caller?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        var hash = Tokens.Hash(token);
        var agent = await ledger.ReadAsync((db, _) => db.Agents.SingleOrDefaultAsync(a => a.TokenHash == hash, ct), ct);
        if (agent is null) return null;

        var caller = new Caller(CallerKind.Agent, agent.Id, agent.Name, agent.ModelLabel);
        var now = clock.GetUtcNow();
        if (!_lastTouch.TryGetValue(agent.Id, out var last) || now - last >= TouchInterval)
        {
            _lastTouch[agent.Id] = now;
            await HeartbeatAsync(caller, new HeartbeatRequest(), ct);
        }
        return caller;
    }

    /// <summary>Proves the agent is alive: refreshes its heartbeat and renews every lease it holds.</summary>
    public Task HeartbeatAsync(Caller caller, HeartbeatRequest request, CancellationToken ct = default)
    {
        var agentId = caller.RequireAgent();
        return ledger.MutateAsync(caller, async m =>
        {
            var agent = await m.Db.Agents.SingleAsync(a => a.Id == agentId, ct);
            agent.LastHeartbeat = m.Now;
            if (request.Summary is not null)
            {
                agent.Summary = request.Summary.Trim();
                m.Record("agent.summary", payload: new { agent = agent.Name, summary = agent.Summary });
            }

            var renewTo = m.Now + leases.ClaimLease;
            var owned = await m.Db.Tasks
                .Where(t => t.OwnerAgentId == agentId && t.State == TaskState.InProgress)
                .ToListAsync(ct);
            foreach (var task in owned.Where(t => t.ClaimExpires is null || t.ClaimExpires < renewTo))
                task.ClaimExpires = renewTo;

            await RoleLeases.RenewAsync(m, agentId, leases, ct);
        }, ct);
    }

    public Task SetLimitedAsync(Caller caller, LimitedRequest request, CancellationToken ct = default)
    {
        var agentId = caller.RequireAgent();
        return ledger.MutateAsync(caller, async m =>
        {
            var agent = await m.Db.Agents.SingleAsync(a => a.Id == agentId, ct);
            agent.LimitedUntil = request.Until;
            m.Record(request.Until is null ? "agent.limit_cleared" : "agent.limited", payload: new { agent = agent.Name, until = request.Until, account = agent.Account });
        }, ct);
    }

    public Task<IReadOnlyList<AgentDto>> ListAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<AgentDto>>(async (db, now) =>
        {
            var agents = await db.Agents.OrderBy(a => a.Name).ToListAsync(ct);
            var open = await db.Tasks
                .Where(t => t.OwnerAgentId != null && t.State != TaskState.Done && t.State != TaskState.Cancelled)
                .GroupBy(t => t.OwnerAgentId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var roles = await RoleLeases.HeldByAgentAsync(db, now, ct);
            return agents
                .Select(a => a.ToDto(leases, now, roles.GetValueOrDefault(a.Id) ?? [], open.GetValueOrDefault(a.Id)))
                .ToList();
        }, ct);

    public async Task<AgentDto> GetAsync(Guid agentId, CancellationToken ct = default) =>
        (await ListAsync(ct)).SingleOrDefault(a => a.Id == agentId) ?? throw Fail.NotFound("Agent", agentId.ToString());
}
