using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// The Receipts page — what the organization spent itself on, rendered by the real dashboard. The numbers are
/// Unit B's; what is tested here is that the page shows them the way the founder asked to read them, which for
/// money means on the row that reported it, naming its harness, and totalled only beside how much of the window the total covers.
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

    /// <summary>
    /// The page at a window spelled exactly as a hand would type it, junk included — which is the point of a URL
    /// whose controls are links. Unlike <see cref="PageAsync"/> this never goes through an int, so what the server
    /// does with a value it cannot read is the thing under test rather than something the test spells away.
    /// </summary>
    private Task<HttpResponseMessage> TypedAsync(string hours) =>
        _hub.CreateClient().GetAsync($"/receipts?hours={Uri.EscapeDataString(hours)}");

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
        (await validator.PostActionAsync(taskId, "fail", new VerdictRequest("win-validator", "the export still 500s", SubjectId: (await validator.GetTaskAsync(taskId)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();

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

    /// <summary>
    /// T-52: the lit control is the window the page is showing, not the one that was asked for. The service
    /// clamps to 1..720, so an out-of-range request used to render thirty days of receipts with nothing lit
    /// at all — on a page whose whole job is to be read at a glance, leaving a date as the only clue.
    /// </summary>
    [Theory]
    [InlineData("100000", 720)]
    [InlineData("9999999999999", 720)]
    [InlineData("721", 720)]
    public async Task An_out_of_range_window_lights_the_control_it_was_clamped_to(string requested, int lit)
    {
        var row = WindowRow(await (await TypedAsync(requested)).Content.ReadAsStringAsync());

        Assert.Matches($"<a class=\"btn btn-on\"[^>]*href=\"/receipts\\?hours={lit}\"", row);
        Assert.Single(Regex.Matches(row, "btn-on"));   // exactly one, so no second control claims the window too
    }

    /// <summary>
    /// The floor is one hour, and no control names it. Nothing lit is the honest answer there and a different
    /// thing from the ceiling, where a control does name the window the page ended up on.
    /// </summary>
    [Fact]
    public async Task A_window_no_control_names_lights_nothing_rather_than_the_nearest()
    {
        var row = WindowRow(await (await TypedAsync("0")).Content.ReadAsStringAsync());

        Assert.DoesNotContain("btn-on", row);
        foreach (var (hours, label) in new[] { (24, "24h"), (168, "7d"), (720, "30d") })
            Assert.Matches($"<a [^>]*href=\"/receipts\\?hours={hours}\"[^>]*>{label}</a>", row);
    }

    [Theory]
    [InlineData("", 24)]
    [InlineData(" ", 24)]
    [InlineData("7d", 24)]
    [InlineData("24h", 24)]
    [InlineData("30d", 24)]
    [InlineData("abc", 24)]
    [InlineData("1e9", 24)]
    [InlineData("24,168", 24)]
    [InlineData("null", 24)]
    [InlineData("9223372036854775808", 24)]
    [InlineData("0", 1)]
    [InlineData("-9", 1)]
    [InlineData("-9223372036854775808", 1)]
    public async Task Every_invalid_supplied_window_explains_the_actual_window(string hours, int effective)
    {
        var response = await TypedAsync(hours);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Equal($"hours={hours} is not a positive integer number of hours, so this is the last " +
            $"{effective} {(effective == 1 ? "hour" : "hours")}. " +
            "The windows are hours=24 (24h), hours=168 (7d), hours=720 (30d).", Notice(page));
        Assert.Equal(Since(await PageAsync(effective)), Since(page));
        var row = WindowRow(page);
        if (effective == 1) Assert.DoesNotContain("btn-on", row);
        else Assert.Matches("<a class=\"btn btn-on\"[^>]*href=\"/receipts\\?hours=24\"", row);
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("24", 24)]
    [InlineData("168", 168)]
    [InlineData("720", 720)]
    [InlineData("100000", 720)]
    [InlineData("9999999999999", 720)]
    [InlineData("9223372036854775807", 720)]
    public async Task Absent_and_valid_positive_windows_are_quiet(string? hours, int effective)
    {
        var response = hours is null ? await _hub.CreateClient().GetAsync("/receipts") : await TypedAsync(hours);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("class=\"notice\"", page);
        Assert.Equal(Since(await PageAsync(effective)), Since(page));
        Assert.Matches($"<a class=\"btn btn-on\"[^>]*href=\"/receipts\\?hours={effective}\"", WindowRow(page));
    }

    [Fact]
    public async Task The_notice_follows_the_numeric_controls_and_their_links_fix_the_window()
    {
        var page = await (await TypedAsync("7d")).Content.ReadAsStringAsync();
        var row = WindowRow(page);

        Assert.Contains("hours=7d ", Notice(page));
        Assert.DoesNotContain("notice", row);
        Assert.DoesNotContain("<button", row, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(Regex.Escape(row) + "\\s*<div class=\"notice\">[^<]+</div>\\s*<div class=\"stats\">", page);
        Assert.Equal(3, Regex.Matches(row, "<a ").Count);
        foreach (var (hours, label) in new[] { (24, "24h"), (168, "7d"), (720, "30d") })
        {
            var link = Regex.Match(row, $"<a [^>]*href=\"(/receipts\\?hours={hours})\"[^>]*>{label}</a>");
            Assert.True(link.Success);
            Assert.Contains($"hours={hours} ({label})", Notice(page));
            var response = await _hub.CreateClient().GetAsync(link.Groups[1].Value);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var followed = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("class=\"notice\"", followed);
            Assert.Equal(Since(await PageAsync(hours)), Since(followed));
            var followedRow = WindowRow(followed);
            Assert.Matches($"<a class=\"btn btn-on\"[^>]*href=\"/receipts\\?hours={hours}\"", followedRow);
            Assert.Single(Regex.Matches(followedRow, "btn-on"));
        }
    }

    [Fact]
    public async Task The_notice_clips_long_input_and_encodes_markup()
    {
        var longResponse = await TypedAsync(new string('x', 200));
        Assert.Equal(HttpStatusCode.OK, longResponse.StatusCode);
        var longPage = await longResponse.Content.ReadAsStringAsync();
        Assert.StartsWith("hours=" + new string('x', 40) + "… is not", Notice(longPage));
        Assert.DoesNotContain(new string('x', 41), longPage);

        const string payload = "<img src=x onerror=alert(1)>";
        var htmlResponse = await TypedAsync(payload);
        Assert.Equal(HttpStatusCode.OK, htmlResponse.StatusCode);
        var htmlPage = await htmlResponse.Content.ReadAsStringAsync();
        Assert.StartsWith($"hours={payload} is not", Notice(htmlPage));
        Assert.Contains("&lt;img", htmlPage);
        Assert.DoesNotContain("<img", htmlPage, StringComparison.OrdinalIgnoreCase);
    }

    private static string Notice(string page)
    {
        var notice = Regex.Match(page, "<div class=\"notice\">([^<]*)</div>");
        Assert.True(notice.Success, "the invalid window must render a notice");
        return WebUtility.HtmlDecode(notice.Groups[1].Value);
    }

    /// <summary>The selector row alone, so "no button here" means in the selector and not elsewhere on the page.</summary>
    private static string WindowRow(string page)
    {
        var row = Regex.Match(page, "<div class=\"btn-row\">.*?</div>", RegexOptions.Singleline);
        Assert.True(row.Success, "the page renders no window selector at all");
        return row.Value;
    }

    /// <summary>
    /// A window the page cannot read is a window, not a crash. Making the controls links made the URL something a
    /// founder edits by hand, and the first things a hand types are the labels the controls print — "24h", "7d" —
    /// neither of which is a number. The design already says what to do with a window it cannot honour: clamp it,
    /// never refuse it. An unreadable one is the same class of mistake, so it reads as the absent window.
    /// </summary>
    [Theory]
    [InlineData("7d")]              // the label on control 2
    [InlineData("24h")]             // the label on control 1
    [InlineData("abc")]
    [InlineData("1e9")]
    [InlineData("24,168")]          // two windows, pasted
    [InlineData("null")]
    [InlineData(" ")]
    [InlineData("")]                // ?hours= with nothing after it
    [InlineData("9999999999999")]   // a number, but not one an int can hold
    public async Task A_window_the_page_cannot_read_renders_rather_than_erroring(string hours)
    {
        var response = await TypedAsync(hours);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("An unhandled error", await response.Content.ReadAsStringAsync());
    }

    /// <summary>The label the page itself prints is not a number, so it falls back to the default window.</summary>
    [Fact]
    public async Task The_window_the_page_cannot_read_falls_back_to_twenty_four_hours()
    {
        var row = WindowRow(await (await TypedAsync("7d")).Content.ReadAsStringAsync());

        Assert.Matches("<a class=\"btn btn-on\"[^>]*href=\"/receipts\\?hours=24\"", row);
        Assert.DoesNotMatch("class=\"btn btn-on\"[^>]*>7d<", row);
    }

    /// <summary>
    /// A window too large for an int is clamped like any other number too large, because the page saturates it to
    /// int.MaxValue and hands the clamping to <c>ReceiptsService</c>, where the 1..720 bounds live. Read through
    /// the window the panel says it read: the marker stays on the window that was asked for, which for any number
    /// outside the three controls — 9999999999999 as much as 100000 — is none of them.
    /// </summary>
    [Fact]
    public async Task A_window_too_large_for_an_int_is_clamped_to_thirty_days_rather_than_refused()
    {
        await SpendAsync();

        var huge = Since(await (await TypedAsync("9999999999999")).Content.ReadAsStringAsync());

        Assert.Equal(Since(await PageAsync(720)), huge);
        Assert.NotEqual(Since(await PageAsync()), huge);
    }

    /// <summary>The window the panel reports having read, which is the clamped one and not the one requested.</summary>
    private static string Since(string page)
    {
        var since = Regex.Match(page, "<span class=\"panel-sub\">since ([^<]+)</span>");
        Assert.True(since.Success, "the panel does not say which window it read");
        return since.Groups[1].Value;
    }

    /// <summary>A finished conductor session on the ledger, in the shape the conductor records it.</summary>
    private async Task SessionAsync(string taskId, string role, string harness, string model, int seconds, decimal? costUsd, int? inputTokens = null, int? outputTokens = null, string? failureKind = null)
    {
        Assert.True(Wire.TryParseTaskId(taskId, out var id));
        await _hub.Services.GetRequiredService<Muthur.Server.Services.Ledger>().MutateAsync(Muthur.Server.Auth.Caller.Founder, m =>
        {
            m.Record("conductor.session_finished", id, new { role, harness, model, account = "work@example.com", seconds, costUsd, inputTokens, outputTokens, failureKind, started = true, runId = "abc123" });
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Money on the page. Each row shows what it reported and names its harness, so a blank reads as the harness
    /// and not as a bug; the total is shown, and never without the pair that says how many rows it is made of;
    /// and beside each task is the answer the page exists for — what it has cost to land so far.
    /// </summary>
    [Fact]
    public async Task Money_is_totalled_with_its_coverage_and_each_task_says_what_it_cost_to_land()
    {
        var task = await RunsAsync();
        await SessionAsync(task, "#orchestrator", "claude", "opus", 34, 0.31m, 1200, 340);
        await SessionAsync(task, "win-validator", "codex", "gpt", 2700, null, failureKind: "timeout");

        var page = await PageAsync();

        // Each row names its harness, so a reader seeing a blank cost can tell the blank is claude, not a bug.
        Assert.Contains("class=\"receipt-title\">claude/opus", page);
        Assert.Contains("class=\"receipt-title\">codex/gpt", page);
        // Newest first, told by the unit each run worked, which only the run rows carry.
        Assert.True(page.IndexOf("unit-b", StringComparison.Ordinal) < page.IndexOf("unit-a", StringComparison.Ordinal),
            "worker runs render newest first");

        // The one that reported a cost shows it; the one that reported none shows an absence, not a zero.
        Assert.Contains("class=\"receipt-cost\">$0.42<", page);
        Assert.Contains("class=\"receipt-cost\">$0.31<", page);
        Assert.Contains("class=\"receipt-none\">—<", page);
        Assert.DoesNotContain("$0.00", page);

        // The total, with the coverage beside it in the stats and said in words above the rows.
        Assert.Contains("class=\"stat stat-cost\"><b>$0.73</b> spent", page);
        Assert.Contains("<b>2</b> of 4 priced", page);
        Assert.Contains("2 of 4 rows priced; 2 reported no cost and are not in the total", page);
        Assert.Contains("<b>1540</b> tokens", page);

        // The task's own line: what it has cost to land so far, and the sessions behind it.
        Assert.Contains("class=\"receipt-cost\">$0.73 to land<", page);
        Assert.Contains("conductor sessions", page);
        Assert.Contains("class=\"tag tag-open\">timeout<", page);
        Assert.Contains("#orchestrator", page);
        Assert.Contains("by harness", page);
    }

    /// <summary>A window in which nothing reported a cost has no total, not a total of zero: zero would say the work was free.</summary>
    [Fact]
    public async Task Nothing_priced_shows_no_total_rather_than_zero()
    {
        await ProjectAsync();
        var owner = await RegisterAsync("runner", "claude", "opus", "deep", "founder@example.com");
        var task = await owner.AddTaskAsync("Run the worker");
        (await owner.PostAsJsonAsync(Routes.WorkerRuns, new WorkerRunReport(
            task.Id, "deep", "claude", "opus", "founder@example.com", Branch(task.Id), "unit-b", false, 30, null))).EnsureSuccessStatusCode();

        var page = await PageAsync();

        Assert.Contains("class=\"stat stat-cost\"><b>—</b> spent", page);
        Assert.Contains("<b>0</b> of 1 priced", page);
        Assert.DoesNotContain("$0.00", page);
        Assert.DoesNotContain("to land", page);
    }

    /// <summary>
    /// Every class the page renders is defined in app.css. Nothing catches a class that does not exist at runtime —
    /// the browser simply draws it unstyled — so the stylesheet is read and checked here instead.
    /// </summary>
    [Fact]
    public async Task Every_class_the_page_renders_is_defined_in_app_css()
    {
        var css = await File.ReadAllTextAsync(AppCss());

        // Full, empty and invalid pages cover the sections and the window notice.
        await SpendAsync();
        await RunsAsync();
        var agent = await RegisterAsync("limited", "codex", "gpt", "cheap", "work@example.com");
        (await agent.PostAsJsonAsync(Routes.AccountLimits,
            new AccountLimitRequest("work@example.com", _hub.Clock.GetUtcNow().AddHours(2)))).EnsureSuccessStatusCode();
        var full = await PageAsync();
        using var bare = new HubFactory();
        var empty = await bare.CreateClient().GetStringAsync("/receipts");
        var invalid = await (await TypedAsync("7d")).Content.ReadAsStringAsync();

        Assert.Contains("rows priced;", full);   // the fixture really did render it all
        Assert.Contains("nothing spent in this window", empty);
        var classes = ClassesIn(full).Union(ClassesIn(empty), StringComparer.Ordinal)
            .Union(ClassesIn(invalid), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.Contains("receipt-cost", classes);
        Assert.Contains("notice", classes);
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
