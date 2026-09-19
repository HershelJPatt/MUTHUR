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

        // The harness is chosen, not typed: a misspelled harness registers an agent whose kit does not exist.
        foreach (var harness in new[] { "claude", "codex", "generic" })
            Assert.Matches($"<option value=\"{harness}\"[^>]*>{harness}</option>", page);
        foreach (var tier in new[] { "mastermind", "implementer", "utility" })
            Assert.Matches($"<option value=\"{tier}\"[^>]*>{tier}</option>", page);
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
