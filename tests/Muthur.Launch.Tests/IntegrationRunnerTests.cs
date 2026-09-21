using Muthur.Contracts;

namespace Muthur.Launch.Tests;

public sealed class IntegrationRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "muthur-integration-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessRunner processes = new();
    public void Dispose() => VerificationFiles.DeleteOwned(root, Path.GetDirectoryName(root)!);

    private async Task<string> Git(params string[] args)
    {
        var result = await processes.RunAsync("git", args, root);
        Assert.True(result.Ok, result.Message);
        return result.StdOut.Trim();
    }

    private async Task<FakeClient> SetupAsync(string build, bool conflict = false)
    {
        Directory.CreateDirectory(root);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.name", "Fixture");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "base\n");
        await Git("add", "."); await Git("commit", "-qm", "base");
        await Git("checkout", "-qb", "implementation");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "implementation\n");
        await Git("commit", "-qam", "implementation");
        var implementation = await Git("rev-parse", "HEAD");
        await Git("checkout", "-q", "main");
        if (conflict)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "target\n");
            await Git("commit", "-qam", "target");
        }
        var target = await Git("rev-parse", "HEAD");
        var now = DateTimeOffset.UtcNow;
        var candidate = new IntegrationCandidateDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "T-1", Guid.NewGuid(),
            root, "main", target, implementation, null, null, new(build, "echo fixture-test"), "assigned", Guid.NewGuid(),
            now.AddMinutes(5), 1, now, null, null, false, null, null, null, null, null, null);
        return new(new(candidate, 60));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checks_run_at_the_exact_candidate_and_failed_build_skips_tests(bool fail)
    {
        var client = await SetupAsync(fail ? "exit 7" : "git rev-parse HEAD");
        var run = await new IntegrationRunner(processes, client).RunAsync("T-1");
        Assert.Equal(!fail, run.Passed);
        Assert.NotNull(client.Registered);
        var evidence = Assert.IsType<IntegrationEvidenceDto>(client.Evidence);
        Assert.Equal(client.Registered.CandidateSha, evidence.CheckoutSha);
        Assert.Equal(client.Assigned.Candidate.TargetSha, await Git("rev-parse", "main"));
        Assert.Equal(client.Assigned.Candidate.TargetSha + " " + client.Assigned.Candidate.ImplementationSha,
            await Git("show", "-s", "--format=%P", evidence.CheckoutSha));
        Assert.True(evidence.CleanupSucceeded);
        Assert.False(Directory.Exists(client.Start!.OwnedWorktreePath));
        Assert.Equal(fail, evidence.Checks[1].Skipped);
        Assert.Equal(fail ? 7 : 0, evidence.Checks[0].ExitCode);
        Assert.Equal(evidence.Checks[0].ArtifactSha256, VerificationFiles.HashFile(evidence.Checks[0].ArtifactReference!));
        if (!fail) Assert.Contains(evidence.CheckoutSha, await File.ReadAllTextAsync(evidence.Checks[0].ArtifactReference!));
        Assert.True(File.Exists(run.EvidencePath));
    }

    [Fact]
    public async Task Conflict_preserves_evidence_without_changing_target_or_creating_a_checkout()
    {
        var client = await SetupAsync("echo build", conflict: true);
        var run = await new IntegrationRunner(processes, client).RunAsync("T-1");
        Assert.False(run.Passed);
        Assert.Null(client.Registered);
        Assert.Equal("merge_conflict", client.Failure!.Code);
        Assert.Equal(client.Assigned.Candidate.TargetSha, await Git("rev-parse", "main"));
        Assert.False(Directory.Exists(client.Start!.OwnedWorktreePath));
        Assert.True(File.Exists(Path.Combine(client.Start.ArtifactsDirectory, "merge.log")));
    }

    private sealed class FakeClient(IntegrationAssignmentDto assigned) : IIntegrationClient
    {
        public IntegrationAssignmentDto Assigned { get; } = assigned;
        public IntegrationStartRequest? Start { get; private set; }
        public IntegrationCandidateRequest? Registered { get; private set; }
        public IntegrationEvidenceDto? Evidence { get; private set; }
        public IntegrationFailureRequest? Failure { get; private set; }
        public Task<IntegrationAssignmentDto> ClaimAsync(string task, CancellationToken ct) => Task.FromResult(Assigned);
        public Task StartedAsync(string task, IntegrationStartRequest request, CancellationToken ct) { Start = request; return Task.CompletedTask; }
        public Task CandidateAsync(string task, IntegrationCandidateRequest request, CancellationToken ct) { Registered = request; return Task.CompletedTask; }
        public Task VerdictAsync(string task, IntegrationEvidenceDto request, CancellationToken ct) { Evidence = request; return Task.CompletedTask; }
        public Task FailureAsync(string task, IntegrationFailureRequest request, CancellationToken ct) { Failure = request; return Task.CompletedTask; }
        public Task RenewAsync(string task, IntegrationRenewRequest request, CancellationToken ct) => Task.CompletedTask;
    }
}
