using System.Text.Json;

namespace Muthur.Launch;

/// <summary>
/// Codex CLI in exec mode; with an open-source provider it drives a local model (Ollama) through the same harness.
/// Codex has no per-command allow list: its boundary is the workspace-write sandbox, so command lists are not used here.
/// </summary>
public sealed class CodexAdapter(string name, string? openSourceProvider) : IHarnessAdapter
{
    public string Name => name;

    public string? CapabilityExecutable => "codex";

    public string? CapabilitySettings(WorkerRequest request) => Launch.CapabilitySettings.HashFiles([
        ("user", Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "config.toml")),
        ("project", Path.Combine(request.WorkingDirectory, ".codex", "config.toml"))]);

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
            "-c", "service_tier=\"default\"",
            "-c", "features.fast_mode=false",
        };
        if (!request.RequireRepository) arguments.Add("--skip-git-repo-check");
        foreach (var (key, value) in request.GitEnvironment ?? new Dictionary<string, string>())
        {
            // Only the appended trust entry belongs on the command line. Inherited Git settings may contain
            // credentials; they stay in the child environment and are never copied to arguments or logs.
            var last = int.Parse(request.GitEnvironment!["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture) - 1;
            if (key != "GIT_CONFIG_COUNT" && key != $"GIT_CONFIG_KEY_{last}" && key != $"GIT_CONFIG_VALUE_{last}") continue;
            arguments.Add("-c");
            arguments.Add($"shell_environment_policy.set.{key}=\"{JsonEncodedText.Encode(value)}\"");
        }
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
        if (request.ReasoningEffort is { Length: > 0 } effort)
        {
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort=\"{effort}\"");
        }
        arguments.Add("-"); // prompt on stdin
        return new HarnessInvocation("codex", arguments, request.Prompt);
    }

    public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result)
    {
        var file = LastMessageFile(request);
        var report = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        if (result.Ok && report.Length > 0) return new WorkerOutcome(true, report, RateLimited: false);

        var text = result.Ok ? result.Message : $"Process exited with code {result.ExitCode}: {result.Message}";
        if (report.Length > 0) text += "\n\nFinal report from this attempt:\n" + report;
        return new WorkerOutcome(false, text, Harnesses.LooksRateLimited(result.StdErr + result.StdOut));
    }
}
