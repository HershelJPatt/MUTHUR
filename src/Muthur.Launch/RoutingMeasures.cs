using Muthur.Contracts;

namespace Muthur.Launch;

/// <summary>
/// What one (harness, model) did on units of one work-kind, from the ledger's worker rows and failed verdicts.
/// </summary>
/// <param name="Comparable">Worker runs on this kind of work: the sample every rate below is drawn from.</param>
/// <param name="Starts">Runs where a process actually ran; a launch the hub or the account refused is not an outcome.</param>
/// <param name="FalseApprovals">Done units whose task later failed validation in the window.</param>
/// <param name="DoneRate">Done per start; null before the first start.</param>
/// <param name="FalseApprovalRate">False approvals per done unit; zero when nothing was done and so nothing was falsely approved.</param>
/// <param name="MedianSeconds">Of the started runs.</param>
/// <param name="MeanCostUsd">Over the runs that reported a cost; null when none did.</param>
public sealed record RoutingMeasure(int Comparable, int Starts, int Done, int Blocked, int FalseApprovals,
    decimal? DoneRate, decimal? BlockedRate, decimal? FalseApprovalRate, double? MedianSeconds, decimal? MeanCostUsd);

public static class RoutingMeasures
{
    /// <summary>Runs on one harness and model, one per run id: a run reported twice counts once, by its last word.</summary>
    public static IReadOnlyList<WorkerRunDto> Comparable(IEnumerable<WorkerRunDto> runs, string harness, string model, string workKind, string tier = "implementer")
    {
        var worker = harness + "/" + model;
        var mine = runs.Where(r => r.Tier == tier && r.Worker == worker && r.WorkKind == workKind).ToList();
        return [.. mine.Where(r => r.RunId is null).Concat(mine.Where(r => r.RunId is not null).GroupBy(r => r.RunId).Select(g => g.MaxBy(r => r.At)!))];
    }

    public static RoutingMeasure Measure(IReadOnlyList<WorkerRunDto> runs, IEnumerable<ValidationFailureDto> failures)
    {
        var verdicts = failures.ToList();
        var started = runs.Where(r => r.FailureKind != "launch_unavailable").ToList();
        var done = started.Where(r => r.Success).ToList();
        var blocked = started.Count(r => r.Status == "blocked" || r.FailureKind == "worker_blocked");
        var falseApprovals = done.Count(r => r.Task is { } task && verdicts.Any(v => v.Task == task && v.At > r.At));
        var priced = runs.Where(r => r.CostUsd is not null).Select(r => r.CostUsd!.Value).ToList();
        return new(runs.Count, started.Count, done.Count, blocked, falseApprovals,
            Rate(done.Count, started.Count), Rate(blocked, started.Count),
            done.Count == 0 ? (started.Count == 0 ? null : 0m) : Rate(falseApprovals, done.Count),
            Median(started.Select(r => (double)r.Seconds)), priced.Count == 0 ? null : priced.Average());
    }

    private static decimal? Rate(int part, int whole) => whole == 0 ? null : Math.Round((decimal)part / whole, 4);

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return null;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
