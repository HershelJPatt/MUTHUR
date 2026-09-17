using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>A full hub over an isolated temp data directory with a controllable clock.</summary>
public sealed class HubFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Muthur:DataDir", DataDir);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public string FounderToken
    {
        get
        {
            _ = Server; // force startup
            return File.ReadAllText(Path.Combine(DataDir, MuthurEnvironment.FounderTokenFile)).Trim();
        }
    }

    public HttpClient CreateClient(string? token)
    {
        var client = CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient Founder() => CreateClient(FounderToken);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
