namespace Muthur.Launch;

/// <summary>
/// Codex CLI in exec mode; with an open-source provider it drives a local model (Ollama) through the same harness.
/// Codex has no per-command allow list: its boundary is the workspace-write sandbox, so command lists are not used here.
/// </summary>
public sealed class CodexAdapter(string name, string? openSourceProvider) : IHarnessAdapter
{
    public string Name => name;

    /// <summary>The workspace-write sandbox keeps .git read-only, so a Codex worker cannot commit; the launcher does it.</summary>
    public string? WorkerNote =>
        "Your sandbox does not allow writing to .git, so `git add`/`git commit` will be refused. That is expected and is NOT a blocker: " +
        "leave your finished work uncommitted in the worktree and the launcher commits it for you. Report `COMMITS: pending (launcher)` and STATUS by the state of the work itself.";

    private static string LastMessageFile(WorkerRequest request) => Path.Combine(request.ScratchDirectory, "codex-last-message.txt");

    public HarnessInvocation Build(WorkerRequest request)
    {
        var arguments = new List<string>
        {
            "exec",
            "--cd", request.WorkingDirectory,
            "--sandbox", "workspace-write",
            "--output-last-message", LastMessageFile(request),
            "--color", "never",
        };
        if (request.GitCommonDirectory is { Length: > 0 } git)
        {
            // A worktree's index and refs live in the main repository's .git; without this the worker cannot commit.
            arguments.Add("--add-dir");
            arguments.Add(git);
        }
        if (openSourceProvider is not null)
        {
            arguments.Add("--oss");
            arguments.Add("--local-provider");
            arguments.Add(openSourceProvider);
        }
        if (request.Model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }
        arguments.Add("-"); // prompt on stdin
        return new HarnessInvocation("codex", arguments, request.Prompt);
    }

    public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result)
    {
        var file = LastMessageFile(request);
        var report = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        if (result.Ok && report.Length > 0) return new WorkerOutcome(true, report, RateLimited: false);

        var text = report.Length > 0 ? report : result.Message;
        return new WorkerOutcome(false, text, Harnesses.LooksRateLimited(result.StdErr + result.StdOut));
    }
}
