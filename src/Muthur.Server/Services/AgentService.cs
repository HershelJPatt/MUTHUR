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

    /// <summary>Called only by the launcher while its child process is still running.</summary>
    internal async Task RenewChildAsync(Muthur.Launch.AgentIdentity identity, CancellationToken ct)
    {
        if (await AuthenticateAsync(identity.Token, ct) is { } caller)
            await HeartbeatAsync(caller, new HeartbeatRequest(), ct);
        else throw new InvalidOperationException("Child identity was revoked; stop the session instead of renewing another owner's lease.");
    }

    internal Task ReleaseChildAsync(Muthur.Launch.AgentIdentity identity, CancellationToken ct) =>
        ledger.MutateAsync(Caller.Founder, async m =>
        {
            var hash = Tokens.Hash(identity.Token);
            var agent = await m.Db.Agents.SingleOrDefaultAsync(a => a.Name == identity.Name && a.TokenHash == hash, ct);
            if (agent is null) return; // Re-registration belongs to another session.
            var holds = await m.Db.RoleHolds.Where(h => h.AgentId == agent.Id).ToListAsync(ct);
            foreach (var hold in holds)
            {
                m.Db.RoleHolds.Remove(hold);
                m.Record("role.released", payload: new { role = hold.RoleKey, agent = agent.Name, reason = "child exited" });
            }
            var claims = await m.Db.TaskValidations.Where(v => v.ClaimedByAgentId == agent.Id).ToListAsync(ct);
            foreach (var claim in claims) { claim.ClaimedByAgentId = null; claim.ClaimExpires = null; }
            var owned = await m.Db.Tasks.Where(t => t.OwnerAgentId == agent.Id &&
                (t.State == TaskState.InProgress || t.State == TaskState.Blocked || t.State == TaskState.Validating || t.State == TaskState.Validated)).ToListAsync(ct);
            foreach (var task in owned)
                m.Record("conductor.orchestrator_exited", task.Id, new { agent = agent.Name });
            m.Record("conductor.child_exited", payload: new { agent = agent.Name, roles = holds.Count, claims = claims.Count });
        }, ct);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,79}$")]
    private static partial Regex NamePattern();

    public Task<RegisterAgentResponse> RegisterAsync(Caller caller, RegisterAgentRequest request, CancellationToken ct = default) =>
        RegisterCoreAsync(caller, request, conductorStaffed: false, integrationRunner: false, ct);

    /// <summary>
    /// Registration for a session the conductor is staffing. Separate from <see cref="RegisterAsync"/> rather
    /// than a flag on it so that the HTTP endpoint has no way to reach it: what marks a row is the code that
    /// staffed the session, not anything a caller can send.
    /// </summary>
    internal Task<RegisterAgentResponse> RegisterConductorSessionAsync(RegisterAgentRequest request, CancellationToken ct = default) =>
        RegisterCoreAsync(Caller.Founder, request, conductorStaffed: true, integrationRunner: false, ct);

    internal Task<RegisterAgentResponse> RegisterIntegrationRunnerAsync(RegisterAgentRequest request, CancellationToken ct = default) =>
        RegisterCoreAsync(Caller.System, request, conductorStaffed: true, integrationRunner: true, ct);

    private async Task<RegisterAgentResponse> RegisterCoreAsync(Caller caller, RegisterAgentRequest request, bool conductorStaffed, bool integrationRunner, CancellationToken ct)
    {
        if (caller.IntegrationRunner)
            throw Fail.Rule("integration_runner_forbidden", "Integration runners cannot register identities.");
        var name = (request.Name ?? "").Trim().ToLowerInvariant();
        if (!NamePattern().IsMatch(name) || name is "founder" or "muthur" or "anonymous")
            throw Fail.Rule("invalid_name", "Agent names are 1-80 chars of a-z, 0-9, '.', '_' or '-', and may not be a reserved name.");
        if (string.IsNullOrWhiteSpace(request.Harness) || string.IsNullOrWhiteSpace(request.Model))
            throw Fail.Rule("harness_required", "Both --harness and --model are required: the ledger records which model did what.");

        var token = Tokens.Generate();
        var agent = await ledger.MutateAsync(caller, async m =>
        {
            var existing = await m.Db.Agents.SingleOrDefaultAsync(a => a.Name == name, ct);
            if (existing?.IntegrationRunner == true || (integrationRunner && existing is not null))
                throw Fail.Rule("integration_runner_forbidden", "Integration runner identities cannot be re-registered or taken over.");
            if (existing is not null && !(caller.IsFounder || caller.AgentId == existing.Id))
                throw Fail.Conflict("agent_exists", $"Agent '{name}' is already registered. Re-register as that agent, or with --founder to take the name over.");

            var agent = existing ?? new Agent { Id = Guid.NewGuid(), Name = name, TokenHash = "", Harness = "", Model = "", RegisteredAt = m.Now };
            agent.TokenHash = Tokens.Hash(token);
            agent.Harness = request.Harness.Trim();
            agent.Model = request.Model.Trim();
            agent.Tier = request.Tier?.Trim().ToLowerInvariant();
            agent.Account = request.Account?.Trim();
            agent.LastHeartbeat = m.Now;
            // Sticky, never cleared. A conductor-staffed session that registers again through `muthur agent register`
            // — which the orchestrate procedure tells it to do if its token is gone — is still a staffed session.
            if (conductorStaffed) agent.ConductorStaffed = true;
            if (integrationRunner) agent.IntegrationRunner = true;
            if (existing is null) m.Db.Agents.Add(agent);

            m.Record(existing is null ? "agent.registered" : "agent.reregistered",
                payload: new { agent = name, agent.Harness, agent.Model, agent.Tier, agent.Account, agent.ConductorStaffed });
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

        var caller = new Caller(CallerKind.Agent, agent.Id, agent.Name, agent.ModelLabel, agent.IntegrationRunner);
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

            // A validator that is working and reporting in keeps the pairs it took. Only live claims: one that
            // already lapsed belongs to whoever takes it next, not to whoever comes back first.
            var claimed = await m.Db.TaskValidations
                .Where(v => v.ClaimedByAgentId == agentId && v.ClaimExpires > m.Now && v.ClaimExpires < renewTo)
                .ToListAsync(ct);
            foreach (var row in claimed) row.ClaimExpires = renewTo;

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
            if (agent.Account is { Length: > 0 } account)
                await HarnessService.ApplyAsync(m, account, request.Until, ct); // the quota belongs to the account, not the session
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

    /// <summary>
    /// The roster as a reader wants it: standing agents only, with a count of the conductor-staffed sessions
    /// left out, or everything when <paramref name="all"/> is true. Nothing is deleted or reclassified — this
    /// is a view over the same rows <see cref="ListAsync"/> returns.
    /// </summary>
    public async Task<AgentRosterDto> RosterAsync(bool all, CancellationToken ct = default)
    {
        var agents = await ListAsync(ct);
        if (all) return new AgentRosterDto(agents, 0);
        var standing = agents.Where(a => !a.ConductorStaffed).ToList();
        return new AgentRosterDto(standing, agents.Count - standing.Count);
    }

    public async Task<AgentDto> GetAsync(Guid agentId, CancellationToken ct = default) =>
        (await ListAsync(ct)).SingleOrDefault(a => a.Id == agentId) ?? throw Fail.NotFound("Agent", agentId.ToString());
}
