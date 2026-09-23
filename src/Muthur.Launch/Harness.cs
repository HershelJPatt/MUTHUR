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
    string ScratchDirectory,
    /// <summary>Harness-specific reasoning effort, e.g. "high". Null means the harness's own default.</summary>
    string? ReasoningEffort = null,
    bool RequireRepository = true,
    IReadOnlyDictionary<string, string>? GitEnvironment = null,
    CapabilityContext? Capabilities = null,
    /// <summary>A cap on agentic turns for harnesses that have one. Null means the harness's own default.</summary>
    int? MaxTurns = null);

/// <summary>A process to start: executable, arguments, and the prompt on stdin.</summary>
public sealed record HarnessInvocation(string FileName, IReadOnlyList<string> Arguments, string Stdin);

/// <param name="CostUsd">What the harness reported for the run, or null when it reports none.</param>
/// <param name="TotalTokens">For harnesses that print only one number (Codex prints "tokens used"), the total; null when input and output are known separately.</param>
public sealed record WorkerOutcome(bool Success, string Report, bool RateLimited, decimal? CostUsd = null, string? SessionId = null,
    int? InputTokens = null, int? OutputTokens = null, int? CacheReadTokens = null, int? TotalTokens = null);

/// <summary>Everything MUTHUR knows about one agent harness. The only place vendor CLIs are named.</summary>
public interface IHarnessAdapter
{
    string Name { get; }

    /// <summary>Harness-specific guidance appended to the worker's assignment, or null.</summary>
    string? WorkerNote { get; }

    string? CapabilityExecutable => null;

    string? CapabilitySettings(WorkerRequest request) => null;

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
        new SimAdapter(),
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
