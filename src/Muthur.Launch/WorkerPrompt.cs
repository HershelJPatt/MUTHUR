using System.Text;

namespace Muthur.Launch;

public static class WorkerPrompt
{
    /// <summary>The implementer contract followed by the concrete assignment. Everything the worker knows is in here.</summary>
    public static string Compose(string contractMarkdown, string specPath, string? unit, string branch, IReadOnlyList<string> verifyCommands, string? extra)
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
