using Muthur.Cli.Commands;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Tests;

/// <summary>
/// The catalog is a file the founder edits, and `worker run` is the path that consumes it most. A tier entry
/// that says how hard its model should think has to survive the whole way — tier DTO, candidate, request —
/// or the knob works for validator sessions and silently does nothing for the workers that build the units.
/// </summary>
public sealed class WorkerReasoningEffortTests
{
    private static IReadOnlyList<TierDto> Catalog(string? effort) =>
        [new TierDto("implementer", [new HarnessCandidateDto("codex", "gpt-6-astra", "chatgpt-subscription", false, null, effort)])];

    private static WorkerRequest RequestFrom(string? effort)
    {
        var candidate = Assert.Single(WorkerCommands.Candidates(Catalog(effort), harness: null));
        return WorkerCommands.RequestFor(candidate, "C:/repo/.worktrees/w1", "do the unit", "C:/repo/.git", ["dotnet *"], "C:/scratch");
    }

    [Fact]
    public void A_tier_entry_that_says_how_hard_to_think_reaches_the_worker() =>
        Assert.Equal("high", RequestFrom("high").ReasoningEffort);

    [Fact]
    public void A_tier_entry_that_says_nothing_leaves_the_harness_its_own_default() =>
        Assert.Null(RequestFrom(null).ReasoningEffort);

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
