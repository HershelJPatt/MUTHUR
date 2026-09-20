using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class HarnessTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private async Task<TierDto> TierAsync(string tier) =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Tiers}?tier={tier}", MuthurJsonContext.Default.IReadOnlyListTierDto))!.Single();

    [Fact]
    public async Task The_catalog_is_a_file_the_founder_edits()
    {
        var defaults = await TierAsync("implementer");
        Assert.True(defaults.Candidates.Count >= 2);
        Assert.True(File.Exists(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile)));

        File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile),
            """{ "tiers": { "implementer": [ { "harness": "codex", "model": "x", "account": "team" } ] } }""");

        var edited = await TierAsync("implementer");
        Assert.Equal(("codex", "x", "team"), Assert.Single(edited.Candidates) is var c ? (c.Harness, c.Model, c.Account) : default);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_is_reported_rather_than_crashing_the_endpoint()
    {
        await TierAsync("implementer");
        var catalogPath = Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile);
        File.Delete(catalogPath);
        Directory.CreateDirectory(catalogPath);

        var response = await _hub.CreateClient().GetAsync(Routes.Tiers);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_tier_entry_may_say_how_hard_its_model_thinks()
    {
        // What a new hub is given: the codex entries name their model, so the ledger stops recording "codex/default".
        var shipped = (await TierAsync("mastermind")).Candidates.Single(c => c.Harness == "codex");
        Assert.Equal("gpt-6-astra", shipped.Model);
        Assert.Equal("high", shipped.ReasoningEffort);

        File.WriteAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile),
            """
            { "tiers": { "implementer": [
              { "harness": "codex", "model": "x", "reasoningEffort": "high", "account": "team" },
              { "harness": "codex", "model": "y", "account": "team" },
              { "harness": "codex", "model": "z", "reasoningEffort": "", "account": "team" }
            ] } }
            """);

        var edited = (await TierAsync("implementer")).Candidates;
        Assert.Equal("high", edited[0].ReasoningEffort);
        Assert.Null(edited[1].ReasoningEffort);   // absent means the harness's own default
        Assert.Null(edited[2].ReasoningEffort);   // and so does empty
    }

    [Fact]
    public async Task An_agent_out_of_quota_takes_its_account_out_of_rotation_until_the_limit_passes()
    {
        var first = (await TierAsync("implementer")).Candidates[0];
        var response = await _hub.CreateClient().PostAsJsonAsync(Routes.AgentRegister,
            new RegisterAgentRequest("spent", first.Harness, first.Model, "implementer", first.Account));
        var agent = _hub.CreateClient((await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse))!.Token);

        (await agent.PostAsJsonAsync(Routes.AgentLimited, new LimitedRequest(_hub.Clock.GetUtcNow().AddHours(2)))).EnsureSuccessStatusCode();

        var tier = await TierAsync("implementer");
        Assert.All(tier.Candidates.Where(c => c.Account == first.Account), c => Assert.True(c.Limited));
        Assert.Contains(tier.Candidates, c => !c.Limited);

        _hub.Clock.Advance(TimeSpan.FromHours(3));
        Assert.All((await TierAsync("implementer")).Candidates, c => Assert.False(c.Limited));
    }

    [Fact]
    public async Task Worker_runs_are_recorded_against_the_task_with_the_model_that_did_the_work()
    {
        await _hub.AddProjectAsync();
        var orchestrator = await _hub.RegisterAgentAsync("orchestrator");
        var task = await orchestrator.AddTaskAsync("Delegated");

        (await orchestrator.PostAsJsonAsync(Routes.WorkerRuns,
            new WorkerRunReport(task.Id, "implementer", "codex", "gpt-x", "chatgpt-subscription", "worker/t-1-a-abc123", "Unit A", true, 312, null))).EnsureSuccessStatusCode();

        var recorded = (await orchestrator.GetTaskAsync(task.Id)).Events.Single(e => e.Type == "worker.finished");
        Assert.Equal("codex/gpt-x", recorded.Payload.GetProperty("worker").GetString());
        Assert.Equal("orchestrator", recorded.Actor);
    }
}
