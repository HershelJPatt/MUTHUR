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
