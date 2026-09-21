using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

public sealed class ProbeAdmissionClientTests
{
    [Fact]
    public void Source_generated_contracts_preserve_the_execution_grant_and_request_identity()
    {
        var request = new ProbeAdmissionRequest("T-1", "worker", "fixture", "model", "account", Guid.NewGuid().ToString("N"));
        var json = JsonSerializer.Serialize(request, MuthurJsonContext.Default.ProbeAdmissionRequest);
        Assert.Equal(request, JsonSerializer.Deserialize(json, MuthurJsonContext.Default.ProbeAdmissionRequest));
        foreach (var granted in new[] { true, false })
        {
            var response = new ProbeAdmissionDto("reservation", request.Task, request.RunId, granted);
            var wire = JsonSerializer.Serialize(response, MuthurJsonContext.Default.ProbeAdmissionDto);
            Assert.Equal(response, JsonSerializer.Deserialize(wire, MuthurJsonContext.Default.ProbeAdmissionDto));
            Assert.Contains("\"mayExecute\":", wire);
        }
        var release = new ProbeReleaseRequest("reservation");
        Assert.Equal(release, JsonSerializer.Deserialize(JsonSerializer.Serialize(release,
            MuthurJsonContext.Default.ProbeReleaseRequest), MuthurJsonContext.Default.ProbeReleaseRequest));
    }

    [Fact]
    public async Task Unreachable_hub_is_a_refusal_and_release_failure_names_reservation()
    {
        Assert.Equal(HubIsolation.UnreachableUrl, MuthurEnvironment.Url);
        var adapter = new ProbeAdmissionClient(new HubClient(null, TimeSpan.FromSeconds(1)));
        var request = new ProbeAdmissionRequest("T-1", "worker", "fixture", "model", "account", Guid.NewGuid().ToString("N"));
        Assert.Contains("refused", (await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.AdmitAsync(request))).Message);
        Assert.Contains("reservation", (await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ReleaseAsync(new("reservation")))).Message);
    }
}
