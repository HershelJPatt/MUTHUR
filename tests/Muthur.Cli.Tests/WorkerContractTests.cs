using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Launch;

namespace Muthur.Cli.Tests;

/// <summary>
/// `worker run` read `core/implementer.md` whatever the tier, so a mastermind was staffed for its judgment and
/// then handed the one contract that tells it not to redesign anything. These compose the prompt the way
/// `worker run` does - against this repository's own kit - and read back what each tier would actually be given.
/// </summary>
public sealed class WorkerContractTests
{
    private static string Kit =>
        Path.Combine(
            Path.GetDirectoryName(
                ProjectContext.FindFile(AppContext.BaseDirectory)
                ?? throw new InvalidOperationException(
                    $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}."))!,
            "kit");

    /// <summary>A heading only `core/specialist.md` carries, and the boundary only `core/implementer.md` carries.</summary>
    private const string SpecialistMarker = "## When the subtree is bigger than you";
    private const string ImplementerMarker = "**The spec is frozen.**";

    private static async Task<string> PromptFor(string tier) =>
        WorkerPrompt.Compose(
            await WorkerCommands.ReadContractAsync(Kit, tier, CancellationToken.None),
            "specs/T-19-specialist-fleet.md", "Unit A", "worker/t-19-a-1a2b3c",
            ["dotnet build", "dotnet test"], extra: null);

    [Fact]
    public async Task A_mastermind_is_given_the_specialist_contract_on_top_of_the_implementers()
    {
        var prompt = await PromptFor("mastermind");

        Assert.Contains(SpecialistMarker, prompt, StringComparison.Ordinal);
        Assert.Contains(ImplementerMarker, prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf(SpecialistMarker, StringComparison.Ordinal) < prompt.IndexOf(ImplementerMarker, StringComparison.Ordinal),
            "The specialist procedure must come before the implementer contract it adds to.");
    }

    [Theory]
    [InlineData("MASTERMIND")]
    [InlineData("MasterMind")]
    public async Task The_tier_name_is_matched_whatever_its_casing(string tier) =>
        Assert.Contains(SpecialistMarker, await PromptFor(tier), StringComparison.Ordinal);

    [Theory]
    [InlineData("implementer")]
    [InlineData("utility")]
    public async Task Every_other_tier_is_given_the_implementer_contract_and_nothing_else(string tier)
    {
        var prompt = await PromptFor(tier);

        Assert.Contains(ImplementerMarker, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SpecialistMarker, prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// `{{core:...}}` is expanded by `kit install` and never on the read `worker run` does, so an include token
    /// left in `core/specialist.md` would reach the worker as literal text instead of the procedure it names.
    /// </summary>
    [Theory]
    [InlineData("mastermind")]
    [InlineData("implementer")]
    public async Task No_tier_is_handed_an_unexpanded_include(string tier) =>
        Assert.DoesNotContain("{{core:", await PromptFor(tier), StringComparison.Ordinal);
}
