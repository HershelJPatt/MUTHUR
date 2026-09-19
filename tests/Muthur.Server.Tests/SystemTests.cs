using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muthur.Contracts;
using Muthur.Server.Infrastructure;
using Muthur.Server.Services;

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

    // ---- T-41: what a request in flight when the hub stops is told --------------------------------------
    //
    // A hub that has been asked to stop disposes its database connection and its service provider, and every
    // request still inside it used to be answered "500 internal_error" — the hub telling its own agents that
    // it broke, when what happened is that it was told to stop.

    [Fact]
    public async Task A_request_to_a_hub_that_is_stopping_is_refused_not_blamed()
    {
        using var hub = StoppingHub(out var founder, out var release);
        try
        {
            // The endpoint T-41 was filed against; any ordinary write would do just as well.
            var response = await DefineTargetAsync(founder);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await response.ReadErrorAsync();
            Assert.Equal("hub_stopping", error.Code);
            Assert.Contains("muthur up", error.Message);
        }
        finally
        {
            release();
        }
    }

    [Fact]
    public async Task Refusing_a_request_because_the_hub_is_stopping_is_not_logged_as_an_error()
    {
        using var hub = StoppingHub(out var founder, out var release);
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await DefineTargetAsync(founder)).StatusCode);

            // An error line here is the same false alarm in the log that the 500 was on the wire — and
            // searching hub logs for exactly this line is how T-41 was found in the first place.
            Assert.DoesNotContain(nameof(ErrorMiddleware), ReadHubLog(), StringComparison.Ordinal);
        }
        finally
        {
            release();
        }
    }

    [Fact]
    public async Task A_long_poll_is_never_told_the_hub_broke()
    {
        var gate = new StopGate();
        using var hub = Gated(gate);
        var waiter = await RegisterAsync(hub, "idle");
        var waiting = waiter.GetAsync($"{Routes.Inbox}?wait=900");

        // The hub is told to stop only once the request is really parked in the long poll. The 900-second
        // timer that wait creates on the hub's clock is the only thing a test can observe about a request
        // which, by then, is doing nothing at all — so there is no sleep here and no guess about timing.
        await Eventually.TrueAsync(() => _hub.Clock.Timers.Contains(TimeSpan.FromSeconds(900)),
            "the inbox never reached its 900-second wait on the hub's clock");
        Assert.False(waiting.IsCompleted);

        hub.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        try
        {
            using var response = await Eventually.CompletesAsync(waiting, "the inbox never answered after the hub stopped");
            // Both sides of this race are correct. The long poll ends its own wait on the stopping signal, so
            // an empty, timed-out inbox is the good answer; a request that loses the race is refused 503.
            // The only wrong answer is the one that says the hub broke.
            Assert.False(response.StatusCode is HttpStatusCode.InternalServerError,
                $"the inbox was told the hub broke: {await response.Content.ReadAsStringAsync()}");
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable)
            {
                Assert.Equal("hub_stopping", (await response.ReadErrorAsync()).Code);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True((await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.InboxDto))!.TimedOut);
            }

            // The next agent to sit down in the long poll — the request the hub's own log caught fifteen
            // times out of thirty-two — is refused rather than served by a hub on its way out.
            using var next = await waiter.GetAsync($"{Routes.Inbox}?wait=900");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, next.StatusCode);
            Assert.Equal("hub_stopping", (await next.ReadErrorAsync()).Code);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task A_request_overtaken_by_the_stop_is_refused_rather_than_blamed()
    {
        // The guard at the front of the middleware cannot cover a request that was already past it, so the
        // same answer is given to an ObjectDisposedException raised while the hub is stopping. Here the
        // failing dependency raises the stopping signal itself, which puts the request squarely inside that
        // window every time instead of hoping to land in it.
        var gate = new StopGate();
        using var hub = Gated(gate, s => s.AddSingleton<IOutboundChannel>(
            sp => new DisposedChannel(sp.GetRequiredService<IHostApplicationLifetime>())));
        try
        {
            var response = await DefineTargetAsync(Founder(hub), DisposedChannel.ChannelName);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("hub_stopping", (await response.ReadErrorAsync()).Code);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task A_disposed_object_on_a_hub_that_is_not_stopping_is_still_an_internal_error()
    {
        // This is the test that keeps the fix from being a silencer. The exception is the very one the
        // shutdown race raises; the only difference is that nothing has asked this hub to stop. That
        // difference is the second condition on the catch, and without it every genuinely disposed object —
        // a disposed HttpClient, a captured scoped service, a CancellationTokenSource used after disposal —
        // would stop being a 500 with a stack in the log and start saying "the hub is shutting down".
        using var hub = _hub.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IOutboundChannel>(new DisposedChannel(stopFirst: null))));

        var response = await DefineTargetAsync(Founder(hub), DisposedChannel.ChannelName);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", (await response.ReadErrorAsync()).Code);
        Assert.Contains(nameof(ErrorMiddleware), ReadHubLog(), StringComparison.Ordinal);
    }

    /// <summary>The target definition T-41 was filed against.</summary>
    private Task<HttpResponseMessage> DefineTargetAsync(HttpClient founder, string channel = "file") =>
        founder.PutAsJsonAsync(Routes.OutboundTargets, new DefineTargetRequest("news", channel, Path.Combine(_hub.DataDir, "news.txt")));

    /// <summary>A hub that has been asked to stop and has not finished stopping, with a founder client on it.</summary>
    private WebApplicationFactory<Program> StoppingHub(out HttpClient founder, out Action release)
    {
        var gate = new StopGate();
        var hub = Gated(gate);
        founder = Founder(hub);
        hub.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        release = gate.Release;
        return hub;
    }

    private WebApplicationFactory<Program> Gated(StopGate gate, Action<IServiceCollection>? also = null) =>
        _hub.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IHostedService>(gate);
            also?.Invoke(s);
        }));

    /// <summary>A founder client on a hub built by <c>WithWebHostBuilder</c>, which keeps this data directory.</summary>
    private HttpClient Founder(WebApplicationFactory<Program> hub)
    {
        _ = hub.Services; // starting the host is what writes the founder token
        var client = hub.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", File.ReadAllText(Path.Combine(_hub.DataDir, MuthurEnvironment.FounderTokenFile)).Trim());
        return client;
    }

    private static async Task<HttpClient> RegisterAsync(WebApplicationFactory<Program> hub, string name)
    {
        var client = hub.CreateClient();
        var response = await client.PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest(name, "claude", "opus", null));
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered!.Token);
        return client;
    }

    /// <summary>The hub's own log, read while the hub still holds it open for writing.</summary>
    private string ReadHubLog()
    {
        var path = Path.Combine(_hub.DataDir, MuthurEnvironment.LogFile);
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Holds a shutdown at the only point where any of this matters: the stopping signal is up and every
    /// request in the hub is on borrowed time, but nothing has been disposed yet. Hosted services are stopped
    /// before the server is, so the hub keeps answering until <see cref="Release"/>. Without this the test
    /// host finishes tearing itself down first and the request meets a disposed TestServer, which is not
    /// something any hub could answer.
    /// </summary>
    private sealed class StopGate : IHostedService
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => _released.Task.WaitAsync(ct);

        /// <summary>Lets the shutdown finish — and lets it finish again, since disposal stops the host a second time.</summary>
        public void Release() => _released.TrySetResult();
    }

    /// <summary>
    /// What `muthur receipts` sends on a hub that has done nothing yet. It carries no token and asks for no
    /// window, and the answer has to be a 200 with zeros — the command exits on the status, so an empty hub
    /// answering anything but 200 would make a founder's first receipts run look like a broken hub.
    /// </summary>
    [Fact]
    public async Task Receipts_answers_an_empty_hub_without_a_token()
    {
        var response = await _hub.CreateClient().GetAsync(Routes.Receipts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipts = await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto);
        Assert.NotNull(receipts);
        Assert.Equal(0, receipts.Sessions);
        Assert.Empty(receipts.Tasks);
        Assert.Equal(_hub.Clock.GetUtcNow(), receipts.At);
        Assert.Equal(receipts.At.AddHours(-24), receipts.Since);
    }

    /// <summary>`muthur receipts --hours 6`, spelled the way the CLI spells it, moves the hub's window.</summary>
    [Fact]
    public async Task Receipts_takes_the_window_from_the_query_string()
    {
        var response = await _hub.CreateClient().GetAsync(Routes.Receipts + "?hours=6");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipts = await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ReceiptsDto);
        Assert.Equal(_hub.Clock.GetUtcNow().AddHours(-6), receipts!.Since);
    }

    /// <summary>
    /// A dependency that fails the way a disposed one does, on an endpoint that does not catch it. Given a
    /// lifetime it raises the stopping signal before failing, which is the shutdown race; without one it is
    /// an ordinary defect on a hub nobody has asked to stop.
    /// </summary>
    private sealed class DisposedChannel(IHostApplicationLifetime? stopFirst) : IOutboundChannel
    {
        public const string ChannelName = "disposed";

        public string Name => ChannelName;

        public void Validate(string address, string body)
        {
            stopFirst?.StopApplication();
            throw new ObjectDisposedException("SQLitePCL.sqlite3");
        }

        public Task SendAsync(string address, string body, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ProbeAsync(string address, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
