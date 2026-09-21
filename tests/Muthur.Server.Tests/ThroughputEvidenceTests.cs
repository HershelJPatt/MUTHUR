using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class ThroughputEvidenceTests
{
    [Fact]
    public void Recent_decisions_are_bounded_newest_first_and_preserve_authority_instructions()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var requests = Enumerable.Range(1, 8).Select(i => new FounderRequest
        {
            Id = i, AgentName = "owner", Question = new string('Q', 1000), Answer = new string('A', 3000),
            AnsweredAt = at.AddMinutes(i), Status = RequestStatus.Answered
        });
        var packet = DecisionContext.Compose(requests, new Dictionary<int, string> { [8] = "founder" });
        Assert.StartsWith("Request #8; answered 2026-01-01T00:08:00", packet);
        Assert.DoesNotContain("Request #3;", packet);
        Assert.Contains("TRUNCATED", packet);
        Assert.Contains("actor: founder", packet);
        Assert.True(packet.Length < 10000);
        var prompt = OrchestratorSessionLauncher.Prompt(new(1, "T-1", "task", "repo", true, "old", packet), new("test", "model", null));
        Assert.Contains(packet, prompt);
        Assert.Contains("Technical decisions cannot waive human-only decisions", prompt);
        Assert.Contains("latest applicable decision", prompt);
    }

    [Fact]
    public async Task Old_and_new_worker_receipts_round_trip_without_inventing_provenance()
    {
        using var hub = new HubFactory();
        await hub.AddProjectAsync();
        var owner = await hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Measured work");
        var old = new WorkerRunReport(task.Id, "implementer", "test", "model", null, "worker/old", null, true, 12, null);
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, old)).EnsureSuccessStatusCode();
        var current = old with { Success = false, RunId = "run-123", Status = "blocked", FailureKind = "timeout",
            BaseCommit = new string('a', 40), HeadCommit = new string('b', 40), SpecBlob = new string('c', 40), ExitCode = 124 };
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, current)).EnsureSuccessStatusCode();
        var receipts = await hub.Services.GetRequiredService<ReceiptsService>().ReadAsync(24);
        var recent = receipts.Runs[0];
        Assert.Equal(current.RunId, recent.RunId);
        Assert.Equal(current.FailureKind, recent.FailureKind);
        Assert.Equal(current.BaseCommit, recent.BaseCommit);
        Assert.Equal(current.HeadCommit, recent.HeadCommit);
        Assert.Equal(current.SpecBlob, recent.SpecBlob);
        Assert.Equal(124, recent.ExitCode);
        Assert.Null(receipts.Runs[1].RunId);
        Assert.Null(receipts.Runs[1].BaseCommit);
        Assert.Null(receipts.Runs[1].ExitCode);
    }
}
