using System.Text.Json;
using System.Text.RegularExpressions;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

/// <summary>What a session prompt asks for: which kind of session, on which task, holding which role.</summary>
internal sealed record SessionBrief(string Kind, string Task, string? Role);

/// <summary>What one <c>muthur worker run</c> came back with, read off its JSON.</summary>
internal sealed record WorkerRun(bool Success, string? Status, string? FailureKind, string Detail);

/// <summary>
/// One scripted session: what a model would do, done by a script, against the real hub. An orchestrator claims,
/// writes a spec on a branch, dispatches one implementer through <c>muthur worker run</c>, merges the unit and
/// submits it; an implementer reads its assignment and writes the file the spec asks for; a validator takes its
/// role, reads the branch and gives a real verdict on what it finds there. The task body's first line chooses the
/// variation: <c>sim: plain</c>, <c>sim: ask</c> (a founder question first, so the task crosses Needs you),
/// <c>sim: bounce</c> (a wrong first implementation, so validation fails once and the task comes back) or
/// <c>sim: block</c> (a first dispatch whose assignment check fails, so the worker returns blocked once).
/// </summary>
internal sealed partial class SimSession(HubClient hub, IProcessRunner processes, string repo, int pace, int askWait, CancellationToken ct)
{
    /// <summary>What a rate-limited CLI prints; the model name <c>limited</c> makes the scripted agent print it and exit 1.</summary>
    internal const string UsageLimitMessage = "ERROR: You've hit your usage limit.";

    /// <summary>The line a spec or prompt carries when the scripted implementer's assignment check is meant to fail.</summary>
    internal const string BlockLine = "sim: block";

    /// <summary>The notes an orchestrator leaves when it has to stop on an unanswered question: what a resuming session reads first.</summary>
    internal static string Notes(string id) =>
        $"# {id} working notes\n\nBecame the expert: the task writes `{id}.txt` at the repository root.\nAsked the founder CSV or JSON; waiting on the answer.\nLeft: write the spec and the file once the answer is in.\n";

    /// <summary>Why this session must not run: a home or URL that could be the organization's own.</summary>
    internal static string? Refusal(string? home, string url)
    {
        if (string.IsNullOrWhiteSpace(home)) return "MUTHUR_HOME is not set; the sim never runs against the default home.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback) return $"MUTHUR_URL '{url}' is not a loopback address.";
        if (uri.Port == new Uri(MuthurEnvironment.DefaultUrl).Port) return "MUTHUR_URL names the default port, which is where the organization lives.";
        return null;
    }

