using Muthur.Contracts;

namespace Muthur.Launch.Tests;

public sealed class RoutingPolicyTests
{
    private static RoutingCandidate Candidate(int position, decimal falseGreen = 0, decimal success = 1) =>
        new("fixture", "model-" + position, "account", null, position, true, true, "Exact identity fixture", 10, 10,
            falseGreen, success, 0, 100, 10, 0, ["independent fixture grader"], []);

    [Fact]
    public void Capability_and_quota_exclusions_precede_observed_quality()
    {
        var rows = new[] { Candidate(0) with { Eligible = false }, Candidate(1) with { Available = false }, Candidate(2, .1m, .8m) };
        Assert.Equal(2, RoutingPolicy.Recommend(rows).Position);
        Assert.Null(RoutingPolicy.Recommend(rows.Take(2).ToList()).Position);
    }

    [Fact]
    public void Unknown_or_small_samples_fall_back_without_relabeling_reported_success()
    {
        Assert.Equal(0, RoutingPolicy.Recommend([Candidate(0, .8m, .2m), Candidate(1) with { ComparableOutcomes = 4 }]).Position);
        Assert.Equal(0, RoutingPolicy.Recommend([Candidate(0, .8m, .2m), Candidate(1) with { FalseApprovalRate = null }]).Position);
        Assert.Equal(0, RoutingPolicy.Recommend([Candidate(0), Candidate(1) with { ActualStarts = null }]).Position);
    }

    [Fact]
    public void Independent_false_approval_precedes_speed_and_reported_success()
    {
        Assert.Equal(1, RoutingPolicy.Recommend([Candidate(0, .2m) with { EndToEndSeconds = 1 }, Candidate(1, 0, .7m)]).Position);
        Assert.Equal(0, RoutingPolicy.Recommend([Candidate(0), Candidate(1)]).Position);
    }

    [Fact]
    public void Cost_breaks_a_tie_before_catalog_order_and_an_unpriced_candidate_sorts_last()
    {
        Assert.Equal(1, RoutingPolicy.Recommend([Candidate(0) with { MeanCostUsd = 0.9m }, Candidate(1) with { MeanCostUsd = 0.4m }]).Position);
        Assert.Equal(1, RoutingPolicy.Recommend([Candidate(0), Candidate(1) with { MeanCostUsd = 0.4m }]).Position);
        Assert.Equal(0, RoutingPolicy.Recommend([Candidate(0), Candidate(1)]).Position);
        // Quality still precedes price: the cheaper candidate loses on false approvals.
        Assert.Equal(0, RoutingPolicy.Recommend([Candidate(0) with { MeanCostUsd = 2m }, Candidate(1, .1m) with { MeanCostUsd = 0.1m }]).Position);
    }

    [Fact]
    public void Measures_count_starts_done_blocked_and_later_failed_verdicts()
    {
        var at = DateTimeOffset.UnixEpoch;
        WorkerRunDto Run(string task, bool success, int seconds, decimal? cost, string? status = null, string? failure = null, string kind = "mechanical", string? runId = null) =>
            new(task, "implementer", "fixture/model", "acct", "unit", seconds, cost, success, at, RunId: runId, Status: status, FailureKind: failure, WorkKind: kind);
        var runs = new[]
        {
            Run("T-1", true, 100, 1m), Run("T-2", true, 300, 3m), Run("T-3", false, 50, null, "blocked", "worker_blocked"),
            Run("T-4", false, 0, null, null, "launch_unavailable"), Run("T-5", true, 200, null, kind: "general"),
            Run("T-6", false, 10, null, runId: "r"), Run("T-6", true, 20, null, runId: "r") with { At = at.AddMinutes(1) },
        };
        var comparable = RoutingMeasures.Comparable(runs, "fixture", "model", "mechanical");
        Assert.Equal(5, comparable.Count);
        var measure = RoutingMeasures.Measure(comparable, [new("T-1", at.AddHours(1)), new("T-2", at.AddHours(-1))]);
        Assert.Equal((5, 4, 3, 1, 1), (measure.Comparable, measure.Starts, measure.Done, measure.Blocked, measure.FalseApprovals));
        Assert.Equal(0.75m, measure.DoneRate);
        Assert.Equal(0.25m, measure.BlockedRate);
        Assert.Equal(0.3333m, measure.FalseApprovalRate);
        Assert.Equal(75, measure.MedianSeconds);
        Assert.Equal(2m, measure.MeanCostUsd);
        var nothing = RoutingMeasures.Measure([], []);
        Assert.Null(nothing.DoneRate);
        Assert.Null(nothing.FalseApprovalRate);
        Assert.Null(nothing.MedianSeconds);
    }

    [Theory]
    [InlineData("mechanical", null, "low")]
    [InlineData("general", null, "medium")]
    [InlineData("unknown", null, "medium")]
    [InlineData("complex-debugging", null, "high")]
    [InlineData("ui-interaction", null, "high")]
    [InlineData("ui-interaction", "medium", "medium")]
    [InlineData("complex-debugging", "low", "low")]
    [InlineData("mechanical", "high", "low")]
    [InlineData("general", "xhigh", "xhigh")]
    public void Effort_follows_the_work_kind_under_the_catalog_ceiling(string kind, string? ceiling, string expected) =>
        Assert.Equal(expected, RoutingPolicy.EffortFor(kind, new HarnessCandidate("fixture", "model", "account", ceiling)));

    [Theory]
    [InlineData("# old spec", "unknown")]
    [InlineData("work-kind: mechanical", "mechanical")]
    [InlineData("work-kind: ui-interaction\nwork-kind: ui-interaction", "ui-interaction")]
    public void Classification_is_explicit_and_legacy_compatible(string text, string expected) => Assert.Equal(expected, RoutingPolicy.WorkKind(text));

    [Theory]
    [InlineData("work-kind: cheap")]
    [InlineData("work-kind: mechanical\nwork-kind: general")]
    public void Conflicting_or_unknown_classification_fails(string text) => Assert.Throws<WorkerDispatchException>(() => RoutingPolicy.WorkKind(text));
}
