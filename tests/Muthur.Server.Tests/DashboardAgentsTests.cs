namespace Muthur.Server.Tests;

public sealed class DashboardAgentsTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task Agents_panel_lists_live_agents_before_stale_ones()
    {
        await _hub.RegisterAgentAsync("zeta");
        _hub.Clock.Advance(TimeSpan.FromMinutes(10));
        await _hub.RegisterAgentAsync("alpha", harness: "codex", model: "gpt-5", tier: "implementer");

        var html = await _hub.CreateClient().GetStringAsync("/");

        Assert.Contains("alpha", html);
        Assert.Contains("codex/gpt-5", html);
        Assert.Contains("implementer", html);
        Assert.Contains("1 live of 2", html);
        Assert.Contains("agent-stale", html);
        Assert.True(html.IndexOf("alpha", StringComparison.Ordinal) < html.IndexOf("zeta", StringComparison.Ordinal));
    }
}
