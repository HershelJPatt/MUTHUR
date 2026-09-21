using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class RoutingTests : IDisposable
{
    private readonly HubFactory hub = new();
    public void Dispose() => hub.Dispose();

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
    }
}
