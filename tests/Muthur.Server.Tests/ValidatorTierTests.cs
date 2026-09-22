using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// Which catalog tier a conductor-started validator comes from. Validation exercises a frozen spec's verification,
/// which is implementer-shaped work; staffing it from the mastermind tier by default made every task cost two
/// mastermind cold starts. The tier is configuration, and an empty tier falls back rather than leaving the
/// task unvalidated.
/// </summary>
public sealed class ValidatorTierTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private void Catalog(string json) => File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile), json);

    private Task<(string Tier, IReadOnlyList<Muthur.Launch.HarnessCandidate> Candidates)> CandidatesAsync() =>
        ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services).CandidatesAsync(null, CancellationToken.None);

    [Fact]
    public async Task By_default_a_validator_comes_from_the_implementer_tier()
    {
        _ = _hub.Server;
        Catalog("""
            { "tiers": {
                "mastermind":  [ { "harness": "claude", "model": "opus",   "account": "a" } ],
                "implementer": [ { "harness": "codex",  "model": "gpt",    "account": "b" } ] } }
            """);

        var (tier, candidates) = await CandidatesAsync();

        Assert.Equal("implementer", tier);
        Assert.Equal(["codex"], candidates.Select(c => c.Harness));
    }

    [Fact]
    public async Task An_empty_or_limited_out_tier_falls_back_to_mastermind()
    {
        _ = _hub.Server;
        Catalog("""
            { "tiers": {
                "mastermind":  [ { "harness": "claude", "model": "opus", "account": "a" } ],
                "implementer": [ ] } }
            """);

        var (tier, candidates) = await CandidatesAsync();

        Assert.Equal("mastermind", tier);
        Assert.Equal(["claude"], candidates.Select(c => c.Harness));
    }

    [Fact]
    public async Task The_founder_can_keep_validation_on_the_mastermind_tier()
    {
        _hub.Settings["Muthur:ConductorValidatorTier"] = "mastermind";
        _ = _hub.Server;
        Catalog("""
            { "tiers": {
                "mastermind":  [ { "harness": "claude", "model": "opus", "account": "a" } ],
                "implementer": [ { "harness": "codex",  "model": "gpt",  "account": "b" } ] } }
            """);

        var (tier, candidates) = await CandidatesAsync();

        Assert.Equal("mastermind", tier);
        Assert.Equal(["claude"], candidates.Select(c => c.Harness));
    }
}
