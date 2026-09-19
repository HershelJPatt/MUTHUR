using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muthur.Server.Infrastructure;

namespace Muthur.Server.Tests;

/// <summary>Which logging providers a running hub registers, and — the point of T-35 — which it does not.</summary>
public sealed class LoggingProviderTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public void A_hub_registers_the_log_file_it_reads_and_no_windows_event_log()
    {
        var providers = _hub.Services.GetServices<ILoggerProvider>().ToList();

        // Matched by type name: this project does not reference Microsoft.Extensions.Logging.EventLog, and taking
        // the reference to write the assertion would add the very dependency the task exists to be rid of.
        Assert.DoesNotContain(providers, p => p.GetType().Name.Contains("EventLog", StringComparison.OrdinalIgnoreCase));

        // Without this the assertion above would pass just as happily on a hub with no logging at all.
        Assert.Contains(providers, p => p is FileLoggerProvider);
    }
}
