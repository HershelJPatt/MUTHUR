using System.Text.Json;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>
/// `core/specialist.md` reaches claude through a `{{core:...}}` include and codex and generic only through an
/// explicit `kit.json` entry, so forgetting the entry gives the specialist contract to one vendor and not the
/// others - the vendor-capability leak this procedure exists to close. `KitRosterTests` pins the brief sets
/// across the three manifests; these pin the procedures the same way.
/// </summary>
public sealed class SpecialistRosterTests
{
    private const string ProcedureDirectory = ".muthur/procedures/";

    private static string RepoRoot =>
        Path.GetDirectoryName(
            ProjectContext.FindFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}."))!;

    private static string ManifestPath(string harness) => Path.Combine(RepoRoot, "kit", harness, "kit.json");

    private static (string From, string To)[] Entries(string harness)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath(harness)));
        return [.. doc.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => (file.GetProperty("from").GetString()!, file.GetProperty("to").GetString()!))];
    }

    private static SortedSet<string> ProceduresShippedBy(string harness) =>
        new(Entries(harness).Select(e => e.To).Where(to => to.StartsWith(ProcedureDirectory, StringComparison.Ordinal)),
            StringComparer.Ordinal);

    [Theory]
    [InlineData("codex")]
    [InlineData("generic")]
    public void A_harness_without_includes_ships_the_specialist_procedure_as_a_file(string harness) =>
        Assert.Contains($"{ProcedureDirectory}specialist.md", ProceduresShippedBy(harness));

    /// <summary>Claude has no procedures directory; its agent definition pulls the same core file in by include.</summary>
    [Fact]
    public void The_claude_specialist_agent_reaches_the_same_core_procedure()
    {
        var agent = File.ReadAllText(Path.Combine(RepoRoot, "kit", "claude", "agents", "muthur-specialist.md"));

        Assert.Contains("{{core:specialist.md}}", agent, StringComparison.Ordinal);
        Assert.Contains("{{core:implementer.md}}", agent, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_and_generic_ship_the_same_core_procedures()
    {
        var codex = ProceduresShippedBy("codex");
        var generic = ProceduresShippedBy("generic");

        Assert.True(
            codex.SetEquals(generic),
            $"The procedure rosters have drifted: codex ships [{string.Join(", ", codex)}], "
            + $"generic ships [{string.Join(", ", generic)}].");
    }

    /// <summary>A misspelled `from` installs nothing and says nothing; `kit install` would throw at the copy.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("generic")]
    public void Every_source_a_manifest_names_exists(string harness)
    {
        var harnessDir = Path.GetDirectoryName(ManifestPath(harness))!;

        foreach (var (from, to) in Entries(harness))
            Assert.True(
                File.Exists(Path.GetFullPath(Path.Combine(harnessDir, from))),
                $"kit/{harness}/kit.json maps '{from}' to '{to}', but that source does not exist.");
    }
}
