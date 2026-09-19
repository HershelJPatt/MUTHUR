using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>A full hub over an isolated temp data directory with a controllable clock.</summary>
public sealed class HubFactory : WebApplicationFactory<Program>
{
    /// <summary>Settable so a test can bring a second hub up over the same database, as a restart does.</summary>
    public string DataDir { get; init; } = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
    public ObservingClock Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Extra configuration for a test class, e.g. ["Muthur:RequireCrossProviderReview"] = "true".</summary>
    public Dictionary<string, string> Settings { get; } = [];

    public FakePullRequestOpener PullRequests { get; } = new();
    public FakeInboundSource Source { get; } = new();
    public FakeValidatorSessions Validators { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Muthur:DataDir", DataDir);
        builder.UseSetting("Muthur:BackgroundServices", "false");
        // Pooling off, not pool-clearing: the process-global ClearAllPools() disposed connections out from
        // under hubs that were still serving, which failed an arbitrary test with a 500 under load.
        builder.UseSetting("Muthur:ConnectionString",
            $"Data Source={Path.Combine(DataDir, MuthurEnvironment.DatabaseFile)};Pooling=False");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IPullRequestOpener>();
            services.AddSingleton<IPullRequestOpener>(PullRequests);
            services.AddSingleton<IInboundSource>(Source);
            services.RemoveAll<IValidatorSessionLauncher>();
            services.AddSingleton<IValidatorSessionLauncher>(Validators);
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
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A fake clock that also records what has started waiting on it. A test cannot otherwise observe that a
/// long-poll request has reached the hub, and the request's own wait is what creates the timer.
/// </summary>
public sealed class ObservingClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly List<TimeSpan> _timers = [];

    /// <summary>The due time of every timer created on this clock, in the order they were created.</summary>
    public IReadOnlyList<TimeSpan> Timers { get { lock (_timers) return [.. _timers]; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers) _timers.Add(dueTime);
        return base.CreateTimer(callback, state, dueTime, period);
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

    public Task ProbeAsync(string location, CancellationToken ct = default) =>
        FailWith is not null ? throw new InvalidOperationException(FailWith) : Task.CompletedTask;
}

/// <summary>Records what the conductor asked to start, without starting anything.</summary>
public sealed class FakeValidatorSessions : IValidatorSessionLauncher
{
    private readonly List<ConductorAssignment> _started = [];
    private readonly SemaphoreSlim _release = new(0);

    /// <summary>When true, a started session blocks until <see cref="Finish"/>, so a test can hold the budget full.</summary>
    public bool Block { get; set; }

    public IReadOnlyList<ConductorAssignment> Started { get { lock (_started) return [.. _started]; } }

    /// <summary>When set, every start throws it — a harness that is not installed, or no candidate left.</summary>
    public Exception? Throw { get; set; }

    /// <summary>When set, the real launcher answers instead, so a test can see how it actually fails.</summary>
    public IValidatorSessionLauncher? Delegate { get; set; }

    public async Task StartAsync(ConductorAssignment assignment, CancellationToken ct = default)
    {
        lock (_started) _started.Add(assignment);
        if (Throw is { } failure) throw failure;
        if (Delegate is { } real) await real.StartAsync(assignment, ct);
        if (Block) await _release.WaitAsync(ct);
    }

    public void Finish(int count = 1) => _release.Release(count);
}
