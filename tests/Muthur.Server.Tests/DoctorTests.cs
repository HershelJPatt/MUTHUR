using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class DoctorTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capability_cache_check_is_read_only_even_when_probe_is_requested(bool probe)
    {
        var directory = Path.Combine(_hub.DataDir, "capabilities");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "malformed.json");
        const string content = "not observations";
        File.WriteAllText(file, content);
        var report = await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe={probe.ToString().ToLowerInvariant()}", MuthurJsonContext.Default.DoctorDto);
        var check = Assert.Single(report!.Checks, c => c.Category == "capability");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("unknown/malformed: 1", check.Detail);
        Assert.Contains("Doctor never starts model probes", check.Detail);
        Assert.Equal(content, File.ReadAllText(file));
        Assert.Single(Directory.EnumerateFiles(directory));
        Assert.False(Directory.Exists(Path.Combine(_hub.DataDir, "capability-scratch")));
        Assert.Empty(_hub.Validators.Started);
        Assert.Empty(_hub.Orchestrators.Started);
    }

    [Fact]
    public async Task A_hub_with_nothing_configured_answers_anyone_with_only_what_it_knows_of_itself()
    {
        var response = await _hub.CreateClient().GetAsync(Routes.Doctor);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.DoctorDto);
        // The launch-side cache starts with unknown coverage; reading it never starts a model probe.
        Assert.Equal(2, report!.Checks.Count);
        var logging = Assert.Single(report.Checks, c => c.Category == "logging");
        var capabilities = Assert.Single(report.Checks, c => c.Category == "capability");
        Assert.Contains("Cached observations: 0", capabilities.Detail);
        Assert.Equal("logging", logging.Category);
        Assert.Equal(CheckStatus.Ok, logging.Status);
        Assert.Equal(1, report.Ok);
        Assert.Equal(1, report.Warn);
        Assert.Equal(0, report.Fail);
        Assert.Equal(_hub.Clock.GetUtcNow(), report.At);
    }

    [Fact]
    public async Task Offline_says_so_in_the_report()
    {
        var probed = await _hub.CreateClient().GetFromJsonAsync(Routes.Doctor, MuthurJsonContext.Default.DoctorDto);
        Assert.True(probed!.Probed, "a report probes unless it is told not to");

        var offline = await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe=false", MuthurJsonContext.Default.DoctorDto);
        Assert.False(offline!.Probed);
    }

    [Fact]
    public void A_status_goes_on_the_wire_lowercase()
    {
        var json = JsonSerializer.Serialize(
            new CheckDto("project", "scratch", CheckStatus.Ok, "Gated by win-validator."),
            MuthurJsonContext.Default.CheckDto);

        // The CLI and the panel read this text, not the enum: "Ok" would be a different wire contract.
        Assert.Contains("\"status\":\"ok\"", json);
        Assert.DoesNotContain("\"Ok\"", json);
    }
}
