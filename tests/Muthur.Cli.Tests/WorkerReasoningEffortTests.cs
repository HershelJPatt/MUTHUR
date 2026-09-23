using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Tests;

/// <summary>
/// The catalog is a file the founder edits, and `worker run` is the path that consumes it most. A tier entry
/// that says how hard its model should think has to survive the whole way — tier DTO, candidate, request —
/// as the ceiling on what the spec's work-kind asks for, and an explicit --effort has to beat them both. The
/// report then records what was sent, never what the catalog said.
/// </summary>
public sealed class WorkerReasoningEffortTests
{
    private static IReadOnlyList<TierDto> Catalog(string? effort) =>
        [new TierDto("implementer", [new HarnessCandidateDto("codex", "gpt-6-astra", "chatgpt-subscription", false, null, effort)])];

    private static WorkerRequest RequestFrom(string? ceiling, string workKind = "unknown", string? effort = null)
    {
        var candidate = Assert.Single(WorkerCommands.Candidates(Catalog(ceiling), harness: null));
        return WorkerCommands.RequestFor(candidate, "C:/repo/.worktrees/w1", "do the unit", "C:/repo/.git", ["dotnet *"], "C:/scratch", workKind, effort);
    }

    [Fact]
    public void A_tier_entry_that_says_how_hard_to_think_is_the_ceiling()
    {
        Assert.Equal("high", RequestFrom("high", "complex-debugging").ReasoningEffort);
        Assert.Equal("medium", RequestFrom("medium", "complex-debugging").ReasoningEffort);
        Assert.Equal("low", RequestFrom("high", "mechanical").ReasoningEffort);
    }

    [Fact]
    public void A_tier_entry_that_says_nothing_leaves_the_work_kind_to_decide()
    {
        Assert.Equal("medium", RequestFrom(null).ReasoningEffort);
        Assert.Equal("medium", RequestFrom(null, "general").ReasoningEffort);
        Assert.Equal("high", RequestFrom(null, "ui-interaction").ReasoningEffort);
    }

    [Fact]
    public void An_explicit_effort_overrides_the_work_kind_and_the_ceiling()
    {
        Assert.Equal("high", RequestFrom("low", "mechanical", "high").ReasoningEffort);
        var candidate = Assert.Single(WorkerCommands.Candidates(Catalog("low"), harness: null));
        Assert.Equal("high", WorkerCommands.EffortFor(candidate, "mechanical", "high"));
        Assert.Equal("low", WorkerCommands.EffortFor(candidate, "ui-interaction", null));
    }

    [Fact]
    public void The_run_command_takes_effort_and_refuses_a_level_it_does_not_know()
    {
        var root = new RootCommand("test");
        WorkerCommands.AddTo(root);
        Assert.Empty(root.Parse(["worker", "run", "--spec", "specs/T-1.md", "--task", "T-1", "--effort", "low"]).Errors);
        Assert.True(WorkerCommands.ValidEffort(null));
        Assert.True(WorkerCommands.ValidEffort("medium"));
        Assert.False(WorkerCommands.ValidEffort("max"));
        Assert.False(WorkerCommands.ValidEffort("High"));
    }

    [Fact]
    public void An_account_out_of_quota_is_still_skipped()
    {
        IReadOnlyList<TierDto> catalog = [new TierDto("implementer", [
            new HarnessCandidateDto("codex", "gpt-6-astra", "chatgpt-subscription", true, DateTimeOffset.UnixEpoch, "high"),
            new HarnessCandidateDto("claude", "opus", "claude-subscription", false, null, null)])];

        var candidate = Assert.Single(WorkerCommands.Candidates(catalog, harness: null));
        Assert.Equal("claude", candidate.Harness);
        Assert.Null(candidate.ReasoningEffort);
    }
}
