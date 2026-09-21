using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class ObjectiveTests : IDisposable
{
    private readonly HubFactory hub = new();
    private HttpClient owner = null!;
    private ObjectiveDefinition definition = null!;
    private TaskDto task = null!;
    public void Dispose() => hub.Dispose();

    private async Task<ObjectiveDto> CreateAsync()
    {
        await hub.AddProjectAsync();
        task = await hub.Founder().AddTaskAsync("Approved delivery quality objective", "demo");
        owner = await hub.RegisterAgentAsync("objective-owner");
        var ledger = hub.Services.GetRequiredService<Ledger>();
        var seq = await ledger.ReadAsync(async (db, _) => await db.Events.Where(e => e.Type == "task.added").Select(e => e.Seq).SingleAsync());
        var now = hub.Clock.GetUtcNow();
        definition = new(new(task.Id, seq), "Reduce repeated incidents", "Internal delivery", new("incidents", 3, "measured", "events", 12,
            now.AddDays(-1), now, [task.Id], ["fixture-v1"], "baseline fixture", ""), "No repeated incidents in ten observations",
            now, now.AddDays(1), "incidents", 0, "at_most", 10, "Founder-approved scope");
        return await Read(await owner.PostAsJsonAsync("/api/v1/objectives", new AddObjectiveRequest("demo", "Delivery quality", definition)));
    }

    private static async Task<ObjectiveDto> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ObjectiveDto))!;
    }

    private ObjectiveMeasurement Observation(decimal value = 0, string quality = "measured", int samples = 10, string missing = "") =>
        definition.Baseline with { Value = value, Quality = quality, SampleCount = samples,
            From = definition.WindowStart, To = definition.WindowEnd, Evidence = "Independent fixture report", MissingData = missing };

    [Fact]
    public async Task Acceptance_requires_current_measured_window_and_founder_and_preserves_history()
    {
        var o = await CreateAsync();
        var path = "/api/v1/objectives/" + o.Id;
        await Read(await owner.PostAsJsonAsync(path + "/link", new LinkObjectiveRequest(1, task.Id, "capability", "Approved work")));
        var observation = new ObserveObjectiveRequest(1, "first", "observation", Observation(), "Ten independent observations", task.Id);
        await Read(await owner.PostAsJsonAsync(path + "/observe", observation));
        var accept = new AcceptObjectiveRequest(1, "first", "Checked the outcome and retained validation guardrails.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync(path + "/accept", accept)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await hub.Founder().PostAsJsonAsync(path + "/accept", accept)).StatusCode);
        hub.Clock.Advance(TimeSpan.FromDays(1));
        var achieved = await Read(await hub.Founder().PostAsJsonAsync(path + "/accept", accept));
        Assert.Equal("achieved", achieved.State);
        Assert.Single(achieved.Acceptances);
        Assert.Equal(HttpStatusCode.Conflict, (await hub.Founder().PostAsJsonAsync(path + "/accept", accept)).StatusCode);
        var regression = observation with { Key = "regression", Kind = "regression", Description = "A later regression was observed" };
        await Read(await owner.PostAsJsonAsync(path + "/observe", regression));
        var view = await hub.Services.GetRequiredService<ObjectiveService>().GetAsync(o.Id);
        Assert.True(view.RegressionAfterAcceptance);
        Assert.Null(view.OutcomesPerMeasuredFounderHour);
        await Read(await hub.Founder().PostAsJsonAsync(path + "/effort", new EffortObjectiveRequest(1, "review", 30, "measured", definition.WindowStart, definition.WindowEnd, "Founder stopwatch", "Review allocation")));
        Assert.Equal(2m, (await hub.Services.GetRequiredService<ObjectiveService>().GetAsync(o.Id)).OutcomesPerMeasuredFounderHour);
        var amended = await Read(await hub.Founder().PostAsJsonAsync(path + "/amend", new AmendObjectiveRequest(1, definition with { Reason = "Reopen after regression" })));
        Assert.Equal(2, amended.Revision);
        Assert.Equal("observing", amended.State);
        Assert.Single(amended.Acceptances);
        Assert.Equal(2, amended.Evidence.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path + "/observe", observation)).StatusCode);
    }

    [Theory]
    [InlineData("estimated", 10, "", 0)]
    [InlineData("measured", 9, "", 0)]
    [InlineData("measured", 10, "unknown revision", 0)]
    [InlineData("measured", 10, "", 1)]
    public async Task Missing_or_insufficient_outcome_evidence_never_passes(string quality, int samples, string missing, int value)
    {
        var o = await CreateAsync();
        var path = "/api/v1/objectives/" + o.Id;
        await Read(await owner.PostAsJsonAsync(path + "/observe", new ObserveObjectiveRequest(1, "sample", "observation", Observation(value, quality, samples, missing), "Observed result")));
        hub.Clock.Advance(TimeSpan.FromDays(2));
        var response = await hub.Founder().PostAsJsonAsync(path + "/accept", new AcceptObjectiveRequest(1, "sample", "Review"));
        Assert.Equal("objective_success_not_demonstrated", (await response.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Replay_is_read_only_and_conflicting_key_or_caller_is_refused()
    {
        var o = await CreateAsync();
        var path = "/api/v1/objectives/" + o.Id;
        var request = new ObserveObjectiveRequest(1, "sample", "observation", Observation(), "Observed result");
        var first = await Read(await owner.PostAsJsonAsync(path + "/observe", request));
        hub.Clock.Advance(TimeSpan.FromMinutes(1));
        var repeated = await Read(await owner.PostAsJsonAsync(path + "/observe", request));
        Assert.Equal(first.UpdatedAt, repeated.UpdatedAt);
        Assert.Single(repeated.Evidence);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path + "/observe", request with { Description = "Different result" })).StatusCode);
        var stranger = await hub.RegisterAgentAsync("stranger");
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync(path + "/observe", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync(path + "/amend", new AmendObjectiveRequest(1, definition))).StatusCode);
        await Read(await hub.Founder().PostAsJsonAsync(path + "/close", new CloseObjectiveRequest(1, "abandoned", "No longer pursuing")));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path + "/observe", request)).StatusCode);
        var dashboard = await hub.Founder().GetStringAsync("/outcomes/" + o.Id);
        Assert.Contains("Recorded approval reference", dashboard);
        Assert.DoesNotContain("percent", dashboard, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_task_creation_cannot_supply_founder_approval()
    {
        await CreateAsync();
        var agentTask = await owner.AddTaskAsync("Unapproved outcome", "demo");
        var seq = await hub.Services.GetRequiredService<Ledger>().ReadAsync(async (db, _) => await db.Events.Where(e => e.Type == "task.added").OrderByDescending(e => e.Seq).Select(e => e.Seq).FirstAsync());
        var response = await owner.PostAsJsonAsync("/api/v1/objectives", new AddObjectiveRequest("demo", "Unapproved", definition with { Approval = new(agentTask.Id, seq) }));
        Assert.Equal("objective_approval", (await response.ReadErrorAsync()).Code);
    }
}
