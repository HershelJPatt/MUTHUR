using Muthur.Contracts;
using Muthur.Core;
using Muthur.Launch;

namespace Muthur.Server.Services;

public sealed class DoctorHarnessCheck(AgentService agents, HarnessService harnesses, MuthurOptions options) : IDoctorCheck
{
    public async Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
    {
        var kits = KitDirectory.Harnesses(KitDirectory.Locate(options.KitDir));
        if (kits is null) return [];

        IReadOnlyList<TierDto> tiers;
        try
        {
            tiers = await harnesses.TiersAsync(ct: ct);
        }
        catch (MuthurException)
        {
            // Source diagnostics belong to DoctorAgentCheck.
            return [];
        }

        var standing = (await agents.ListAsync(ct)).Where(a => !a.ConductorStaffed)
            .Select(a => a.Harness).ToHashSet(StringComparer.Ordinal);
        return [.. tiers.SelectMany(t => t.Candidates).Select(c => c.Harness)
            .Distinct(StringComparer.Ordinal)
            .Where(h => !kits.Contains(h, StringComparer.Ordinal) && !standing.Contains(h))
            .Select(h => new CheckDto("harness", h, CheckStatus.Warn,
                $"Harness '{h}' has a tier entry in harnesses.json but no kit, so a session started on it gets no procedures. "
                + $"Add kit/{h}/kit.json, or correct the spelling in harnesses.json."))];
    }
}
