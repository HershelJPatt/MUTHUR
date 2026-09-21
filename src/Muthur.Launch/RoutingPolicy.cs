using Muthur.Contracts;

namespace Muthur.Launch;

/// <summary>Recommendations never grant capacity, account access, or authority to start a process.</summary>
public static class RoutingPolicy
{
    public static string WorkKind(string specification)
    {
        var kinds = specification.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("work-kind:", StringComparison.Ordinal))
            .Select(l => l[10..].Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (kinds.Count == 0) return "unknown";
        if (kinds.Count != 1 || kinds[0] is not ("mechanical" or "complex-debugging" or "ui-interaction" or "general"))
            throw new WorkerDispatchException("invalid_work_kind", "Use one consistent work-kind: mechanical, complex-debugging, ui-interaction or general.");
        return kinds[0];
    }

    public static (int? Position, string Reason) Recommend(IReadOnlyList<RoutingCandidate> candidates)
    {
        var actionable = candidates.Where(c => c.Eligible && c.Available).OrderBy(c => c.CatalogPosition).ToList();
        if (actionable.Count == 0) return (null, "No immediately available capability-eligible candidate. No launch is authorized.");
        if (actionable.Any(c => c.ComparableOutcomes < 5 || c.ActualStarts is null or <= 0 || c.FalseApprovalRate is null
            || c.IndependentSuccessRate is null || c.ReworkRate is null || c.EndToEndSeconds is null))
            return (actionable[0].CatalogPosition, "Catalog-order fallback: fewer than five comparable independently observed outcomes or missing ranking dimensions.");
        var selected = actionable.OrderBy(c => c.FalseApprovalRate).ThenByDescending(c => c.IndependentSuccessRate)
            .ThenBy(c => c.ReworkRate).ThenBy(c => c.EndToEndSeconds).ThenBy(c => c.CatalogPosition).First();
        return (selected.CatalogPosition, "Measured quality: false approvals, independent success per actual start, rework, elapsed time, then stable catalog order.");
    }
}
