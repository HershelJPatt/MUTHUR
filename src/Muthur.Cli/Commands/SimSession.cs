using System.Text.Json;
using System.Text.RegularExpressions;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

/// <summary>What a session prompt asks for: which kind of session, on which task, holding which role.</summary>
internal sealed record SessionBrief(string Kind, string Task, string? Role);

/// <summary>
/// One scripted session: what a model would do, done by a script, against the real hub. An orchestrator claims,
/// writes a spec and an implementation on a branch and submits it; a validator takes its role, reads the branch
/// and gives a real verdict on what it finds there. The task body's first line chooses the variation:
/// <c>sim: plain</c>, <c>sim: ask</c> (a founder question first, so the task crosses Needs you) or
/// <c>sim: bounce</c> (a wrong first implementation, so validation fails once and the task comes back).
/// </summary>
internal sealed partial class SimSession(HubClient hub, IProcessRunner processes, string repo, int pace, CancellationToken ct)
{
    /// <summary>Why this session must not run: a home or URL that could be the organization's own.</summary>
    internal static string? Refusal(string? home, string url)
    {
        if (string.IsNullOrWhiteSpace(home)) return "MUTHUR_HOME is not set; the sim never runs against the default home.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback) return $"MUTHUR_URL '{url}' is not a loopback address.";
        if (uri.Port == new Uri(MuthurEnvironment.DefaultUrl).Port) return "MUTHUR_URL names the default port, which is where the organization lives.";
        return null;
    }

    /// <summary>Reads the session's assignment off the prompt the launcher wrote for a model.</summary>
    internal static SessionBrief? Parse(string prompt)
    {
        var task = TaskKey().Match(prompt);
        if (!task.Success) return null;
        if (prompt.Contains("You are a validator", StringComparison.Ordinal))
        {
            var role = RoleKey().Match(prompt);
            return role.Success ? new("validator", task.Value, role.Groups[1].Value) : null;
        }
        return prompt.Contains("mastermind orchestrator", StringComparison.Ordinal) ? new("orchestrator", task.Value, null) : null;
    }

    /// <summary>"sim: ask" on the task body's first line, or plain when it says nothing.</summary>
    internal static string Mode(string body)
    {
        var first = body.Split('\n', 2)[0].Trim();
        return first.StartsWith("sim:", StringComparison.OrdinalIgnoreCase) ? first[4..].Trim().ToLowerInvariant() : "plain";
    }

    internal static string BranchFor(string task) => $"task/{task.ToLowerInvariant()}-sim";

    private Task StepAsync() => pace > 0 ? Task.Delay(pace, ct) : Task.CompletedTask;

    private static string Failed(string step, ApiResult result) => $"STATUS: failed\nNOTES: {step} answered {result.Status}: {result.Body}";

    private Task<ProcessResult> GitAsync(string directory, params string[] arguments) =>
        processes.RunAsync("git", ["-c", "commit.gpgsign=false", .. arguments], directory, timeout: TimeSpan.FromSeconds(60), ct: ct);

    private async Task<TaskDetailDto?> DetailAsync(string id)
    {
        var detail = await hub.GetAsync(Routes.Task(id), ct);
        return detail.IsSuccess ? JsonSerializer.Deserialize(detail.Body, MuthurJsonContext.Default.TaskDetailDto) : null;
    }

