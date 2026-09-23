using System.Text;

namespace Muthur.Launch;

public static class WorkerPrompt
{
    /// <summary>The line that tells a worker the launcher already ran the assignment check; the contract keys off it.</summary>
    public const string VerifiedMarker = "- **Verified by the launcher at dispatch:** base branch, dispatch SHA, clean worktree and frozen spec blob were checked when this worktree was created.";

    /// <summary>The implementer contract followed by the concrete assignment. Everything the worker knows is in here.</summary>
    public static string Compose(string contractMarkdown, string specPath, string? unit, string branch, IReadOnlyList<string> verifyCommands, string? extra,
        WorkerAssignment? assignment = null)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine(contractMarkdown.Trim());
        prompt.AppendLine();
        prompt.AppendLine("---");
        prompt.AppendLine();
        prompt.AppendLine("# Your assignment");
        prompt.AppendLine();
        prompt.AppendLine($"- Spec: `{specPath}` (read all of it, and the repository's CLAUDE.md / AGENTS.md if present).");
        prompt.AppendLine(unit is { Length: > 0 } ? $"- Your unit: **{unit}**. Only the files that unit names." : "- Your unit: the whole spec.");
        prompt.AppendLine($"- You are in your own git worktree, on branch `{branch}`. Work only here; commit here; never switch branches.");
        if (assignment is { } a)
        {
            // The launcher resolved the base, cut the worktree from that commit, and read the spec blob itself, so the
            // worker is told so in as many words: the contract's short re-check applies, not the recovery procedure.
            prompt.AppendLine(VerifiedMarker);
            prompt.AppendLine($"- Exact worktree root: `{a.Worktree}`.");
            prompt.AppendLine($"- Named local base branch: `{a.BaseBranch}`.");
            prompt.AppendLine($"- Full base commit SHA at dispatch: `{a.BaseCommit}`.");
            prompt.AppendLine($"- Named default branch: `{a.DefaultBranch}`.");
            prompt.AppendLine($"- Frozen spec blob SHA: `{a.SpecBlob}` (path `{a.SpecPath}`).");
            prompt.AppendLine($"- Re-check only that `refs/heads/{a.BaseBranch}` still resolves to `{a.BaseCommit}` and that `git status --porcelain` is empty; then read the spec with `git show \"refs/heads/{a.BaseBranch}:{a.SpecPath}\"`.");
            prompt.AppendLine("- Git trust is supplied only to this process for this worktree. Never change global safe.directory or sandbox permissions.");
        }
        if (verifyCommands.Count > 0)
        {
            prompt.AppendLine("- Verification (run from the worktree root; all must pass):");
            foreach (var command in verifyCommands) prompt.AppendLine($"    {command}");
        }
        if (extra is { Length: > 0 }) prompt.AppendLine($"- Note from the orchestrator: {extra}");
        prompt.AppendLine();
        prompt.AppendLine("Finish with the report in the exact format of the contract. Your final message must be that report and nothing else.");
        return prompt.ToString();
    }
}

public static class WorkerReport
{
    /// <summary>The value of the report's STATUS line ("done", "blocked", "spec-problem"), or null when there is none.</summary>
    public static string? Status(string report)
    {
        foreach (var line in report.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('*', '#', ' ').TrimEnd('*');
            if (trimmed.StartsWith("STATUS:", StringComparison.OrdinalIgnoreCase))
                return trimmed["STATUS:".Length..].Trim().Trim('*', '`', ' ').ToLowerInvariant();
        }
        return null;
    }
}
