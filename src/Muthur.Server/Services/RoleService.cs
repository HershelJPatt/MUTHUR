using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed partial class RoleService(Ledger ledger, LeasePolicy leases)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex KeyPattern();

    /// <summary>Creates a role or updates its brief. Founder only: a brief is an agent's job description.</summary>
    public Task<RoleDto> DefineAsync(Caller caller, DefineRoleRequest request, CancellationToken ct = default)
    {
        if (!caller.IsFounder)
            throw Fail.Unauthorized("Roles and their briefs are defined by the founder (pass --founder).");
        var key = Normalize(request.Key);
        if (!KeyPattern().IsMatch(key))
            throw Fail.Rule("invalid_key", "Role keys are 1-48 chars of a-z, 0-9 or '-'.");

        return ledger.MutateAsync(caller, async m =>
        {
            var role = await m.Db.Roles.SingleOrDefaultAsync(r => r.Key == key, ct);
            var created = role is null;
            if (role is null)
            {
                role = new Role { Key = key, IsValidator = key.EndsWith("-validator", StringComparison.Ordinal) };
                m.Db.Roles.Add(role);
            }
            if (request.Brief is not null) role.BriefMd = request.Brief;
            if (request.IsValidator is { } validator) role.IsValidator = validator;
            role.UpdatedAt = m.Now;
            m.Record(created ? "role.defined" : "role.updated", payload: new { role = key, role.IsValidator, briefChars = role.BriefMd.Length });
            return ToDto(role, null);
        }, ct);
    }

    public Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<RoleDto>>(async (db, now) =>
        {
            var roles = await db.Roles.OrderBy(r => r.Key).ToListAsync(ct);
            var holds = await db.RoleHolds.Include(h => h.Agent).Where(h => h.LeaseExpires > now).ToDictionaryAsync(h => h.RoleKey, ct);
            return roles.Select(r => ToDto(r, holds.GetValueOrDefault(r.Key))).ToList();
        }, ct);

    public Task<RoleBriefDto> BriefAsync(string key, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) =>
        {
            var normalized = Normalize(key);
            var role = await db.Roles.SingleOrDefaultAsync(r => r.Key == normalized, ct) ?? throw Fail.NotFound("Role", normalized);
            return new RoleBriefDto(role.Key, role.IsValidator, role.BriefMd, role.UpdatedAt);
        }, ct);

    public Task<RoleDto> TakeAsync(Caller caller, string key, CancellationToken ct = default)
    {
        var agentId = caller.RequireAgent();
        return ledger.MutateAsync(caller, async m =>
        {
            var normalized = Normalize(key);
            var role = await m.Db.Roles.SingleOrDefaultAsync(r => r.Key == normalized, ct) ?? throw Fail.NotFound("Role", normalized);
            var hold = await m.Db.RoleHolds.Include(h => h.Agent).SingleOrDefaultAsync(h => h.RoleKey == normalized, ct);

            if (hold is not null && hold.AgentId != agentId && hold.LeaseExpires > m.Now)
                throw Fail.Conflict("role_held", $"Role '{normalized}' is held by '{hold.Agent?.Name}' until {hold.LeaseExpires:O}.");

            // Taking a role you already hold just renews it; no ledger noise.
            var isRenewal = hold is not null && hold.AgentId == agentId && hold.LeaseExpires > m.Now;
            var previous = hold is not null && hold.AgentId != agentId ? hold.Agent?.Name : null;
            if (hold is null)
            {
                hold = new RoleHold { RoleKey = normalized, AgentId = agentId };
                m.Db.RoleHolds.Add(hold);
            }
            if (!isRenewal)
            {
                hold.AgentId = agentId;
                hold.AcquiredAt = m.Now;
                m.Record("role.taken", payload: new { role = normalized, agent = caller.Name, tookOverFrom = previous });
            }
            hold.LeaseExpires = m.Now + leases.RoleLease;
            hold.Agent = await m.Db.Agents.SingleAsync(a => a.Id == agentId, ct);
            return ToDto(role, hold);
        }, ct);
    }

    public Task ReleaseAsync(Caller caller, string key, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var normalized = Normalize(key);
            var hold = await m.Db.RoleHolds.Include(h => h.Agent).SingleOrDefaultAsync(h => h.RoleKey == normalized, ct)
                ?? throw Fail.Rule("role_not_held", $"Role '{normalized}' is not held by anyone.");
            if (!caller.IsFounder && hold.AgentId != caller.AgentId)
                throw Fail.Rule("not_holder", $"Role '{normalized}' is held by '{hold.Agent?.Name}'; only the holder or the founder may release it.");
            m.Db.RoleHolds.Remove(hold);
            m.Record("role.released", payload: new { role = normalized, agent = hold.Agent?.Name });
        }, ct);

    /// <summary>Removes holds whose lease ran out, so the role shows as open.</summary>
    public Task<int> SweepExpiredHoldsAsync(CancellationToken ct = default) =>
        ledger.MutateAsync(Caller.System, async m =>
        {
            var lapsed = await m.Db.RoleHolds.Include(h => h.Agent).Where(h => h.LeaseExpires <= m.Now).ToListAsync(ct);
            foreach (var hold in lapsed)
            {
                m.Db.RoleHolds.Remove(hold);
                m.Record("role.expired", payload: new { role = hold.RoleKey, agent = hold.Agent?.Name });
            }
            return lapsed.Count;
        }, ct);

    private static string Normalize(string? key) => (key ?? "").Trim().ToLowerInvariant();

    private static RoleDto ToDto(Role role, RoleHold? hold) =>
        new(role.Key, role.IsValidator, hold?.Agent?.Name, hold?.AcquiredAt, hold?.LeaseExpires, role.BriefMd.Length > 0, role.UpdatedAt);
}