    public async Task<string> OrchestrateAsync(string id)
    {
        var claim = await hub.PostAsync(Routes.TaskAction(id, "claim"), new ClaimTaskRequest(), MuthurJsonContext.Default.ClaimTaskRequest, ct);
        if (claim.Status == 409) return $"STATUS: done\nNOTES: another session holds {id}; nothing to do";
        if (!claim.IsSuccess) return Failed("claim", claim);
        if (await DetailAsync(id) is not { } detail) return $"STATUS: failed\nNOTES: {id} could not be read after the claim";
        var mode = Mode(detail.Task.Body);
        await StepAsync();

        if (mode == "ask" && !detail.Events.Any(e => e.Type == "request.answered"))
        {
            var ask = await hub.PostAsync(Routes.Requests,
                new AskRequest($"{id}: should the export be CSV or JSON?", id, ["csv", "json"], "human"), MuthurJsonContext.Default.AskRequest, ct);
            return ask.IsSuccess ? $"STATUS: done\nNOTES: asked the founder about {id} and checkpointed; a later session resumes it" : Failed("ask", ask);
        }

        var branch = BranchFor(id);
        var wrong = mode == "bounce" && !detail.Events.Any(e => e.Type == "task.validation_failed");
        var worktree = Path.Combine(Path.GetTempPath(), "muthur-sim", "wt-" + Guid.NewGuid().ToString("n"));
        var exists = (await GitAsync(repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch)).Ok;
        var add = exists
            ? await GitAsync(repo, "worktree", "add", worktree, branch)
            : await GitAsync(repo, "worktree", "add", "-b", branch, worktree, "main");
        if (!add.Ok) return $"STATUS: failed\nNOTES: git worktree add: {add.Message}";
        try
        {
            Directory.CreateDirectory(Path.Combine(worktree, "specs"));
            await File.WriteAllTextAsync(Path.Combine(worktree, "specs", $"{id}.md"),
                $"# {id} — {detail.Task.Title}\n\n## Goal\n\nWrite `{id}.txt` at the repository root containing exactly `{id}`.\n\n## Verification\n\n`git show <branch>:{id}.txt` prints `{id}`.\n", ct);
            await File.WriteAllTextAsync(Path.Combine(worktree, $"{id}.txt"), wrong ? $"{id} (first attempt, wrong on purpose)\n" : $"{id}\n", ct);
            var staged = await GitAsync(worktree, "add", "-A");
            if (!staged.Ok) return $"STATUS: failed\nNOTES: git add: {staged.Message}";
            var committed = await GitAsync(worktree, "commit", "-q", "-m", $"{id}: {(wrong ? "first attempt" : "implement the spec")}");
            if (!committed.Ok) return $"STATUS: failed\nNOTES: git commit: {committed.Message}";
        }
        finally
        {
            await GitAsync(repo, "worktree", "remove", "--force", worktree);
        }
        await StepAsync();

        if (detail.Task.SpecPath is null)
        {
            var spec = await hub.PostAsync(Routes.TaskAction(id, "spec"), new SetSpecRequest($"specs/{id}.md", branch), MuthurJsonContext.Default.SetSpecRequest, ct);
            if (!spec.IsSuccess) return Failed("spec", spec);
            await StepAsync();
        }
        var implemented = await hub.PostAsync(Routes.TaskAction(id, "implemented"), new ImplementedRequest(branch), MuthurJsonContext.Default.ImplementedRequest, ct);
        if (!implemented.IsSuccess) return Failed("implemented", implemented);
        return $"STATUS: done\nNOTES: {id} implemented on {branch}{(wrong ? " (deliberately wrong; validation should bounce it)" : "")}";
    }

    public async Task<string> ValidateAsync(string id, string role)
    {
        var take = await hub.PostAsync(Routes.RoleAction(role, "take"), ct);
        if (!take.IsSuccess) return Failed("role take", take);
        try
        {
            await hub.GetAsync(Routes.RoleAction(role, "brief"), ct);
            var claim = await hub.PostAsync(Routes.TaskAction(id, "validate-claim"), new ClaimValidationRequest(role), MuthurJsonContext.Default.ClaimValidationRequest, ct);
            if (claim.Status == 409) return $"STATUS: done\nNOTES: another validator has {id}; released the role";
            if (!claim.IsSuccess) return Failed("validate claim", claim);
            var task = JsonSerializer.Deserialize(claim.Body, MuthurJsonContext.Default.TaskDto);
            await StepAsync();

            // The verdict is on what the branch holds, not on what the script was told to say.
            var branch = task?.Branch ?? "";
            var shown = await GitAsync(repo, "show", $"{branch}:{id}.txt");
            var actual = shown.Ok ? shown.StdOut.Replace("\r\n", "\n") : "";
            var pass = shown.Ok && actual == id + "\n";
            var command = $"git show {branch}:{id}.txt";
            var evidence = pass
                ? $"Ran `{command}` in the project repository: the file contains exactly `{id}`, which is what specs/{id}.md requires. Reproduce with: {command}"
                : $"Ran `{command}`: expected exactly `{id}`, found `{(shown.Ok ? actual.Trim() : shown.Message)}`. specs/{id}.md is not met. Reproduce with: {command}";
            var verdict = await hub.PostAsync(Routes.TaskAction(id, pass ? "pass" : "fail"), new VerdictRequest(role, evidence, task?.CurrentSubject?.Id), MuthurJsonContext.Default.VerdictRequest, ct);
            if (!verdict.IsSuccess) return Failed("verdict", verdict);
            return $"STATUS: done\nNOTES: {(pass ? "passed" : "failed")} {id} on {branch}";
        }
        finally
        {
            await hub.PostAsync(Routes.RoleAction(role, "release"), CancellationToken.None);
        }
    }

    [GeneratedRegex(@"\bT-\d+\b")]
    private static partial Regex TaskKey();

    [GeneratedRegex(@"Take the role `([^`]+)`")]
    private static partial Regex RoleKey();
}
