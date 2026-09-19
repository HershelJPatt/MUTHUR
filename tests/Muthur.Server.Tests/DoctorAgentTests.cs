using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// `muthur doctor` answering whether every agent runs on a harness this organization actually has. The hub is
/// pointed at a scratch kit through Muthur:KitDir rather than the process-global MUTHUR_KIT, and the tier
/// catalog is written straight into the hub's data directory — HarnessService re-reads that file on every call.
/// </summary>
public sealed class DoctorAgentTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly string _kit = Path.Combine(Path.GetTempPath(), "muthur-tests", "kit-" + Guid.NewGuid().ToString("n"));

    public DoctorAgentTests()
    {
        Directory.CreateDirectory(_kit);
        _hub.Settings["Muthur:KitDir"] = _kit;
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_kit, recursive: true); } catch (IOException) { }
    }

    private string CatalogPath => Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile);

    /// <summary>A harness the kit has procedures to install.</summary>
    private void KitFor(string harness) =>
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(Path.Combine(_kit, harness)).FullName, KitDirectory.Manifest),
            """{"files":[]}""");

    /// <summary>The whole tier catalog, overwritten: one tier whose candidates are the harnesses named here.</summary>
    private void Catalog(params string[] harnesses)
    {
        Directory.CreateDirectory(_hub.DataDir);
        var candidates = string.Join(",", harnesses.Select(h => $"{{\"harness\":\"{h}\",\"model\":\"opus\"}}"));
        File.WriteAllText(CatalogPath, $"{{\"tiers\":{{\"mastermind\":[{candidates}]}}}}");
    }

    private async Task<DoctorDto> ReportAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync(Routes.Doctor, MuthurJsonContext.Default.DoctorDto))!;

    private static CheckDto AgentCheck(DoctorDto report, string name) =>
        Assert.Single(report.Checks, c => c.Category == "agent" && c.Subject == name);

    [Fact]
    public async Task An_agent_on_a_harness_with_a_kit_and_a_tier_entry_says_so_and_nothing_else()
    {
        KitFor("claude");
        Catalog("claude");
        await _hub.RegisterAgentAsync("straight", harness: "claude");

        var report = await ReportAsync();

        var check = Assert.Single(report.Checks, c => c.Category == "agent");
        Assert.Equal("straight", check.Subject);
        Assert.Equal(CheckStatus.Ok, check.Status);
        // Both sources answered, so there is nothing to caveat the verdict with.
        Assert.DoesNotContain(report.Checks, c => c.Category == "harness");
    }

    [Fact]
    public async Task A_live_agent_on_a_harness_in_neither_source_fails_and_names_what_there_is()
    {
        KitFor("claude");
        KitFor("generic");
        Catalog("claude", "codex");
        await _hub.RegisterAgentAsync("corner", harness: "cladue");

        var report = await ReportAsync();

        var check = AgentCheck(report, "corner");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal(1, report.Fail);
        Assert.Contains("'cladue'", check.Detail);
        Assert.Contains("This organization has: claude, codex, generic.", check.Detail);
    }

    [Fact]
    public async Task The_same_agent_gone_stale_only_warns()
    {
        KitFor("claude");
        Catalog("claude");
        await _hub.RegisterAgentAsync("corner", harness: "cladue");

        // Nothing has heard from it since, which is the difference between a defect and a harness since removed.
        _hub.Clock.Advance(TimeSpan.FromSeconds(new MuthurOptions().AgentStaleSeconds + 1));

        var report = await ReportAsync();

        var check = AgentCheck(report, "corner");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Equal(0, report.Fail);
        Assert.Contains("Last seen", check.Detail);
    }

    [Fact]
    public async Task A_harness_with_a_kit_and_no_tier_entry_is_fine()
    {
        KitFor("generic");
        Catalog("claude");
        await _hub.RegisterAgentAsync("hand-run", harness: "generic");

        var check = AgentCheck(await ReportAsync(), "hand-run");

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Contains("no tier can be staffed on it headlessly", check.Detail);
    }

    [Fact]
    public async Task A_tier_entry_with_no_kit_warns()
    {
        KitFor("claude");
        Catalog("claude", "codex-oss");
        await _hub.RegisterAgentAsync("spare", harness: "codex-oss");

        var check = AgentCheck(await ReportAsync(), "spare");

        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("a session started on it gets no procedures", check.Detail);
    }

    [Fact]
    public async Task A_kit_directory_that_is_not_reachable_costs_the_check_its_fail()
    {
        _hub.Settings["Muthur:KitDir"] = Path.Combine(_kit, "absent");
        Catalog("claude");
        await _hub.RegisterAgentAsync("straight", harness: "claude");
        await _hub.RegisterAgentAsync("corner", harness: "cladue");

        var report = await ReportAsync();

        var kit = Assert.Single(report.Checks, c => c.Category == "harness");
        Assert.Equal("kit", kit.Subject);
        Assert.Equal(CheckStatus.Warn, kit.Status);
        Assert.Equal(CheckStatus.Ok, AgentCheck(report, "straight").Status);
        // Live, and in neither source the check could read — but one of them was never read, so warn, not fail.
        Assert.Equal(CheckStatus.Warn, AgentCheck(report, "corner").Status);
        Assert.Equal(0, report.Fail);
    }

    [Fact]
    public async Task A_catalog_that_will_not_parse_is_reported_rather_than_thrown()
    {
        KitFor("claude");
        await _hub.RegisterAgentAsync("straight", harness: "claude");
        File.WriteAllText(CatalogPath, "not json");

        var report = await ReportAsync();

        var catalog = Assert.Single(report.Checks, c => c.Category == "harness");
        Assert.Equal(MuthurEnvironment.HarnessFile, catalog.Subject);
        Assert.Equal(CheckStatus.Warn, catalog.Status);
        Assert.Contains(CatalogPath, catalog.Detail);
        // "doctor" is what DoctorService emits when a check throws, so its absence is the whole point here.
        Assert.DoesNotContain(report.Checks, c => c.Category == "doctor");
        Assert.Equal(CheckStatus.Ok, AgentCheck(report, "straight").Status);
    }

    [Fact]
    public async Task A_hub_with_no_standing_agents_says_nothing_at_all()
    {
        _hub.Settings["Muthur:KitDir"] = Path.Combine(_kit, "absent");

        var report = await ReportAsync();

        Assert.DoesNotContain(report.Checks, c => c.Category is "agent" or "harness");
    }

    [Fact]
    public async Task A_conductor_staffed_session_is_not_reported_although_a_standing_agent_beside_it_is()
    {
        KitFor("claude");
        Catalog("claude");
        await StaffAsync("cladue");
        await _hub.RegisterAgentAsync("corner", harness: "cladue");

        var report = await ReportAsync();

        // Same harness, same typo: what separates them is that the conductor's came out of the catalog.
        var check = Assert.Single(report.Checks, c => c.Category == "agent");
        Assert.Equal("corner", check.Subject);
    }

    [Fact]
    public async Task A_harness_that_differs_from_the_sources_only_in_case_is_reported()
    {
        // Registration lower-cases the harness, so the two spellings are put on either side of the comparison
        // by naming the sources in capitals. The rule is the same either way: the check compares ordinally,
        // because one that folded case would hide the class of typo it exists to catch.
        KitFor("Claude");
        Catalog("Claude");
        await _hub.RegisterAgentAsync("corner", harness: "Claude");

        var check = AgentCheck(await ReportAsync(), "corner");

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("'claude'", check.Detail);
        Assert.Contains("This organization has: Claude.", check.Detail);
    }

    /// <summary>Staffs one session the way the conductor does — the real launcher over the hub's own services.</summary>
    private Task<AgentIdentity> StaffAsync(string harness) =>
        ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services).IdentityFor(
            new ConductorAssignment(1, "T-1", "Build T-1", "muthur", "win-validator", AvoidHarness: null),
            new HarnessCandidate(harness, "opus", "a-subscription"),
            default);
}
