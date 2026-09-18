using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class DoctorTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task A_hub_with_nothing_to_check_answers_anyone_with_an_empty_report()
    {
        var response = await _hub.CreateClient().GetAsync(Routes.Doctor);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.DoctorDto);
        Assert.Empty(report!.Checks);
        Assert.Equal(0, report.Ok);
        Assert.Equal(0, report.Warn);
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
