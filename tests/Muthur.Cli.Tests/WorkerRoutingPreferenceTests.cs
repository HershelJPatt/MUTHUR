using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Tests;

/// <summary>
/// `worker run` asks the hub for the task's recorded recommendation and tries that candidate first. Advisory
/// only: a recommendation it cannot match, or none at all, leaves the catalog order and the catalog policy.
/// </summary>
public sealed class WorkerRoutingPreferenceTests
{
    private static readonly IReadOnlyList<HarnessCandidate> Catalog =
    [
        new("codex", "gpt", "chatgpt-subscription", "high"),
        new("claude", "opus", "claude-subscription", null),
        new("claude", "sonnet", "claude-subscription", null),
    ];

    private static RoutingReport Report(int? position, params RoutingCandidate[] candidates) =>
        new(1, "measured-quality-v1", DateTimeOffset.UnixEpoch, "T-1", new string('a', 40), new string('b', 40), "mechanical",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, [], candidates, position, "fixture", null, 0, []);

    private static RoutingCandidate Row(int position, string harness, string model, string account) =>
        new(harness, model, account, null, position, true, true, "fixture", 0, null, null, null, null, null, 0, 0, [], []);

    private static string Snapshots(params RoutingReport[] reports) =>
        JsonSerializer.Serialize<IReadOnlyList<RoutingSnapshot>>(reports.Select((r, i) => new RoutingSnapshot(i + 1, DateTimeOffset.UnixEpoch, "owner", r)).ToList(),
            MuthurJsonContext.Default.IReadOnlyListRoutingSnapshot);

    [Fact]
    public void The_recommended_candidate_moves_to_the_front_and_the_report_names_the_policy()
    {
        var report = Report(1, Row(0, "codex", "gpt", "chatgpt-subscription"), Row(1, "claude", "opus", "claude-subscription"));

        var (ordered, policy) = WorkerCommands.Prefer(Catalog, report);

        Assert.Equal(["claude/opus", "codex/gpt", "claude/sonnet"], ordered.Select(c => c.Harness + "/" + c.Model));
        Assert.Equal("measured-quality-v1", policy);
    }

    [Fact]
    public void A_recommendation_is_matched_by_identity_not_by_position()
    {
        // The catalog was edited since: position 1 now names a model the report never measured.
        var report = Report(1, Row(0, "codex", "gpt", "chatgpt-subscription"), Row(1, "claude", "haiku", "claude-subscription"));

        var (ordered, policy) = WorkerCommands.Prefer(Catalog, report);

        Assert.Equal(Catalog, ordered);
        Assert.Equal("catalog-order-v1", policy);
    }

    [Fact]
    public void The_newest_snapshot_with_a_position_wins_and_none_leaves_catalog_order()
    {
        var older = Report(0, Row(0, "codex", "gpt", "chatgpt-subscription"));
        var newer = Report(2, Row(0, "codex", "gpt", "chatgpt-subscription"), Row(2, "claude", "sonnet", "claude-subscription"));
        var undecided = Report(null);

        var latest = WorkerCommands.LatestRecommendation(Snapshots(older, newer, undecided));

        Assert.NotNull(latest);
        Assert.Equal(2, latest.RecommendedCatalogPosition);
        Assert.Null(WorkerCommands.LatestRecommendation(Snapshots(undecided)));
        Assert.Null(WorkerCommands.LatestRecommendation("[]"));
    }
}
