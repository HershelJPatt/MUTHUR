using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>A full hub over an isolated temp data directory with a controllable clock.</summary>
public sealed class HubFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public FakePullRequestOpener PullRequests { get; } = new();
    public FakeInboundSource Source { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Muthur:DataDir", DataDir);
        builder.UseSetting("Muthur:BackgroundServices", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IPullRequestOpener>();
            services.AddSingleton<IPullRequestOpener>(PullRequests);
            services.AddSingleton<IInboundSource>(Source);
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

public sealed class FakePullRequestOpener : IPullRequestOpener
{
    public List<(string Base, string Head, string Title)> Opened { get; } = [];
    public string? FailWith { get; set; }

    public Task<string> OpenAsync(string repoPath, string baseBranch, string headBranch, string title, string body, CancellationToken ct = default)
    {
        if (FailWith is not null) throw new InvalidOperationException(FailWith);
        Opened.Add((baseBranch, headBranch, title));
        return Task.FromResult($"https://github.com/example/repo/pull/{Opened.Count}");
    }
}

/// <summary>An external source ("fake:anything") that behaves like the real ones: items at or after the cursor, oldest first.</summary>
public sealed class FakeInboundSource : IInboundSource
{
    private readonly List<(IncomingItem Item, string UpdatedAt)> _items = [];

    public string Scheme => "fake";
    public string? FailWith { get; set; }
    public string? LastCursorSeen { get; private set; }

    public void Add(string externalId, string title, string updatedAt) =>
        _items.Add((new IncomingItem(externalId, title, "", null, "someone"), updatedAt));

    public Task<SourceFetch> FetchAsync(string location, string? cursor, CancellationToken ct = default)
    {
        if (FailWith is not null) throw new InvalidOperationException(FailWith);
        LastCursorSeen = cursor;
        var matching = _items.Where(i => cursor is null || string.CompareOrdinal(i.UpdatedAt, cursor) >= 0).OrderBy(i => i.UpdatedAt, StringComparer.Ordinal).ToList();
        var newest = matching.Count > 0 ? matching[^1].UpdatedAt : cursor;
        return Task.FromResult(new SourceFetch(matching.Select(i => i.Item).ToList(), newest));
    }
}
