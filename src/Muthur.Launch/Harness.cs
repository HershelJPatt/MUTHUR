namespace Muthur.Launch;

/// <summary>What a headless worker is asked to do, independent of which harness runs it.</summary>
public sealed record WorkerRequest(
    string WorkingDirectory,
    string Prompt,
    string Model,
    /// <summary>The repository's shared .git directory. A worktree's index lives there, so sandboxes must allow writing to it.</summary>
    string? GitCommonDirectory,
    IReadOnlyList<string> AllowedCommands,
    IReadOnlyList<string> DeniedCommands,
    /// <summary>Directory for files the adapter needs (settings, last-message capture). Outside the worktree.</summary>
    string ScratchDirectory);

/// <summary>A process to start: executable, arguments, and the prompt on stdin.</summary>
public sealed record HarnessInvocation(string FileName, IReadOnlyList<string> Arguments, string Stdin);

public sealed record WorkerOutcome(bool Success, string Report, bool RateLimited, decimal? CostUsd = null, string? SessionId = null);

/// <summary>Everything MUTHUR knows about one agent harness. The only place vendor CLIs are named.</summary>
public interface IHarnessAdapter
{
    string Name { get; }

    /// <summary>Harness-specific guidance appended to the worker's assignment, or null.</summary>
    string? WorkerNote { get; }

    HarnessInvocation Build(WorkerRequest request);

    WorkerOutcome Interpret(WorkerRequest request, ProcessResult result);
}

public static class Harnesses
{
    public static readonly IReadOnlyList<IHarnessAdapter> All =
    [
        new ClaudeAdapter(),
        new CodexAdapter("codex", openSourceProvider: null),
        new CodexAdapter("codex-oss", openSourceProvider: "ollama"),
    ];

    public static IHarnessAdapter? Find(string name) =>
        All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Usage limits look different in every CLI; these phrases cover what they print today.</summary>
    public static bool LooksRateLimited(string text)
    {
        string[] signs = ["rate limit", "rate_limit", "usage limit", "quota", "too many requests", "429", "limit reached", "overloaded"];
        return signs.Any(sign => text.Contains(sign, StringComparison.OrdinalIgnoreCase));
    }
}
