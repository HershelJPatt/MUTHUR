using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class DoctorHarnessTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly string _kit = Path.Combine(Path.GetTempPath(), "muthur-tests", "kit-" + Guid.NewGuid().ToString("n"));

    public DoctorHarnessTests()
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

    private void Catalog(string json = """{"tiers":{"implementer":[{"harness":"ghost"}]}}""")
    {
        Directory.CreateDirectory(_hub.DataDir);
        File.WriteAllText(CatalogPath, json);
    }

    private void KitFor(string harness) => File.WriteAllText(
        Path.Combine(Directory.CreateDirectory(Path.Combine(_kit, harness)).FullName, KitDirectory.Manifest),
        """{"files":[]}""");

    private async Task<DoctorDto> ReportAsync(bool probe = false) =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe={probe.ToString().ToLowerInvariant()}",
            MuthurJsonContext.Default.DoctorDto))!;

    private static void Warning(DoctorDto report, string harness = "ghost")
    {
        var check = Assert.Single(report.Checks, c => c.Category == "harness" && c.Subject == harness);
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Equal($"Harness '{harness}' has a tier entry in harnesses.json but no kit, so a session started on it gets no procedures. "
            + $"Add kit/{harness}/kit.json, or correct the spelling in harnesses.json.", check.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_catalog_only_harness_warns_with_zero_agents_and_an_empty_readable_kit(bool probe)
    {
        Catalog();
        var report = await ReportAsync(probe);
        Warning(report);
        Assert.DoesNotContain(report.Checks, c => c.Category == "agent");
    }

    [Fact]
    public async Task A_codex_candidate_whose_sandbox_cannot_read_the_nuget_configuration_is_a_warning_with_the_fix()
    {
        if (!OperatingSystem.IsWindows() || !WorkerEnvironment.RequiredReadable().Any(p => File.Exists(p) || Directory.Exists(p))) return;
        var original = WorkerEnvironment.Readable;
        WorkerEnvironment.Readable = (_, _) => false;
        try
        {
            KitFor("codex");
            Catalog("""{"tiers":{"implementer":[{"harness":"codex","model":"gpt","account":"b"}]}}""");
            var check = Assert.Single((await ReportAsync()).Checks, c => c.Category == "harness" && c.Detail.Contains("icacls", StringComparison.Ordinal));
            Assert.Equal(CheckStatus.Warn, check.Status);
            Assert.Contains(WorkerEnvironment.CodexSandboxGroup, check.Detail);
        }
        finally { WorkerEnvironment.Readable = original; }
    }

    [Fact]
    public async Task A_known_kit_is_silent()
    {
        Catalog();
        KitFor("ghost");
        Assert.DoesNotContain((await ReportAsync()).Checks, c => c.Category == "harness");
    }

    [Fact]
    public async Task A_missing_kit_is_silent()
    {
        Catalog();
        _hub.Settings["Muthur:KitDir"] = Path.Combine(_kit, "absent");
        Assert.DoesNotContain((await ReportAsync()).Checks, c => c.Category == "harness");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_invalid_or_unreadable_catalog_is_silent_without_a_doctor_failure(bool unreadable)
    {
        Catalog();
        _ = _hub.CreateClient();
        if (unreadable)
        {
            File.Delete(CatalogPath);
            Directory.CreateDirectory(CatalogPath);
        }
        else File.WriteAllText(CatalogPath, "not json");
        Assert.DoesNotContain((await ReportAsync()).Checks, c => c.Category is "harness" or "doctor");
    }

    [Fact]
    public async Task Repeated_candidates_across_tiers_models_and_accounts_produce_one_row()
    {
        Catalog("""{"tiers":{"implementer":[{"harness":"ghost","model":"a","account":"one"},{"harness":"ghost","model":"b","account":"two"}],"mastermind":[{"harness":"ghost","model":"c"}]}}""");
        var report = await ReportAsync();
        Warning(report);
        Assert.Single(report.Checks, c => c.Category == "harness");
    }

    [Fact]
    public async Task Kit_agent_and_deduplication_comparisons_are_ordinal()
    {
        Catalog("""{"tiers":{"implementer":[{"harness":"Ghost"},{"harness":"GHOST"},{"harness":"ghost"}]}}""");
        KitFor("ghost");
        await _hub.RegisterAgentAsync("standing", harness: "ghost");
        var report = await ReportAsync();
        Warning(report, "Ghost");
        Warning(report, "GHOST");
        Assert.Equal(2, report.Checks.Count(c => c.Category == "harness"));
    }

    [Fact]
    public async Task A_limited_candidate_is_still_reported()
    {
        Catalog("""{"tiers":{"implementer":[{"harness":"ghost","account":"spent"}]}}""");
        (await _hub.Founder().PostAsJsonAsync(Routes.AccountLimits,
            new AccountLimitRequest("spent", _hub.Clock.GetUtcNow().AddHours(1)))).EnsureSuccessStatusCode();
        var tiers = await _hub.CreateClient().GetFromJsonAsync(Routes.Tiers, MuthurJsonContext.Default.IReadOnlyListTierDto);
        Assert.True(Assert.Single(Assert.Single(tiers!).Candidates).Limited);
        Warning(await ReportAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standing_agents_suppress_the_catalog_row_and_retain_the_agent_warning(bool stale)
    {
        Catalog();
        await _hub.RegisterAgentAsync("standing", harness: "ghost");
        if (stale) _hub.Clock.Advance(TimeSpan.FromSeconds(new MuthurOptions().AgentStaleSeconds + 1));
        var report = await ReportAsync();
        Assert.DoesNotContain(report.Checks, c => c.Category == "harness");
        var check = Assert.Single(report.Checks, c => c.Category == "agent" && c.Subject == "standing");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("a tier entry in harnesses.json but no kit", check.Detail);
    }

    [Fact]
    public async Task A_conductor_only_agent_does_not_suppress_the_catalog_row()
    {
        Catalog();
        _ = _hub.CreateClient();
        await ActivatorUtilities.CreateInstance<ValidatorSessionLauncher>(_hub.Services).IdentityFor(
            new ConductorAssignment(1, "T-1", "Build T-1", "muthur", "win-validator", AvoidHarness: null),
            new HarnessCandidate("ghost", "model", "account"), default);
        var report = await ReportAsync();
        Warning(report);
        Assert.DoesNotContain(report.Checks, c => c.Category == "agent");
    }

    [Fact]
    public async Task Repeated_calls_observe_catalog_kit_and_agent_changes()
    {
        Catalog();
        Warning(await ReportAsync());
        Catalog("""{"tiers":{}}""");
        Assert.DoesNotContain((await ReportAsync()).Checks, c => c.Category == "harness");
        Catalog();
        Warning(await ReportAsync());
        KitFor("ghost");
        Assert.DoesNotContain((await ReportAsync()).Checks, c => c.Category == "harness");
        File.Delete(Path.Combine(_kit, "ghost", KitDirectory.Manifest));
        Warning(await ReportAsync());
        await _hub.RegisterAgentAsync("standing", harness: "ghost");
        Assert.DoesNotContain((await ReportAsync()).Checks, c => c.Category == "harness");
    }
}
