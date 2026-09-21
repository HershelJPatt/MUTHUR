using System.Net.Http.Json;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Server.Tests;

/// <summary>Supplies explicit simulated check receipts for tests of behavior downstream of integration.</summary>
internal static class IntegrationFixture
{
    public static Task<IntegrationRunResult> RunIntegrationAsync(this HubFactory hub, string task) =>
        new IntegrationRunner(new ProcessRunner(), new Client(hub)).RunAsync(task);

    private sealed class Client(HubFactory hub) : IIntegrationClient
    {
        private DateTimeOffset started;
        private async Task Send<T>(string task, string action, T request)
        {
            var response = await hub.Founder().PostAsJsonAsync(Routes.TaskIntegrationAction(task, action), request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        public async Task<IntegrationAssignmentDto> ClaimAsync(string task, CancellationToken ct)
        {
            var response = await hub.Founder().PostAsJsonAsync(Routes.TaskIntegrationAction(task, "claim"), new IntegrationClaimRequest(), ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
            started = hub.Clock.GetUtcNow();
            return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.IntegrationAssignmentDto, ct))!;
        }
        public Task StartedAsync(string task, IntegrationStartRequest request, CancellationToken ct) => Send(task, "started", request with { ProcessStartedAt = started });
        public Task CandidateAsync(string task, IntegrationCandidateRequest request, CancellationToken ct) => Send(task, "candidate", request);
        public Task FailureAsync(string task, IntegrationFailureRequest request, CancellationToken ct) => Send(task, "failure", request);
        public Task RenewAsync(string task, IntegrationRenewRequest request, CancellationToken ct) => Send(task, "renew", request);
        public Task VerdictAsync(string task, IntegrationEvidenceDto request, CancellationToken ct)
        {
            hub.Clock.Advance(request.EndedAt - request.StartedAt);
            return Send(task, "verdict", request with { StartedAt = started, EndedAt = hub.Clock.GetUtcNow() });
        }
    }

    public static async Task PassIntegrationAsync(this HubFactory hub, TestRepo repo, string task, bool detach = true)
    {
        var client = hub.Founder();
        var response = await client.PostAsJsonAsync(Routes.TaskIntegrationAction(task, "claim"), new IntegrationClaimRequest());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var c = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.IntegrationAssignmentDto))!.Candidate;
        var tree = repo.Git("merge-tree", "--write-tree", c.TargetSha, c.ImplementationSha).Split('\n')[0].Trim();
        var candidate = repo.Git("commit-tree", tree, "-p", c.TargetSha, "-p", c.ImplementationSha, "-m", "Integration " + task + " fixture");
        (await client.PostAsJsonAsync(Routes.TaskIntegrationAction(task, "candidate"),
            new IntegrationCandidateRequest(c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, candidate, tree))).EnsureSuccessStatusCode();
        var started = hub.Clock.GetUtcNow();
        hub.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var evidence = new IntegrationEvidenceDto(c.AssignmentId, c.Id, c.SubjectId, candidate,
            [new("build", c.RequiredChecks.Build!, 0, 0, "simulated-build-fixture", new string('a', 64)),
             new("test", c.RequiredChecks.Test!, 0, 0, "simulated-test-fixture", new string('b', 64))], started, hub.Clock.GetUtcNow(), true, null);
        (await client.PostAsJsonAsync(Routes.TaskIntegrationAction(task, "verdict"), evidence)).EnsureSuccessStatusCode();
        if (detach && repo.Git("branch", "--show-current") == c.DefaultBranch) repo.Git("checkout", "-q", "--detach", c.TargetSha);
    }
}
