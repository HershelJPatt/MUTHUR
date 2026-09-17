using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class SystemTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task Status_reports_instance_and_uses_isolated_data_dir()
    {
        var status = await _hub.CreateClient().GetFromJsonAsync(Routes.Status, MuthurJsonContext.Default.StatusResponse);

        Assert.NotNull(status);
        Assert.Equal("MUTHUR", status.Name);
        Assert.Equal(_hub.DataDir, status.DataDirectory);
        Assert.Equal(32, status.InstanceId.Length);
        Assert.Equal(_hub.Clock.GetUtcNow(), status.Now);
        Assert.True(File.Exists(Path.Combine(_hub.DataDir, MuthurEnvironment.DatabaseFile)));
    }

    [Fact]
    public async Task Shutdown_requires_founder_token()
    {
        var anonymous = await _hub.CreateClient().PostAsync(Routes.Shutdown, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var error = await anonymous.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ErrorResponse);
        Assert.Equal("unauthorized", error!.Code);

        var wrong = await _hub.CreateClient("not-the-token").PostAsync(Routes.Shutdown, null);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }
}
