using System.Diagnostics;

namespace Muthur.Launch;

/// <summary>How a check command written in a project file or a spec is handed to the platform's shell.</summary>
public static class Shell
{
    public static (string FileName, string[] Arguments) Command(string command) => OperatingSystem.IsWindows()
        ? ("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command])
        : ("/bin/sh", ["-c", command]);

    /// <summary>Variables a check must not inherit from the hub: its credentials, and any model vendor's.</summary>
    public static string[] Scrub() => Environment.GetEnvironmentVariables().Keys.Cast<string>()
        .Where(k => k.StartsWith("MUTHUR", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Muthur__", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("OPENAI", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>The last <paramref name="lines"/> lines of a process's combined output, which is what an evidence report has room for.</summary>
    public static string Tail(string stdout, string stderr, int lines = 40)
    {
        var all = (stdout + "\n" + stderr).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        return string.Join("\n", all.Skip(Math.Max(0, all.Count - lines)));
    }
}

/// <param name="ExitCode">Null when the command never ran: an earlier one failed, or the run timed out before it.</param>
public sealed record CheckOutcome(string Command, int? ExitCode, string Tail, bool TimedOut = false)
{
    public bool Passed => ExitCode == 0;
}

/// <param name="Failure">Why the run as a whole could not be trusted, when something other than a command's exit code stopped it.</param>
public sealed record ChecksRun(IReadOnlyList<CheckOutcome> Checks, TimeSpan Elapsed, string? Failure = null)
{
    public bool Passed => Failure is null && Checks.Count > 0 && Checks.All(c => c.Passed);

    /// <summary>One line per command with its exit code, then the output tails: the evidence a verdict carries.</summary>
    public string Evidence()
    {
        var lines = Checks.Select(c => $"{(c.Passed ? "pass" : c.TimedOut ? "timeout" : c.ExitCode is null ? "skipped" : "fail")} exit={(c.ExitCode?.ToString() ?? "-")}: {c.Command}").ToList();
        if (Failure is not null) lines.Add("failure: " + Failure);
        foreach (var check in Checks.Where(c => c.Tail.Length > 0))
            lines.Add($"--- {check.Command}\n{check.Tail}");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Runs a subject's required checks in a detached worktree at its implementation commit and reports what each
/// said. It decides nothing: the caller turns all-zero exits into a verdict. Nothing here touches git or a shell
/// except through <see cref="IProcessRunner"/>.
/// </summary>
public sealed class ChecksRunner(IProcessRunner processes)
{
    public async Task<ChecksRun> RunAsync(string repository, string sha, Guid subjectId, IReadOnlyList<string> commands, TimeSpan timeout, CancellationToken ct = default)
    {
        var repo = VerificationFiles.PlainPath(repository);
        var worktree = VerificationFiles.PlainPath(Path.Combine(repo, ".worktrees", $"checks-{subjectId:N}"));
        var scrub = Shell.Scrub();
        var watch = Stopwatch.StartNew();
        var outcomes = new List<CheckOutcome>();
        string? failure = null;
        var created = false;
        try
        {
            if (Path.Exists(worktree)) throw new IOException($"A checks worktree for this subject already exists at {worktree}.");
            Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
            created = true;
            var added = await processes.RunAsync("git", ["worktree", "add", "--detach", worktree, sha], repo,
                timeout: TimeSpan.FromMinutes(2), ct: ct, scrubEnvironment: scrub);
            if (!added.Ok) throw new IOException("git worktree add: " + added.Message);
            // One budget for the whole run, the way a validator session has one: a command gets what is left of it.
            // The runner reports a command it had to kill as exit 124, and the budget being spent is what says
            // that was this deadline rather than the command's own.
            foreach (var command in commands)
            {
                if (outcomes.Any(o => !o.Passed)) { outcomes.Add(new(command, null, "", TimedOut: outcomes.Any(o => o.TimedOut))); continue; }
                var (file, arguments) = Shell.Command(command);
                var remaining = timeout - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) { outcomes.Add(new(command, null, "", TimedOut: true)); continue; }
                var result = await processes.RunAsync(file, arguments, worktree, timeout: remaining, ct: ct, scrubEnvironment: scrub);
                var timedOut = result.ExitCode == 124 && watch.Elapsed >= timeout;
                outcomes.Add(new(command, timedOut ? null : result.ExitCode, Shell.Tail(result.StdOut, result.StdErr), timedOut));
            }
        }
        catch (IOException ex) { failure = ex.Message; }
        finally
        {
            if (created)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var removed = await processes.RunAsync("git", ["worktree", "remove", "--force", "--force", worktree], repo,
                    timeout: TimeSpan.FromSeconds(25), ct: cleanup.Token, scrubEnvironment: scrub);
                if (!removed.Ok && Path.Exists(worktree)) failure ??= "checks worktree cleanup failed: " + removed.Message;
            }
        }
        if (failure is null && outcomes.Any(o => o.TimedOut)) failure = $"timed out after {timeout.TotalMinutes:0} minutes";
        return new ChecksRun(outcomes, watch.Elapsed, failure);
    }
}
