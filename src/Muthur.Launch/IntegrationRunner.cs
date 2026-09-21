using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Launch;

public interface IIntegrationClient
{
    Task<IntegrationAssignmentDto> ClaimAsync(string task, CancellationToken ct);
    Task StartedAsync(string task, IntegrationStartRequest request, CancellationToken ct);
    Task CandidateAsync(string task, IntegrationCandidateRequest request, CancellationToken ct);
    Task VerdictAsync(string task, IntegrationEvidenceDto request, CancellationToken ct);
    Task FailureAsync(string task, IntegrationFailureRequest request, CancellationToken ct);
    Task RenewAsync(string task, IntegrationRenewRequest request, CancellationToken ct);
}

public sealed record IntegrationRunResult(bool Passed, string? Candidate, string EvidencePath, string? Failure);

/// <summary>Executes the immutable approved checks outside the hub and retains the exact tested commit.</summary>
public sealed class IntegrationRunner(IProcessRunner processes, IIntegrationClient client)
{
    public async Task<IntegrationRunResult> RunAsync(string task, CancellationToken ct = default, IntegrationAssignmentDto? assigned = null)
    {
        var assignment = assigned ?? await client.ClaimAsync(task, ct);
        var c = assignment.Candidate;
        if (c.State != "assigned") throw new InvalidOperationException("An existing integration cannot be executed again; inspect its evidence and recover it first.");
        var repo = VerificationFiles.PlainPath(c.RepositoryPath);
        var worktree = VerificationFiles.PlainPath(Path.Combine(repo, ".worktrees", $"integration-{c.Id:N}"));
        var artifacts = VerificationFiles.PlainPath(Path.Combine(repo, ".work", "integration", $"{c.Id:N}"));
        if (Path.Exists(worktree) || Path.Exists(artifacts)) throw new IOException("Assignment-owned output already exists; do not overwrite interrupted evidence.");
        Directory.CreateDirectory(artifacts);
        var evidencePath = Path.Combine(artifacts, "evidence.json");
        var started = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(assignment.SessionTimeoutSeconds));
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var renewal = RenewAsync(task, c, deadline, renewalStop.Token);
        string? candidate = null;
        var checks = new List<IntegrationCheckDto>();
        string? failure = null;
        var cleanup = true;
        var created = false;
        var phase = "construction";
        var scrub = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("MUTHUR", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Muthur__", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("OPENAI", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)).ToArray();
        async Task<ProcessResult> Git(params string[] args) => await processes.RunAsync("git", args, repo, timeout: TimeSpan.FromMinutes(2), ct: deadline.Token, scrubEnvironment: scrub);
        static string Output(ProcessResult r)
        {
            if (!r.Ok) throw new IOException(r.Message);
            return r.StdOut.Trim();
        }
        try
        {
            using var self = Process.GetCurrentProcess();
            await client.StartedAsync(task, new(c.AssignmentId, c.SubjectId, self.Id, self.StartTime.ToUniversalTime(), worktree, artifacts), deadline.Token);
            var included = (await Git("merge-base", "--is-ancestor", c.ImplementationSha, c.TargetSha)).Ok;
            string tree;
            if (included)
            {
                candidate = c.TargetSha;
                tree = Output(await Git("rev-parse", c.TargetSha + "^{tree}"));
            }
            else
            {
                var merge = await Git("merge-tree", "--write-tree", c.TargetSha, c.ImplementationSha);
                await File.WriteAllTextAsync(Path.Combine(artifacts, "merge.log"), merge.StdOut + "\n" + merge.StdErr, deadline.Token);
                if (!merge.Ok) throw new IOException("merge_conflict: " + merge.Message);
                tree = merge.StdOut.Split('\n')[0].Trim();
                candidate = Output(await Git("-c", "user.name=MUTHUR Integration", "-c", "user.email=integration@localhost",
                    "-c", "commit.gpgsign=false", "commit-tree", tree, "-p", c.TargetSha, "-p", c.ImplementationSha, "-m", $"Integration {task} {c.Id:N}"));
            }
            Output(await Git("update-ref", $"refs/muthur/integration/{c.Id:N}", candidate, new string('0', candidate.Length)));
            await client.CandidateAsync(task, new(c.AssignmentId, c.SubjectId, c.TargetSha, c.ImplementationSha, candidate, tree, included), deadline.Token);
            phase = "checkout";
            Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
            created = true; // Cleanup also handles partially created worktrees.
            Output(await Git("worktree", "add", "--detach", worktree, candidate));
            var actual = await processes.RunAsync("git", ["rev-parse", "HEAD"], worktree, ct: deadline.Token, scrubEnvironment: scrub);
            if (Output(actual) != candidate) throw new IOException("Integration checkout differs from the registered candidate.");
            phase = "launch";
            foreach (var (name, command) in new[] { ("build", c.RequiredChecks.Build), ("test", c.RequiredChecks.Test) })
            {
                if (string.IsNullOrWhiteSpace(command)) throw new IOException("Required integration command is missing.");
                if (checks.Any(r => r.ExitCode != 0)) { checks.Add(new(name, command, null, 0, null, null, true)); continue; }
                var shell = OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh";
                var arguments = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-NonInteractive", "-Command", command } : new[] { "-c", command };
                var watch = Stopwatch.StartNew();
                var result = await processes.RunAsync(shell, arguments, worktree, timeout: TimeSpan.FromSeconds(assignment.SessionTimeoutSeconds),
                    ct: deadline.Token, scrubEnvironment: scrub);
                var log = Path.Combine(artifacts, name + ".log");
                await File.WriteAllTextAsync(log, result.StdOut + "\n" + result.StdErr, Encoding.UTF8, deadline.Token);
                checks.Add(new(name, command, result.ExitCode, watch.ElapsedMilliseconds, log, VerificationFiles.HashFile(log)));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            failure = ex.Message;
        }
        finally
        {
            renewalStop.Cancel();
            try { await renewal; } catch (OperationCanceledException) { } catch (Exception ex) { failure ??= "Lease renewal failed: " + ex.Message; }
            if (created)
            {
                try
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var removed = await processes.RunAsync("git", ["worktree", "remove", "--force", "--force", worktree], repo,
                        timeout: TimeSpan.FromSeconds(25), ct: cleanupDeadline.Token, scrubEnvironment: scrub);
                    cleanup = removed.Ok && !Path.Exists(worktree);
                    if (!cleanup) failure ??= "Owned checkout cleanup failed: " + removed.Message;
                }
                catch (Exception ex) { cleanup = false; failure ??= "Owned checkout cleanup failed: " + ex.Message; }
            }
        }
        using var reportDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (candidate is not null && checks.Count == 2)
        {
            var evidence = new IntegrationEvidenceDto(c.AssignmentId, c.Id, c.SubjectId, candidate, checks, started,
                DateTimeOffset.UtcNow, cleanup && failure is null, failure);
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, MuthurJsonContext.Default.IntegrationEvidenceDto), reportDeadline.Token);
            await client.VerdictAsync(task, evidence, reportDeadline.Token);
            return new(evidence.CleanupSucceeded && checks.All(r => r.ExitCode == 0 && !r.Skipped), candidate, evidencePath, failure);
        }
        var code = deadline.IsCancellationRequested ? ct.IsCancellationRequested ? "runner_cancelled" : "runner_timeout"
            : failure?.StartsWith("merge_conflict:", StringComparison.Ordinal) == true ? "merge_conflict" : phase == "checkout" ? "checkout_failed" : "runner_failed";
        var report = new IntegrationFailureRequest(c.AssignmentId, c.SubjectId, phase, code, failure ?? "Integration did not complete.");
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(report, MuthurJsonContext.Default.IntegrationFailureRequest), reportDeadline.Token);
        await client.FailureAsync(task, report, reportDeadline.Token);
        return new(false, candidate, evidencePath, failure);
    }

    private async Task RenewAsync(string task, IntegrationCandidateDto c, CancellationTokenSource execution, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await client.RenewAsync(task, new(c.AssignmentId, c.SubjectId), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { execution.Cancel(); throw; }
    }
}
