using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The founder console. T-24 is marked <c>attended</c> because every control on this dashboard rides a SignalR
/// circuit — <c>App.razor</c> renders the routes interactively — so nothing here can press <em>Register</em> and
/// watch an agent appear; a human confirms that half. What a machine still checks is everything short of the
/// click: that the page is served, that the control and its labels are in the fetched HTML, that no secret is
/// ever rendered into it, and that the service behind the button does what the button claims. The third of
/// those drives <see cref="AgentService"/> directly rather than the page, because the token contract is the
/// service's and is testable where it lives.
/// </summary>
public sealed class DashboardConsoleTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private AgentService Agents => _hub.Services.GetRequiredService<AgentService>();

    private HarnessService Harnesses => _hub.Services.GetRequiredService<HarnessService>();

    private Task<string> PageAsync() => _hub.CreateClient().GetStringAsync("/console");

    // -- the page and its tab ----------------------------------------------------------------------------------

    /// <summary>The Agents control, whole: every input the founder fills, labelled, and the button.</summary>
    [Fact]
    public async Task The_page_renders_the_agents_control_with_every_label_and_its_button()
    {
        var response = await _hub.CreateClient().GetAsync("/console");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("An unhandled error", page);
        Assert.Contains("<title>MUTHUR · Console</title>", page);

        Assert.Contains("class=\"panel-title\">Agents<", page);
        foreach (var label in new[] { "Name", "Harness", "Model", "Tier", "Account" })
            Assert.Matches($"<label for=\"agent-[a-z]+\">{label}</label>", page);
        Assert.Matches("<button class=\"btn btn-go\"[^>]*>Register</button>", page);

        // Harness and tier are typed with suggestions behind them, not chosen from a closed set. The service
        // takes any non-empty harness, and a <select> here would be quietly narrower than the API behind it.
        Assert.Matches("<input id=\"agent-harness\"[^>]*list=\"agent-harnesses\"", page);
        Assert.Matches("<input id=\"agent-tier\"[^>]*list=\"agent-tiers\"", page);
        Assert.Contains("<datalist id=\"agent-harnesses\">", page);
        Assert.Contains("<datalist id=\"agent-tiers\">", page);
        Assert.DoesNotContain("<select", AgentsSection(page), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Agents section alone, which is what the rule above is about. Harness and tier are open sets, and a
    /// closed control would be narrower than the API behind them; later sections on this page choose from sets
    /// that really are closed — a project's land mode is MUTHUR's own two-member enum, and nothing else parses —
    /// so a &lt;select&gt; is the right control there and this assertion must not reach it.
    /// </summary>
    private static string AgentsSection(string page)
    {
        var start = page.IndexOf("class=\"panel-title\">Agents<", StringComparison.Ordinal);
        Assert.True(start >= 0, "the console renders no Agents section");
        var end = page.IndexOf("<section", start, StringComparison.Ordinal);
        return end < 0 ? page[start..] : page[start..end];
    }

    /// <summary>
    /// Where the suggestions come from, which is the only reason this page can offer any without naming a
    /// harness itself: the founder's own catalog, read through the service <c>muthur harness tiers</c> reads
    /// it with, and the harnesses the agents in the ledger were actually registered on. Config and ledger —
    /// so the list is what this organization uses rather than what was true when the file was written.
    /// </summary>
    [Fact]
    public async Task The_harness_and_tier_suggestions_come_from_the_catalog_and_from_the_agents_already_registered()
    {
        var catalog = await Harnesses.TiersAsync();
        var fromCatalog = catalog.SelectMany(t => t.Candidates).Select(c => c.Harness).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(fromCatalog);
        Assert.NotEmpty(catalog);

        var before = await PageAsync();

        foreach (var harness in fromCatalog) Assert.Contains($"<option value=\"{harness}\"></option>", before);
        foreach (var tier in catalog.Select(t => t.Tier)) Assert.Contains($"<option value=\"{tier}\"></option>", before);

        // A harness the catalog has never heard of is the case a closed list would have shut the founder out
        // of. The service takes it, so from then on the console offers it too rather than pretending that a
        // harness this organization demonstrably runs on does not exist.
        Assert.DoesNotContain("<option value=\"handwritten\"></option>", before);
        await Agents.RegisterAsync(Caller.Founder, new RegisterAgentRequest("scribe", "handwritten", "quill", "scriptorium"));

        var after = await PageAsync();
        Assert.Contains("<option value=\"handwritten\"></option>", after);
        Assert.Contains("<option value=\"scriptorium\"></option>", after);
    }

    /// <summary>
    /// A catalog the founder has mistyped costs suggestions and nothing else. The Tiers panel on /operations
    /// is where that error is reported; a console that went down with it would take away the page the founder
    /// would use to register the agent that fixes it.
    /// </summary>
    [Fact]
    public async Task A_broken_harness_catalog_costs_suggestions_rather_than_the_page()
    {
        await Agents.RegisterAsync(Caller.Founder, new RegisterAgentRequest("scribe", "handwritten", "quill", "scriptorium"));
        Harnesses.EnsureCatalogExists();
        await File.WriteAllTextAsync(Path.Combine(_hub.DataDir, MuthurEnvironment.HarnessFile), "{ not json at all");

        var response = await _hub.CreateClient().GetAsync("/console");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("An unhandled error", page);
        Assert.Matches("<button class=\"btn btn-go\"[^>]*>Register</button>", page);
        // What the ledger knows still suggests; only the catalog's half of the list is gone.
        Assert.Contains("<option value=\"handwritten\"></option>", page);
    }

    /// <summary>
    /// The tab is on every page, and carries no count. A badge in this dashboard means something needs the
    /// founder; the console is where they go to act, and it is never waiting on anyone.
    /// </summary>
    [Fact]
    public async Task The_tab_is_on_every_page_without_a_badge()
    {
        foreach (var elsewhere in new[] { "/", "/needs-you", "/operations", "/projects", "/receipts" })
        {
            var other = await _hub.CreateClient().GetStringAsync(elsewhere);
            Assert.Contains("href=\"/console\"", other);
            Assert.Contains(">Console<", other);
        }

        Assert.DoesNotContain("Console<span", await PageAsync());
    }

    /// <summary>
    /// Nothing on the page is styled where it is written. Nothing catches a class that app.css does not define —
    /// the browser simply draws it unstyled — so the stylesheet is read and checked here instead, and an inline
    /// style is checked for at the same time because it is the other way a control ends up styled off-sheet.
    /// </summary>
    [Fact]
    public async Task Every_class_the_page_renders_is_defined_in_app_css_and_nothing_is_styled_inline()
    {
        var css = await File.ReadAllTextAsync(AppCss());

        var page = await PageAsync();

        foreach (var name in ClassesIn(page))
            Assert.True(css.Contains("." + name, StringComparison.Ordinal),
                $"the console renders class '{name}', which app.css does not define. Styling uses only classes " +
                "from that file, so the class belongs in it — never inline on the element.");
        Assert.DoesNotContain("style=\"", Body(page), StringComparison.Ordinal);
    }

    // -- the token contract, driven through the service -------------------------------------------------------

    /// <summary>
    /// The one secret this page ever handles. Registering hands back a token exactly once — it appears in the
    /// response and in no other field of it — and it is never readable again: the hub keeps a hash, so listing
    /// the agents cannot return it and re-registering mints a different one. The page holds it in component
    /// state for the single render that shows it, which is why a fetched <c>/console</c> carries no token at all.
    /// </summary>
    [Fact]
    public async Task A_registered_agents_token_is_returned_once_and_is_never_readable_again()
    {
        var registered = await Agents.RegisterAsync(
            Caller.Founder, new RegisterAgentRequest("corner", "codex", "gpt-6-astra", "mastermind", "work@example.com"));
        var token = registered.Token;

        Assert.NotEmpty(token);
        Assert.Equal("corner", registered.Agent.Name);

        // Once in the response: the Token field carries it, and nothing on the agent beside it repeats it.
        var response = JsonSerializer.Serialize(registered, MuthurJsonContext.Default.RegisterAgentResponse);
        Assert.Single(Regex.Matches(response, Regex.Escape(token)));
        Assert.DoesNotContain(token, JsonSerializer.Serialize(registered.Agent, MuthurJsonContext.Default.AgentDto), StringComparison.Ordinal);

        // And never again, however the agent is read back afterwards.
        var listed = await Agents.ListAsync();
        Assert.Equal("corner", Assert.Single(listed).Name);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(listed, MuthurJsonContext.Default.IReadOnlyListAgentDto), StringComparison.Ordinal);

        // Re-registering the same name is how a lost token is replaced, and what comes back is a new one.
        var again = await Agents.RegisterAsync(
            Caller.Founder, new RegisterAgentRequest("corner", "codex", "gpt-6-astra", "mastermind", "work@example.com"));
        Assert.NotEqual(token, again.Token);

        // The page itself is served without a circuit, so it can hold no token — before or after a registration.
        Assert.DoesNotContain(token, await PageAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(again.Token, await PageAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// What the founder is handed is the service's own refusal, in the service's own words. The console never
    /// invents wording for a rule, so the page and the CLI say the same thing about the same mistake.
    /// </summary>
    [Fact]
    public async Task A_refused_registration_carries_the_services_own_message()
    {
        var refused = await Assert.ThrowsAsync<Muthur.Core.MuthurException>(() =>
            Agents.RegisterAsync(Caller.Founder, new RegisterAgentRequest("Corner Office", "claude", "fable", "mastermind")));

        Assert.Equal("invalid_name", refused.Code);
        Assert.Contains("Agent names are", refused.Message);
        Assert.Empty(await Agents.ListAsync());
    }

    // -- helpers ------------------------------------------------------------------------------------------------

    private static IEnumerable<string> ClassesIn(string markup) =>
        Regex.Matches(markup, "class=\"([^\"]*)\"")
            .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal);

    /// <summary>The page without its head, so the framework's own markup is not read as the console's styling.</summary>
    private static string Body(string page)
    {
        var body = page.IndexOf("<body", StringComparison.Ordinal);
        Assert.True(body >= 0, "the page renders no body at all");
        return page[body..];
    }

    /// <summary>The stylesheet the server serves, found by walking up from the test binary to the repository.</summary>
    private static string AppCss()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Muthur.Server", "wwwroot", "app.css");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"No src/Muthur.Server/wwwroot/app.css walking up from {AppContext.BaseDirectory}.");
    }
}
