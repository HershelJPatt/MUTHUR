namespace Muthur.Launch;

/// <summary>
/// Where the kit is and what is in it. It lives here rather than in the CLI because the hub asks the same two
/// questions and cannot reference the CLI to ask them.
/// </summary>
public static class KitDirectory
{
    public const string Variable = "MUTHUR_KIT";
    public const string Manifest = "kit.json";

    /// <summary>
    /// The kit directory, or null when there is none. An explicit setting that points nowhere is never
    /// silently replaced by a guess — the rule <see cref="Locate"/> shares with the CLI's server probe.
    /// </summary>
    /// <param name="configured">An explicit setting, which beats the environment variable.</param>
    /// <param name="baseDirectory">Where to probe from; the running binary's directory by default.</param>
    public static string? Locate(string? configured = null, string? baseDirectory = null)
    {
        if (configured is { Length: > 0 })
            return Directory.Exists(configured) ? configured : null;
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } fromEnvironment)
            return Directory.Exists(fromEnvironment) ? fromEnvironment : null;

        // The CLI's layout: muthur.exe sits beside kit/.
        var home = baseDirectory ?? AppContext.BaseDirectory;
        var beside = Path.Combine(home, "kit");
        if (Directory.Exists(beside)) return beside;

        // The hub's layout: install.ps1 publishes the server into server/ and copies the kit next to it.
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(home)) is { Length: > 0 } parent)
        {
            var above = Path.Combine(parent, "kit");
            if (Directory.Exists(above)) return above;
        }
        return null;
    }

    /// <summary>
    /// The kit a harness reads its procedures from. The Codex kit is AGENTS.md-shaped, so it serves the Codex
    /// local-provider variant and pi, which reads AGENTS.md too; every other harness has a kit of its own name.
    /// </summary>
    public static string KitFor(string harness) =>
        harness.StartsWith("codex", StringComparison.OrdinalIgnoreCase) || harness.Equals("pi", StringComparison.OrdinalIgnoreCase)
            ? "codex" : harness;

    /// <summary>
    /// The harnesses that kit has procedures for: its immediate subdirectories holding a <see cref="Manifest"/>,
    /// in ordinal order.
    /// <para>
    /// Null and empty are different answers, and callers depend on it: null is "could not tell" — no directory,
    /// or one that would not be read — and an empty list is "told, and there are none".
    /// </para>
    /// </summary>
    public static IReadOnlyList<string>? Harnesses(string? kitDirectory)
    {
        if (kitDirectory is null || !Directory.Exists(kitDirectory)) return null;
        try
        {
            return [.. Directory.EnumerateDirectories(kitDirectory)
                .Where(d => File.Exists(Path.Combine(d, Manifest)))
                .Select(d => Path.GetFileName(d)!)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
