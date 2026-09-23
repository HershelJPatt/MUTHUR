using Muthur.Contracts;
using Muthur.Core;
using Muthur.Launch;
using Muthur.Server.Components.Shared;

namespace Muthur.Server.Services;

/// <summary>
/// Whether every standing agent runs on a harness this organization actually has: a kit to install procedures
/// from, an entry in the tier catalog, or both.
/// <para>
/// Conductor-staffed sessions are left out. Their harness came out of the tier catalog moments earlier, so by
/// construction it cannot be a typo, and a busy hub would bury the standing roster under them.
/// </para>
/// <para>
/// A fail is only ever reached when both sources were read and both said no. A source that could not be read
/// costs this check its confidence, never its honesty — the worst verdict available then is a warn.
/// </para>
/// </summary>
public sealed class DoctorAgentCheck(AgentService agents, HarnessService harnesses, MuthurOptions options) : IDoctorCheck
{
    public async Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
    {
        var standing = (await agents.ListAsync(ct)).Where(a => !a.ConductorStaffed).ToList();
        if (standing.Count == 0) return [];

        var located = KitDirectory.Locate(options.KitDir);
        var kits = KitDirectory.Harnesses(located);          // null => could not tell

        IReadOnlyList<string>? catalog;
        string? catalogProblem;
        try
        {
            catalog = [.. (await harnesses.TiersAsync(ct: ct))
                .SelectMany(t => t.Candidates).Select(c => c.Harness)
                .Distinct(StringComparer.Ordinal)];
            catalogProblem = null;
        }
        catch (MuthurException ex)   // catalog_invalid or catalog_unreadable; the Tiers panel reports the same thing
        {
            catalog = null;
            catalogProblem = ex.Message;
        }

        // Category "harness", not "agent": a source that would not be read is about neither agent in particular,
        // and it is worth saying only because a verdict below is about to be qualified by it.
        var checks = new List<CheckDto>();
        if (located is null)
            checks.Add(new CheckDto("harness", "kit", CheckStatus.Warn,
                "The kit directory was not found: $MUTHUR_KIT is unset or points nowhere, and there is no kit/ "
                + "beside the hub or beside its parent. Harnesses were checked against harnesses.json alone."));
        else if (kits is null)
            checks.Add(new CheckDto("harness", "kit", CheckStatus.Warn,
                $"The kit directory {located} could not be read. Harnesses were checked against harnesses.json alone."));

        if (catalog is null)
            checks.Add(new CheckDto("harness", MuthurEnvironment.HarnessFile, CheckStatus.Warn,
                $"{catalogProblem} Harnesses were checked against the kit directory alone."));

        checks.AddRange(standing.Select(a => Inspect(a, kits, catalog, context.Now)));
        return checks;
    }

    /// <summary>
    /// One agent against both sources. Each is three-valued — yes, no, or could not tell — so the nine answers
    /// are spelled out rather than folded together: which source was silent changes what may honestly be said.
    /// </summary>
    private static CheckDto Inspect(AgentDto agent, IReadOnlyList<string>? kits, IReadOnlyList<string>? catalog, DateTimeOffset now)
    {
        // Ordinal throughout: 'Claude' is not 'claude', and a check that pretended otherwise would hide the
        // class of typo it exists to catch.
        var harness = agent.Harness;
        bool? hasKit = kits?.Contains(KitDirectory.KitFor(harness), StringComparer.Ordinal);
        bool? inCatalog = catalog?.Contains(harness, StringComparer.Ordinal);

        return (hasKit, inCatalog) switch
        {
            (true, true) => Check(CheckStatus.Ok,
                $"Runs on harness '{harness}', which has a kit and a tier entry."),
            (true, false) => Check(CheckStatus.Ok,
                $"Runs on harness '{harness}', which has a kit but no entry in harnesses.json, so no tier can be staffed on it headlessly."),
            (true, null) => Check(CheckStatus.Ok,
                $"Runs on harness '{harness}', which has a kit."),
            (false, true) => Check(CheckStatus.Warn,
                $"Runs on harness '{harness}', which has a tier entry in harnesses.json but no kit, so a session started on it gets no procedures. "
                + $"Add kit/{harness}/kit.json, or correct the spelling in harnesses.json."),
            (false, false) => Typo(),
            (false, null) => Check(CheckStatus.Warn,
                $"Runs on harness '{harness}', which has no kit; harnesses.json could not be read, so whether a tier names it could not be settled."),
            (null, true) => Check(CheckStatus.Ok,
                $"Runs on harness '{harness}', which has a tier entry in harnesses.json."),
            (null, false) => Check(CheckStatus.Warn,
                $"Runs on harness '{harness}', which has no entry in harnesses.json; the kit directory could not be read, so whether it has a kit could not be settled."),
            (null, null) => Check(CheckStatus.Warn,
                $"Runs on harness '{harness}'. Neither the kit directory nor harnesses.json could be read, so whether it is a real harness could not be settled."),
        };

        // Both sources were read and both said no. A session that is still answering is a live defect; one that
        // has gone quiet may be a harness the founder removed after it last spoke.
        CheckDto Typo() =>
            agent.Status == AgentStatus.Live
                ? Check(CheckStatus.Fail,
                    $"Runs on harness '{harness}', which has no kit and no entry in harnesses.json: nothing can be installed for it "
                    + $"and no tier can staff it. Most likely a typo. {Known(kits, catalog)}")
                : Check(CheckStatus.Warn,
                    $"Last seen {Format.Age(agent.LastHeartbeat, now)} ago on harness '{harness}', which has no kit and no entry in "
                    + $"harnesses.json. Either a typo at registration or a harness since removed. {Known(kits, catalog)}");

        CheckDto Check(CheckStatus status, string detail) => new("agent", agent.Name, status, detail);
    }

    /// <summary>What this organization does have, said only where both sources were read and neither is null.</summary>
    private static string Known(IReadOnlyList<string>? kits, IReadOnlyList<string>? catalog)
    {
        var known = (kits ?? []).Concat(catalog ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return known.Count > 0
            ? $"This organization has: {string.Join(", ", known)}."
            : "Nothing here names a harness: neither the kit directory nor harnesses.json has one.";
    }
}
