using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// The Receipts page — what the organization spent itself on, rendered by the real dashboard. The numbers are
/// Unit B's; what is tested here is that the page shows them the way the founder asked to read them, which for
/// money means on the row that reported it, naming its harness, and totalled nowhere.
/// </summary>
public sealed class DashboardReceiptsTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private bool _project;

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private Task<string> PageAsync(int? hours = null) =>
        _hub.CreateClient().GetStringAsync("/receipts" + (hours is { } h ? $"?hours={h}" : ""));

    // -- the fixture, the same spend Unit B's tests read as JSON ------------------------------------------------

    private async Task<HttpClient> RegisterAsync(string name, string harness, string model, string tier, string account)
    {
        var response = await _hub.CreateClient()
            .PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest(name, harness, model, tier, account));
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse))!;
        return _hub.CreateClient(registered.Token);
    }

    /// <summary>The one project, added once however many fixtures a test builds on top of each other.</summary>
    private async Task ProjectAsync(string[]? validators = null)
    {
        if (_project) return;
        _project = true;
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators ?? []);
    }

    private async Task<(HttpClient Owner, HttpClient Validator)> CastAsync()
    {
        await ProjectAsync(["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles,
            new DefineRoleRequest("win-validator", "# win-validator\nDrive it."))).EnsureSuccessStatusCode();
        var owner = await RegisterAsync("owner", "claude", "opus", "deep", "founder@example.com");
        var validator = await RegisterAsync("checker", "codex", "gpt", "cheap", "work@example.com");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        return (owner, validator);
    }

    private static string Branch(string taskId) => $"task/{taskId}-work";

    private async Task<string> ImplementableAsync(HttpClient owner, string title)
    {
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(Branch(task.Id), $"{task.Id}.txt", "work\n");
        return task.Id;
    }

    private static async Task ImplementedAsync(HttpClient owner, string taskId) =>
        (await owner.PostActionAsync(taskId, "implemented", new ImplementedRequest(Branch(taskId)))).EnsureSuccessStatusCode();

    private static async Task FailAsync(HttpClient validator, string taskId) =>
        (await validator.PostActionAsync(taskId, "fail", new VerdictRequest("win-validator", "the export still 500s"))).EnsureSuccessStatusCode();

    /// <summary>
    /// Two tasks, one of which cost far more than the other, and twenty minutes spent waiting on a verdict. The
    /// clock is stepped rather than waited on, so the twenty minutes is exactly twenty minutes and stays inside
    /// the thirty-minute claim and role leases.
    /// </summary>
    private async Task<(string Busy, string Quiet)> SpendAsync()
    {
        var (owner, validator) = await CastAsync();
        var busy = await ImplementableAsync(owner, "The one that took eleven sessions");
        var quiet = await ImplementableAsync(owner, "The one that went through first time");

        await ImplementedAsync(owner, busy);
        _hub.Clock.Advance(TimeSpan.FromMinutes(20));
        await FailAsync(validator, busy);
        await ImplementedAsync(owner, busy);
        await FailAsync(validator, busy);

        await ImplementedAsync(owner, quiet);
        await FailAsync(validator, quiet);
        return (busy, quiet);
    }

    /// <summary>Two runs on two harnesses, one of which reported what it cost and one of which reports nothing.</summary>
    private async Task<string> RunsAsync()
    {
        await ProjectAsync();
        var owner = await RegisterAsync("runner", "claude", "opus", "deep", "founder@example.com");
        var task = await owner.AddTaskAsync("Run the worker");
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "cheap", "codex", "gpt", "work@example.com", Branch(task.Id), "unit-a", true, 90, 0.42m))).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "deep", "claude", "opus", "founder@example.com", Branch(task.Id), "unit-b", false, 30, null))).EnsureSuccessStatusCode();
        return task.Id;
    }

    // -- the acceptance ----------------------------------------------------------------------------------------

    /// <summary>
    /// The page renders and the tab is on every page — without a badge. A badge in this dashboard means something
    /// needs the founder, and receipts never needs anyone; a count there would cry wolf every time a session ran.
    /// </summary>
    [Fact]
    public async Task The_page_renders_and_its_tab_is_on_every_page_without_a_badge()
    {
        var response = await _hub.CreateClient().GetAsync("/receipts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains(">Receipts<", page);
        Assert.Contains("24h", page);
        foreach (var elsewhere in new[] { "/", "/operations", "/projects", "/stream" })
        {
            var other = await _hub.CreateClient().GetStringAsync(elsewhere);
            Assert.Contains("href=\"/receipts\"", other);
            Assert.Contains(">Receipts<", other);
        }

        // The tab carries no count of its own: a badge here would mean the founder is needed, and they are not.
        Assert.DoesNotContain("Receipts<span", page);
    }

    /// <summary>A hub that has spent nothing says so, rather than throwing over the empty lists.</summary>
    [Fact]
    public async Task A_hub_with_nothing_in_it_renders_the_empty_state()
    {
        var response = await _hub.CreateClient().GetAsync("/receipts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("nothing spent in this window", page);
        Assert.DoesNotContain("where the time went", page);
        Assert.DoesNotContain("An unhandled error", page);
    }

    /// <summary>
    /// The row that took eleven sessions is visibly the first one, and beside it the answer T-17 exists for: how
    /// long the work sat waiting on a verdict.
    /// </summary>
    [Fact]
    public async Task The_spend_renders_in_order_with_the_time_in_each_state_and_the_accounts_that_took_it()
    {
        var (busy, quiet) = await SpendAsync();

        var page = await PageAsync();

        Assert.Contains(busy, page);
        Assert.Contains(quiet, page);
        // By title, because a task id also appears above as the longest stay in a state.
        Assert.True(
            page.IndexOf("The one that took eleven sessions", StringComparison.Ordinal)
            < page.IndexOf("The one that went through first time", StringComparison.Ordinal),
            "the task that consumed the most must be the first row on the page, not a thing the founder has to find");

        // Where the time went, with the state named as the wire spells it and the longest stay in it.
        Assert.Contains("where the time went", page);
        Assert.Contains("class=\"pill\">validating<", page);
        Assert.Contains("class=\"pill\">in_progress<", page);
        Assert.Contains("20m across", page);
        Assert.Contains("longest 20m", page);

        // Which accounts the sessions were taken on, at which tier, on which harness.
        Assert.Contains("by account", page);
        Assert.Contains("founder@example.com", page);
        Assert.Contains("work@example.com", page);
        Assert.Contains("claude/opus", page);
        Assert.Contains("codex/gpt", page);

        // And what each task cost in sessions and failed verdicts.
        Assert.Contains("2 failed", page);
        Assert.Contains("1 failed", page);
    }

    /// <summary>
    /// The window is the question the page asks. Two days after the spend, 24h answers nothing and 7d answers all
    /// of it — the same page, re-read, with different numbers, and the button for the window in force marked.
    /// </summary>
    [Fact]
    public async Task Switching_the_window_to_seven_days_re_reads_and_changes_the_numbers()
    {
        var (busy, _) = await SpendAsync();
        _hub.Clock.Advance(TimeSpan.FromDays(2));

        var day = await PageAsync();
        var week = await PageAsync(168);

        Assert.Contains("nothing spent in this window", day);
        Assert.DoesNotContain("The one that took eleven sessions", day);

        Assert.DoesNotContain("nothing spent in this window", week);
        Assert.Contains(busy, week);
        Assert.Contains("The one that took eleven sessions", week);

        // The selector says which window is in force, and it is not the same one on both pages.
        Assert.Matches("class=\"btn btn-on\"[^>]*>24h<", day);
        Assert.Matches("class=\"btn btn-on\"[^>]*>7d<", week);
        Assert.DoesNotMatch("class=\"btn btn-on\"[^>]*>7d<", day);
    }

    /// <summary>
    /// The window selector is a link, not a script. The window already lives in the query string, so each control
    /// is the URL that holds it — which means anything that can fetch the page can follow it, and the window a
    /// founder is reading is one they can send. A button renders identically to a browser and is invisible to
    /// everything else, which is how a green suite once missed that these were buttons.
    /// </summary>
    [Fact]
    public async Task Every_window_is_an_anchor_to_its_own_url_and_the_row_holds_no_button()
    {
        foreach (var requested in new int?[] { null, 24, 168, 720 })
        {
            var row = WindowRow(await PageAsync(requested));

            foreach (var (hours, label) in new[] { (24, "24h"), (168, "7d"), (720, "30d") })
                Assert.Matches($"<a [^>]*href=\"/receipts\\?hours={hours}\"[^>]*>{label}</a>", row);
            Assert.DoesNotContain("<button", row, StringComparison.OrdinalIgnoreCase);

            // And the window in force is marked on the anchor that leads back to it, not on some other control.
            Assert.Matches($"<a class=\"btn btn-on\"[^>]*href=\"/receipts\\?hours={requested ?? 24}\"", row);
        }
    }

    /// <summary>The selector row alone, so "no button here" means in the selector and not elsewhere on the page.</summary>
    private static string WindowRow(string page)
    {
        var row = Regex.Match(page, "<div class=\"btn-row\">.*?</div>", RegexOptions.Singleline);
        Assert.True(row.Success, "the page renders no window selector at all");
        return row.Value;
    }

    /// <summary>
    /// Founder request #13, on the page. Cost sits on the worker-run row that reported it, that row names the
    /// harness so a blank reads as claude rather than as a bug, the section says out loud what it is not counting,
    /// and there is no total anywhere — which is what the second currency figure would be.
    /// </summary>
    [Fact]
    public async Task Money_is_on_the_run_that_reported_it_named_by_harness_and_totalled_nowhere()
    {
        await RunsAsync();

        var page = await PageAsync();

        // Each row names its harness, so a reader seeing a blank cost can tell the blank is claude, not a bug.
        Assert.Contains("class=\"receipt-title\">claude/opus", page);
        Assert.Contains("class=\"receipt-title\">codex/gpt", page);
        // Newest first, told by the unit each run worked, which only the run rows carry.
        Assert.True(page.IndexOf("unit-b", StringComparison.Ordinal) < page.IndexOf("unit-a", StringComparison.Ordinal),
            "worker runs render newest first");

        // The one that reported a cost shows it; the one that reported none shows an absence, not a zero.
        Assert.Contains("class=\"receipt-cost\">$0.42<", page);
        Assert.Contains("class=\"receipt-none\">—<", page);
        Assert.DoesNotContain("$0.00", page);

        // Where a column would naturally total, the page says what is not counted instead.
        Assert.Contains("conductor validator sessions report no cost", page);

        // And nothing totals it. One run reported money, so exactly one currency figure may appear on the page;
        // a second one is a sum, and a sum that omits every claude session and every conductor-started validator
        // is a confident answer to a question nobody asked.
        var figures = Regex.Matches(page, @"\$\d+\.\d\d");
        Assert.True(figures.Count == 1,
            $"the page may show cost on the run that reported it and nowhere else, but it shows: " +
            $"{string.Join(", ", figures.Select(f => f.Value))}. Founder request #13 settled that costUsd is never " +
            "totalled: the runs that report nothing are not a random sample, they are the largest category.");
    }

    /// <summary>
    /// Every class the page renders is defined in app.css. Nothing catches a class that does not exist at runtime —
    /// the browser simply draws it unstyled — so the stylesheet is read and checked here instead.
    /// </summary>
    [Fact]
    public async Task Every_class_the_page_renders_is_defined_in_app_css()
    {
        var css = await File.ReadAllTextAsync(AppCss());

        // One page with every section on it, and one with none of them, so both branches are covered.
        await SpendAsync();
        await RunsAsync();
        var agent = await RegisterAsync("limited", "codex", "gpt", "cheap", "work@example.com");
        (await agent.PostAsJsonAsync(Routes.AccountLimits,
            new AccountLimitRequest("work@example.com", _hub.Clock.GetUtcNow().AddHours(2)))).EnsureSuccessStatusCode();
        var full = await PageAsync();
        using var bare = new HubFactory();
        var empty = await bare.CreateClient().GetStringAsync("/receipts");

        Assert.Contains("conductor validator sessions report no cost", full);   // the fixture really did render it all
        Assert.Contains("nothing spent in this window", empty);
        var classes = ClassesIn(full).Union(ClassesIn(empty), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.Contains("receipt-cost", classes);
        foreach (var name in classes)
            Assert.True(css.Contains("." + name, StringComparison.Ordinal),
                $"the dashboard renders class '{name}', which app.css does not define. Styling uses only classes " +
                "from that file, so the class belongs in it — never inline on the element.");
    }

    private static IEnumerable<string> ClassesIn(string markup) =>
        Regex.Matches(markup, "class=\"([^\"]*)\"")
            .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal);

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
