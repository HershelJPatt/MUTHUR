using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class RoutingTests : IDisposable
{
    private readonly HubFactory hub = new();
    private readonly TestRepo repo = new();

    public void Dispose()
    {
        hub.Dispose();
        repo.Dispose();
    }

    [Fact]
    public async Task Recommendations_are_owner_attributed_immutable_and_do_not_staff()
    {
        await hub.AddProjectAsync();
        var owner = await hub.RegisterAgentAsync("routing-owner");
        var task = await owner.AddTaskAsync("routing context", "demo");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        var now = hub.Clock.GetUtcNow();
        var report = new RoutingReport(1, "measured-quality-v1", now, task.Id, new string('a', 40), new string('b', 40), "general",
            now.AddHours(-1), now, 1, [], [], null, "No eligible candidates", null, 0, ["Capacity admission remains required"]);
        var path = "/api/v1/routing/" + task.Id;
        Assert.Equal(HttpStatusCode.Unauthorized, (await hub.Founder().PostAsJsonAsync(path, report)).StatusCode);
        var response = await owner.PostAsJsonAsync(path, report);
        response.EnsureSuccessStatusCode();
        var recorded = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RoutingSnapshot))!;
        Assert.Equal("routing-owner", recorded.Actor);
        Assert.True(recorded.Sequence > 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(path, report with { SchemaVersion = 2 })).StatusCode);
        var rows = await owner.GetFromJsonAsync(path, MuthurJsonContext.Default.IReadOnlyListRoutingSnapshot);
        Assert.Equal(recorded.Sequence, Assert.Single(rows!).Sequence);
        Assert.DoesNotContain((await owner.GetTaskAsync(task.Id)).Events, e => e.Type == "conductor.staffing");
        var history = await owner.GetFromJsonAsync("/api/v1/routing/history?hours=1", MuthurJsonContext.Default.RoutingHistory);
        Assert.NotNull(history);
        Assert.Empty(history.Runs);
        Assert.NotNull(history.ValidationFailures);
        Assert.Empty(history.ValidationFailures);
    }

    [Fact]
    public async Task A_disabled_candidate_is_never_staffed()
    {
        // The shipped catalog lists pi in both implementer tiers, switched off until its row decides the seat.
        var shipped = await hub.Founder().GetFromJsonAsync(Routes.Tiers, MuthurJsonContext.Default.IReadOnlyListTierDto);
        Assert.Contains("\"harness\": \"pi\"", File.ReadAllText(Path.Combine(hub.DataDir, MuthurEnvironment.HarnessFile)));
        Assert.DoesNotContain(shipped!.SelectMany(t => t.Candidates), c => c.Harness == "pi");
        Assert.Empty(Assert.Single(shipped!, t => t.Tier == "local-implementer").Candidates);

        // Leading the tier does not change that: the conductor's validator staffing reads the same catalog.
        File.WriteAllText(Path.Combine(hub.DataDir, MuthurEnvironment.HarnessFile), """
            { "tiers": { "implementer": [
                { "harness": "pi", "model": "gemma4:26b", "account": "local", "enabled": false },
                { "harness": "claude", "model": "sonnet", "account": "a" } ] } }
            """);
        var (tier, candidates) = await ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(hub.Services).CandidatesAsync(null, CancellationToken.None);
        Assert.Equal("implementer", tier);
        Assert.Equal(["claude"], candidates.Select(c => c.Harness));
    }

    /// <summary>
    /// The whole loop the CLI's `routing report` runs, with the git and capability steps taken out: worker runs and
    /// failed verdicts land in the ledger, the history window hands them back, the measures are taken per
    /// (harness, model, work-kind), and a report built from them ranks by measured quality rather than catalog
    /// order once every candidate has five comparable outcomes.
    /// </summary>
    [Fact]
    public async Task Measured_rates_come_from_worker_runs_and_later_failed_verdicts_and_rank_the_report()
    {
        await hub.AddProjectAsync(repoPath: repo.Path, validators: ["win-validator"]);
        (await hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "# win-validator\nDrive it."))).EnsureSuccessStatusCode();
        var owner = await hub.RegisterAgentAsync("routing-owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(repo.WriteSpec()))).EnsureSuccessStatusCode();
        var other = await owner.AddTaskAsync("Another unit of the same kind");

        // Candidate 0 (catalog order would pick it): six mechanical units, five done, one that a validator later
        // rejected, and one blocked; costs reported. Candidate 1: five done, none rejected, none priced.
        for (var i = 0; i < 5; i++)
            await RunAsync(owner, i < 4 ? other.Id : task.Id, "codex", "gpt", $"unit-{i}", true, 60 + i * 10, 0.5m, "mechanical", "done");
        await RunAsync(owner, other.Id, "codex", "gpt", "unit-5", false, 30, 0.1m, "mechanical", "blocked", "worker_blocked");
        for (var i = 0; i < 5; i++)
            await RunAsync(owner, other.Id, "claude", "opus", $"unit-{i}", true, 200, null, "mechanical", "done");
        // A refused launch and a unit of another kind are not comparable outcomes.
        await RunAsync(owner, other.Id, "claude", "opus", "unit-x", false, 0, null, "mechanical", null, "launch_unavailable");
        await RunAsync(owner, other.Id, "claude", "opus", "unit-y", false, 900, null, "general", "spec-problem", "spec_problem");

        hub.Clock.Advance(TimeSpan.FromMinutes(5));
        repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        var checker = await hub.RegisterAgentAsync("checker");
        (await checker.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        (await checker.PostActionAsync(task.Id, "fail", new VerdictRequest("win-validator", "broken: reproduce with dotnet test",
            SubjectId: (await checker.GetTaskAsync(task.Id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();

        var history = (await owner.GetFromJsonAsync("/api/v1/routing/history?hours=336", MuthurJsonContext.Default.RoutingHistory))!;
        var failure = Assert.Single(history.ValidationFailures!);
        Assert.Equal(task.Id, failure.Task);
        Assert.Equal(13, history.Runs.Count);

        var codex = RoutingMeasures.Measure(RoutingMeasures.Comparable(history.Runs, "codex", "gpt", "mechanical"), history.ValidationFailures!);
        Assert.Equal(6, codex.Comparable);
        Assert.Equal(6, codex.Starts);
        Assert.Equal(5, codex.Done);
        Assert.Equal(1, codex.Blocked);
        Assert.Equal(1, codex.FalseApprovals);
        Assert.Equal(0.8333m, codex.DoneRate);
        Assert.Equal(0.1667m, codex.BlockedRate);
        Assert.Equal(0.2m, codex.FalseApprovalRate);
        Assert.Equal(75, codex.MedianSeconds);
        Assert.Equal(2.6m / 6, codex.MeanCostUsd);

        var claude = RoutingMeasures.Measure(RoutingMeasures.Comparable(history.Runs, "claude", "opus", "mechanical"), history.ValidationFailures!);
        Assert.Equal(6, claude.Comparable);
        Assert.Equal(5, claude.Starts);
        Assert.Equal(0m, claude.FalseApprovalRate);
        Assert.Equal(1m, claude.DoneRate);
        Assert.Equal(200, claude.MedianSeconds);
        Assert.Null(claude.MeanCostUsd);

        RoutingCandidate Row(int position, string harness, string model, RoutingMeasure m) =>
            new(harness, model, "acct", null, position, true, true, "fixture", m.Comparable, m.Starts, m.FalseApprovalRate, m.DoneRate, m.BlockedRate, m.MedianSeconds,
                m.Done, m.Comparable - m.Done, [], [], m.MeanCostUsd);
        var rows = new[] { Row(0, "codex", "gpt", codex), Row(1, "claude", "opus", claude) };
        var chosen = RoutingPolicy.Recommend(rows);
        Assert.Equal(1, chosen.Position);
        Assert.StartsWith("Measured quality", chosen.Reason, StringComparison.Ordinal);

        var now = hub.Clock.GetUtcNow();
        var report = new RoutingReport(1, "measured-quality-v1", now, task.Id, new string('a', 40), new string('b', 40), "mechanical",
            history.From, history.To, history.LedgerBoundary, [], rows, chosen.Position, chosen.Reason, null, history.PendingValidation, []);
        (await owner.PostAsJsonAsync("/api/v1/routing/" + task.Id, report)).EnsureSuccessStatusCode();
        var recorded = Assert.Single((await owner.GetFromJsonAsync("/api/v1/routing/" + task.Id, MuthurJsonContext.Default.IReadOnlyListRoutingSnapshot))!);
        Assert.Equal(1, recorded.Report.RecommendedCatalogPosition);
        Assert.Equal(2.6m / 6, recorded.Report.Candidates[0].MeanCostUsd);
    }

    private static async Task RunAsync(HttpClient owner, string task, string harness, string model, string unit, bool success, int seconds, decimal? cost,
        string workKind, string? status, string? failureKind = null) =>
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(task, "implementer", harness, model, "acct", "worker/" + unit, unit, success, seconds, cost,
            RunId: Guid.NewGuid().ToString("n"), Status: status, FailureKind: failureKind, WorkKind: workKind, PolicyVersion: "catalog-order-v1"))).EnsureSuccessStatusCode();
}
