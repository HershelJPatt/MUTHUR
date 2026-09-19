using System.Text.Json;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>
/// Three harness manifests hand-edited in parallel drift. A brief added to one `kit.json` and forgotten in
/// the other two gives codex and generic repositories a quietly different roster than claude ones, and
/// nothing notices until an agent tries to take a role whose brief was never installed.
/// </summary>
public sealed class KitRosterTests
{
    private static readonly string[] Harnesses = ["claude", "codex", "generic"];

    private static string RepoRoot =>
        Path.GetDirectoryName(
            ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}."))!;

    private static SortedSet<string> BriefsShippedBy(string harness)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot, "kit", harness, "kit.json")));

        return new SortedSet<string>(
            doc.RootElement.GetProperty("files").EnumerateArray()
                .Select(file => file.GetProperty("to").GetString() ?? string.Empty)
                .Where(to => to.StartsWith("briefs/", StringComparison.Ordinal)),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Every_harness_ships_the_same_briefs()
    {
        var rosters = Harnesses.ToDictionary(harness => harness, BriefsShippedBy, StringComparer.Ordinal);
        var everything = new SortedSet<string>(rosters.Values.SelectMany(r => r), StringComparer.Ordinal);

        var differences = everything
            .Select(brief => (brief, missing: Harnesses.Where(h => !rosters[h].Contains(brief)).ToArray()))
            .Where(difference => difference.missing.Length > 0)
            .Select(difference => $"{difference.brief} is missing from {string.Join(" and ", difference.missing)}")
            .ToArray();

        Assert.True(
            differences.Length == 0,
            $"The harness manifests do not ship the same briefs: {string.Join("; ", differences)}.");
    }

    [Fact]
    public void The_knowledge_oncall_brief_is_in_the_roster()
    {
        foreach (var harness in Harnesses)
            Assert.Contains("briefs/knowledge-oncall.md", BriefsShippedBy(harness));

        var starter = Path.Combine(RepoRoot, "kit", "briefs", "knowledge-oncall.md");
        var installed = Path.Combine(RepoRoot, "briefs", "knowledge-oncall.md");

        Assert.True(File.Exists(starter), $"{starter} does not exist.");
        Assert.True(File.Exists(installed), $"{installed} does not exist.");
        Assert.True(
            File.ReadAllBytes(starter).SequenceEqual(File.ReadAllBytes(installed)),
            $"{installed} is not a byte-identical copy of {starter}.");
    }
}