    /// <summary>The catalog model whose account is out of quota: the session fails the way a rate-limited CLI does.</summary>
    internal static bool IsLimited(string? model) => string.Equals(model, "limited", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the session's assignment off the prompt the launcher wrote for a model.</summary>
    internal static SessionBrief? Parse(string prompt)
    {
        var task = TaskKey().Match(prompt);
        if (!task.Success) return null;
        if (prompt.Contains("# Your assignment", StringComparison.Ordinal)) return new("implementer", task.Value, null);
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

    /// <summary>The spec path the assignment block names, or null when the prompt has no assignment.</summary>
    internal static string? SpecPath(string prompt) => SpecLine().Match(prompt) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Whether a prompt or spec carries the line that makes the scripted assignment check fail.</summary>
    internal static bool Blocked(string text) => text.Split('\n').Any(line => line.Trim().Equals(BlockLine, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the scripted implementer writes: the spec's <c>content:</c> line, else the task id.</summary>
    internal static string ContentFor(string spec, string task)
    {
        foreach (var line in spec.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("content:", StringComparison.OrdinalIgnoreCase)) return trimmed["content:".Length..].Trim();
        }
        return task;
    }

    /// <summary>The frozen spec the scripted orchestrator writes; the sim section is what its scripted implementer reads.</summary>
    internal static string Spec(string id, string title, string content, bool block) =>
        $"# {id} — {title}\n\n## Goal\n\nWrite `{id}.txt` at the repository root containing exactly `{id}`.\n\n## Units\n\n- Unit A: write the file.\n\n" +
        $"## Verification\n\n`git show <branch>:{id}.txt` prints `{id}`.\n\n```\necho {id} verified\n```\n\n## Sim\n\ncontent: {content}\n{(block ? BlockLine + "\n" : "")}";

    private async Task<string?> AttachSpecAsync(string id, string branch)
    {
        var spec = await hub.PostAsync(Routes.TaskAction(id, "spec"), new SetSpecRequest($"specs/{id}.md", branch), MuthurJsonContext.Default.SetSpecRequest, ct);
        if (!spec.IsSuccess) return Failed("spec", spec);
        await StepAsync();
        return null;
    }

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
            if (!ask.IsSuccess) return Failed("ask", ask);
            // The procedure: wait for the answer here, where the understanding already is. The wait costs nothing;
            // a new session would cost the whole read phase again. Only an expired wait leaves notes and exits.
            var answered = askWait > 0 && (await hub.GetAsync($"{Routes.Inbox}?wait={askWait}", ct)).IsSuccess
                && (await DetailAsync(id))?.Events.Any(e => e.Type == "request.answered") == true;
            if (!answered)
            {
                var notes = await hub.PostAsync(Routes.TaskAction(id, "notes"), new TaskNotesRequest(Notes(id)), MuthurJsonContext.Default.TaskNotesRequest, ct);
                return notes.IsSuccess
                    ? $"STATUS: done\nNOTES: asked the founder about {id}, no answer in {askWait}s; left notes and exited so a later session resumes from them"
                    : Failed("notes", notes);
            }
        }

        var branch = BranchFor(id);
        var wrong = mode == "bounce" && !detail.Events.Any(e => e.Type == "task.validation_failed");
        var block = mode == "block";
        var content = wrong ? $"{id} (first attempt, wrong on purpose)" : id;
        var worktree = Path.Combine(Path.GetTempPath(), "muthur-sim", "wt-" + Guid.NewGuid().ToString("n"));
        var exists = (await GitAsync(repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch)).Ok;
        var add = exists
            ? await GitAsync(repo, "worktree", "add", worktree, branch)
            : await GitAsync(repo, "worktree", "add", "-b", branch, worktree, "main");
        if (!add.Ok) return $"STATUS: failed\nNOTES: git worktree add: {add.Message}";
        var dispatches = new List<string>();
        try
        {
            if (await CommitSpecAsync(worktree, id, Spec(id, detail.Task.Title, content, block), "write the spec") is { } problem) return problem;
            await StepAsync();
            // Attached after every commit of it, not only the first: the hub freezes the attached digest, and an
            // amended spec that is not reattached is refused at `implemented` as spec_changed — the same rule a
            // model orchestrator has to follow when it corrects a spec after a bounce.
            if (await AttachSpecAsync(id, branch) is { } attach) return attach;

            // The orchestrator delegates the building: one implementer per unit, through the same launcher a model
            // would use. A blocked assignment check is recorded by the launcher and redispatched once, corrected.
            var (run, unit) = await DispatchAsync(id, branch);
            dispatches.Add($"{unit}: {run.Status ?? run.FailureKind ?? "no status"}");
            if (run.Status == "blocked" && block)
            {
                if (await CommitSpecAsync(worktree, id, Spec(id, detail.Task.Title, content, block: false), "fix the assignment") is { } fix) return fix;
                if (await AttachSpecAsync(id, branch) is { } reattach) return reattach;
                (run, unit) = await DispatchAsync(id, branch);
                dispatches.Add($"{unit}: {run.Status ?? run.FailureKind ?? "no status"}");
            }
            if (!run.Success) return $"STATUS: failed\nNOTES: worker run for {id} did not finish ({run.FailureKind ?? run.Status ?? "unknown"}): {run.Detail}";
            var merged = await GitAsync(worktree, "merge", "--no-ff", "-q", "-m", $"{id}: merge Unit A", unit);
            if (!merged.Ok) return $"STATUS: failed\nNOTES: git merge {unit}: {merged.Message}";
        }
        finally
        {
            await GitAsync(repo, "worktree", "remove", "--force", worktree);
        }
        await StepAsync();

        var implemented = await hub.PostAsync(Routes.TaskAction(id, "implemented"), new ImplementedRequest(branch), MuthurJsonContext.Default.ImplementedRequest, ct);
        if (!implemented.IsSuccess) return Failed("implemented", implemented);
        return $"STATUS: done\nNOTES: {id} implemented on {branch} after {string.Join(", ", dispatches)}{(wrong ? " (deliberately wrong; validation should bounce it)" : "")}";
    }

    /// <summary>Writes the spec on the task branch and commits it when it changed; a resumed session may find it already there.</summary>
    private async Task<string?> CommitSpecAsync(string worktree, string id, string spec, string why)
    {
        Directory.CreateDirectory(Path.Combine(worktree, "specs"));
        await File.WriteAllTextAsync(Path.Combine(worktree, "specs", $"{id}.md"), spec, ct);
        var staged = await GitAsync(worktree, "add", "-A");
        if (!staged.Ok) return $"STATUS: failed\nNOTES: git add: {staged.Message}";
        if ((await GitAsync(worktree, "diff", "--cached", "--quiet")).Ok) return null;
        var committed = await GitAsync(worktree, "commit", "-q", "-m", $"{id}: {why}");
        return committed.Ok ? null : $"STATUS: failed\nNOTES: git commit: {committed.Message}";
    }

    /// <summary>One <c>muthur worker run</c> for Unit A on a fresh unit branch off the task branch, run from the project repository as the orchestrator agent.</summary>
    private async Task<(WorkerRun Run, string Unit)> DispatchAsync(string id, string branch)
    {
        var unit = $"{branch}-unit-a";
        for (var n = 2; (await GitAsync(repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + unit)).Ok; n++) unit = $"{branch}-unit-a-{n}";
        var result = await processes.RunAsync("muthur",
            ["worker", "run", "--tier", "implementer", "--spec", $"specs/{id}.md", "--unit", "Unit A", "--task", id, "--base", branch, "--branch", unit],
            repo, timeout: TimeSpan.FromMinutes(10), ct: ct);
        return (ReadWorkerRun(result), unit);
    }

    /// <summary>The launcher prints its JSON on stdout when the run counted as done and on stderr otherwise; the STATUS inside decides.</summary>
    internal static WorkerRun ReadWorkerRun(ProcessResult result)
    {
        foreach (var text in new[] { result.StdOut, result.StdErr })
        {
            var start = text.IndexOf('{');
            if (start < 0) continue;
            try
            {
                using var json = JsonDocument.Parse(text[start..].Trim());
                var root = json.RootElement;
                string? Field(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                return new(success, Field("status"), Field("failureKind"), Field("report") ?? "");
            }
            catch (JsonException) { }
        }
        return new(false, null, result.Started ? "worker_output_unreadable" : "launch_unavailable", result.Message);
    }

    /// <summary>
    /// The scripted implementer: reads its assignment block, checks the spec it names, and writes the one file the
    /// spec asks for, uncommitted, exactly as a Codex-style worker leaves its output for the launcher to commit.
    /// </summary>
    public async Task<string> ImplementAsync(string id, string prompt)
    {
        var specPath = SpecPath(prompt);
        var specFile = specPath is null ? null : Path.Combine(repo, specPath.Replace('/', Path.DirectorySeparatorChar));
        var spec = specFile is not null && File.Exists(specFile) ? await File.ReadAllTextAsync(specFile, ct) : "";
        var branch = (await GitAsync(repo, "branch", "--show-current")).StdOut.Trim();
        var baseLine = BaseLine().Match(prompt) is { Success: true } b ? b.Groups[1].Value : "unnamed";
        var sha = BaseSha().Match(prompt) is { Success: true } s ? s.Groups[1].Value : "unknown";
        await StepAsync();

        if (Blocked(prompt) || Blocked(spec))
            return Report("blocked", repo, branch, baseLine, sha, "none",
                $"Read {specPath ?? "no spec"}; the assignment check failed before any write.", "none run; nothing was written",
                "none", "scripted assignment check failed");

        var file = $"{id}.txt";
        await File.WriteAllTextAsync(Path.Combine(repo, file), ContentFor(spec, id) + "\n", ct);
        return Report("done", repo, branch, baseLine, sha, "none (the launcher commits the output)",
            $"Wrote `{file}` at the worktree root as specs/{id}.md asks.", $"`git status --porcelain` lists {file}; the launcher's commit lands it on {branch}",
            "none", "none");
    }

    private static string Report(string status, string worktree, string branch, string baseLine, string sha, string commits, string summary, string verification, string deviations, string notes) =>
        $"STATUS: {status}\nWORKTREE: {worktree}\nBRANCH: {branch}\nBASE: {baseLine} at dispatch SHA {sha}; HEAD matched, no recovery\nCOMMITS: {commits}\n" +
        $"SUMMARY: {summary}\nVERIFICATION: {verification}\nDEVIATIONS: {deviations}\nNOTES: {notes}";

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

    [GeneratedRegex(@"- Spec: `([^`]+)`")]
    private static partial Regex SpecLine();

    [GeneratedRegex(@"Named local base branch: `([^`]+)`")]
    private static partial Regex BaseLine();

    [GeneratedRegex(@"Full base commit SHA at dispatch: `([^`]+)`")]
    private static partial Regex BaseSha();
}
