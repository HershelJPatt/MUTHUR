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
            // Judged after IsValidator has been applied, so "--validator true --holders 3" works in one call.
            if (request.Holders is { } holders)
            {
                if (holders < 1)
                    throw Fail.Rule("invalid_holders", "A role needs at least one holder.");
                if (holders > 1 && !role.IsValidator)
                    throw Fail.Rule("single_holder", $"Only a validator role may have more than one holder: '{key}' is a standing post, and who holds it has to have one answer.");
                // Lowering the ceiling evicts nobody: existing holds stand until they lapse or are released.
                role.Holders = holders;
            }
            role.UpdatedAt = m.Now;
            m.Record(created ? "role.defined" : "role.updated", payload: new { role = key, role.IsValidator, role.Holders, briefChars = role.BriefMd.Length });
            return ToDto(role, []);
        }, ct);
    }

    public Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<RoleDto>>(async (db, now) =>
        {
            var roles = await db.Roles.OrderBy(r => r.Key).ToListAsync(ct);
            var holds = await db.RoleHolds.Include(h => h.Agent).Where(h => h.LeaseExpires > now).ToListAsync(ct);
            var byRole = holds.GroupBy(h => h.RoleKey).ToDictionary(g => g.Key, g => (IReadOnlyList<RoleHold>)[.. g]);
            return roles.Select(r => ToDto(r, byRole.GetValueOrDefault(r.Key, []))).ToList();
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
            // Lapsed rows the sweeper has not reaped yet still occupy this agent's slot in the key, so load them all.
            var all = await m.Db.RoleHolds.Include(h => h.Agent).Where(h => h.RoleKey == normalized).ToListAsync(ct);
            var live = Ordered(all.Where(h => h.LeaseExpires > m.Now));
            var mine = all.SingleOrDefault(h => h.AgentId == agentId);

            // Taking a role you already hold just renews it; no ledger noise.
            var isRenewal = mine is not null && mine.LeaseExpires > m.Now;
            // Branching on the live count rather than the capacity: a founder may lower a capacity below the
            // holds already standing, and naming one of the two agents in your way is worse than naming none.
            if (!isRenewal && live.Count >= role.Holders)
                throw Fail.Conflict("role_held", live.Count == 1
                    ? $"Role '{normalized}' is held by '{live[0].Agent?.Name}' until {live[0].LeaseExpires:O}."
                    : $"Role '{normalized}' is full: {live.Count} of {role.Holders} held by {string.Join(", ", live.Select(h => h.Agent?.Name))}.");

            if (mine is null)
            {
                mine = new RoleHold { RoleKey = normalized, AgentId = agentId };
                m.Db.RoleHolds.Add(mine);
            }
            if (!isRenewal)
            {
                mine.AcquiredAt = m.Now;
                m.Record("role.taken", payload: new { role = normalized, agent = caller.Name, holders = live.Count + 1, capacity = role.Holders });
            }
            mine.LeaseExpires = m.Now + leases.RoleLease;
            mine.Agent = await m.Db.Agents.SingleAsync(a => a.Id == agentId, ct);
            return ToDto(role, [.. live.Where(h => h.AgentId != agentId), mine]);
        }, ct);
    }

    /// <summary>Releases the caller's hold; the founder's release clears every hold on the role.</summary>
    public Task ReleaseAsync(Caller caller, string key, CancellationToken ct = default) =>
        ledger.MutateAsync(caller, async m =>
        {
            var normalized = Normalize(key);
            var holds = Ordered(await m.Db.RoleHolds.Include(h => h.Agent).Where(h => h.RoleKey == normalized).ToListAsync(ct));
            if (holds.Count == 0)
                throw Fail.Rule("role_not_held", $"Role '{normalized}' is not held by anyone.");

            var releasing = caller.IsFounder ? holds : holds.Where(h => h.AgentId == caller.AgentId).ToList();
            if (releasing.Count == 0)
                throw Fail.Rule("not_holder", $"Role '{normalized}' is held by '{string.Join("', '", holds.Select(h => h.Agent?.Name))}'; only a holder or the founder may release it.");
            foreach (var hold in releasing)
            {
                m.Db.RoleHolds.Remove(hold);
                m.Record("role.released", payload: new { role = normalized, agent = hold.Agent?.Name });
            }
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

    /// <summary>Oldest hold first, so a list of holders reads in the order they arrived.</summary>
    private static List<RoleHold> Ordered(IEnumerable<RoleHold> holds) =>
        [.. holds.OrderBy(h => h.AcquiredAt).ThenBy(h => h.Agent?.Name, StringComparer.Ordinal)];

    private static RoleDto ToDto(Role role, IReadOnlyList<RoleHold> holds) =>
        new(role.Key, role.IsValidator, role.Holders,
            [.. Ordered(holds).Select(h => new RoleHolderDto(h.Agent?.Name ?? "", h.AcquiredAt, h.LeaseExpires))],
            role.BriefMd.Length > 0, role.UpdatedAt);
}
