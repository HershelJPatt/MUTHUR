using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muthur.Contracts;
using Muthur.Server.Components.Layout;
using Muthur.Server.Components.Panels;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// A task in 'validating' with the attended flag set is on somebody's queue. Nothing advances it — the
/// conductor skips it on purpose — so the founder is told by the badge and the page they already read,
/// rather than by happening to recognise the state on the board.
/// </summary>
public sealed class DashboardAttendedTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private LifecycleService Lifecycle => _hub.Services.GetRequiredService<LifecycleService>();
    private FounderAttention Attention => _hub.Services.GetRequiredService<FounderAttention>();

    private async Task<HttpClient> SetUpAsync(params string[] validators)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators);
        foreach (var role in validators)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(role, $"# {role}\nDrive it."))).EnsureSuccessStatusCode();
        return await _hub.RegisterAgentAsync("owner");
    }

    private static Task<HttpResponseMessage> AttendedAsync(HttpClient client, string taskId, string? reason) =>
        client.PostActionAsync(taskId, "attended", new AttendedRequest(reason));

    /// <summary>A claimed, specced task marked implemented onto its own branch: the state this task is about.</summary>
    private async Task<string> ValidatingTaskAsync(HttpClient owner, string title, string branch, string file, int priority = 0)
    {
        var task = await owner.AddTaskAsync(title, priority: priority);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(branch, file, "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return task.Id;
    }

    [Fact]
    public async Task The_queue_is_what_nothing_advances_which_is_the_flag_and_not_the_state()
    {
        // Two tasks alike in every way the read looks at but one: a human has said one of them needs them.
        var owner = await SetUpAsync("win-validator");
        var attended = await ValidatingTaskAsync(owner, "Export the report", "task/T-1-export", "one.txt");
        var unattended = await ValidatingTaskAsync(owner, "Import the report", "task/T-2-import", "two.txt");
        (await AttendedAsync(owner, attended, "the export only renders in a browser")).EnsureSuccessStatusCode();

        var waiting = await Lifecycle.AttendedAsync();

        Assert.Equal(attended, Assert.Single(waiting).Id);
        Assert.DoesNotContain(waiting, t => t.Id == unattended);
    }

    [Fact]
    public async Task A_task_the_flag_is_on_but_validating_is_not_is_somebody_elses_already()
    {
        // Backlog is the conductor's and in-progress is its owner's; only 'validating' has nothing behind it.
        var owner = await SetUpAsync("win-validator");
        var backlog = await owner.AddTaskAsync("Check the Doctor panel");
        (await AttendedAsync(owner, backlog.Id, "the panel is only visible in a browser")).EnsureSuccessStatusCode();
        var inProgress = await owner.AddTaskAsync("Check the importer");
        (await owner.ClaimAsync(inProgress.Id)).EnsureSuccessStatusCode();
        (await AttendedAsync(owner, inProgress.Id, "the importer needs a real file dialog")).EnsureSuccessStatusCode();

        Assert.Empty(await Lifecycle.AttendedAsync());
    }

    [Fact]
    public async Task Priority_orders_the_queue_and_the_longest_wait_breaks_the_tie()
    {
        var owner = await SetUpAsync("win-validator");
        // Waiting longest of the three, and still last: priority is the founder's one lever for what matters.
        var low = await ValidatingTaskAsync(owner, "Tidy the footer", "task/T-1-footer", "one.txt");
        _hub.Clock.Advance(TimeSpan.FromHours(1));
        var older = await ValidatingTaskAsync(owner, "Export the report", "task/T-2-export", "two.txt", priority: 5);
        _hub.Clock.Advance(TimeSpan.FromHours(1));
        var newer = await ValidatingTaskAsync(owner, "Import the report", "task/T-3-import", "three.txt", priority: 5);
        foreach (var id in new[] { low, older, newer })
            (await AttendedAsync(owner, id, "only a browser can tell")).EnsureSuccessStatusCode();

        var waiting = await Lifecycle.AttendedAsync();

        Assert.Equal([older, newer, low], waiting.Select(t => t.Id));
    }

    [Fact]
    public async Task The_wait_reported_is_the_round_that_is_outstanding_not_the_age_of_the_task()
    {
        var owner = await SetUpAsync("win-validator");
        var id = await ValidatingTaskAsync(owner, "Export the report", "task/T-1-export", "one.txt");
        var firstRound = _hub.Clock.GetUtcNow();

        // A blocked verdict sends it back to its owner, so the round that was waiting is over.
        var validator = await _hub.RegisterAgentAsync("validator");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "No browser here. Tried opening the dashboard URL.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();
        (await validator.PostAsync(Routes.RoleAction("win-validator", "release"), null)).EnsureSuccessStatusCode();

        _hub.Clock.Advance(TimeSpan.FromHours(1));
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-export"))).EnsureSuccessStatusCode();
        var secondRound = _hub.Clock.GetUtcNow();
        (await AttendedAsync(owner, id, "the export only renders in a browser")).EnsureSuccessStatusCode();

        var task = Assert.Single(await Lifecycle.AttendedAsync());

        Assert.NotEqual(firstRound, secondRound);
        Assert.Equal(secondRound, LifecycleService.WaitingSince(task));
    }

    [Fact]
    public async Task The_badge_counts_an_attended_task_and_ages_with_it_until_the_flag_is_lifted()
    {
        var owner = await SetUpAsync("win-validator");
        Assert.Equal(0, (await Attention.ReadAsync()).Total);
        var id = await ValidatingTaskAsync(owner, "Export the report", "task/T-1-export", "one.txt");
        var since = _hub.Clock.GetUtcNow();
        (await AttendedAsync(owner, id, "the export only renders in a browser")).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromHours(5));

        var attention = await Attention.ReadAsync();
        Assert.Equal(1, attention.Total);
        Assert.Equal(id, Assert.Single(attention.Attended).Id);
        // Work is genuinely stopped behind it, which is why it ages the headline where an unread message does not.
        Assert.Equal(since, attention.OldestWaitingSince);
        Assert.Contains("class=\"badge\">1<", await _hub.CreateClient().GetStringAsync("/"));

        (await AttendedAsync(owner, id, null)).EnsureSuccessStatusCode();

        var lifted = await Attention.ReadAsync();
        Assert.Equal(0, lifted.Total);
        Assert.Empty(lifted.Attended);
        Assert.Null(lifted.OldestWaitingSince);
        Assert.DoesNotContain("class=\"badge\"", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task The_page_says_which_task_why_on_what_branch_and_who_it_is_waiting_on()
    {
        const string reason = "the export only renders in a browser";
        var owner = await SetUpAsync("win-validator");
        Assert.Contains("nothing is waiting on a person", await _hub.CreateClient().GetStringAsync("/needs-you"));

        var id = await ValidatingTaskAsync(owner, "Export the report", "task/T-1-export", "one.txt");
        (await AttendedAsync(owner, id, reason)).EnsureSuccessStatusCode();

        var page = await _hub.CreateClient().GetStringAsync("/needs-you");

        Assert.Contains("Waiting on a person", page);
        // The panel's own head: the agents rail says "1 task" too, about a different thing.
        Assert.Contains("class=\"panel-sub\">1 task<", page);
        Assert.Contains("needs a person", page);
        Assert.Contains($">{id}<", page);
        Assert.Contains("Export the report", page);
        Assert.Contains(reason, page);                                   // the whole sentence, not a truncation
        Assert.Contains("class=\"pill\">task/T-1-export<", page);
        Assert.Contains("class=\"pill pill-role\">win-validator<", page);
        Assert.DoesNotContain("nothing is waiting on a person", page);
    }

    /// <summary>
    /// The panel states the two exits and offers neither as a click: a verdict is a claim that somebody
    /// exercised the work, and a button on a queue page produces the machinery of one without the substance.
    /// Asserted against the panel's own HTML, because the rest of the page has buttons of its own.
    /// </summary>
    [Fact]
    public async Task Nothing_on_the_panel_acts_it_only_says_what_the_two_ways_out_are()
    {
        var owner = await SetUpAsync("win-validator");
        var id = await ValidatingTaskAsync(owner, "Export the report", "task/T-1-export", "one.txt");
        (await AttendedAsync(owner, id, "the export only renders in a browser")).EnsureSuccessStatusCode();

        var panel = await RenderPanelAsync();

        Assert.Contains("take the role and give the verdict", panel);
        Assert.Contains("muthur task attended T-n --clear", panel);
        Assert.DoesNotContain("<button", panel);
    }

    /// <summary>
    /// The badge and the panel reload the moment an attended task appears, rather than up to a clock tick
    /// later. Both predicates live behind a SignalR circuit no test can click, so they are asserted as the
    /// plain functions they are — and over event types this codebase really records, because a subscription
    /// that watches a word nothing writes is the same defect as watching nothing.
    /// </summary>
    [Fact]
    public void The_badge_and_the_panel_wake_on_the_events_that_move_them()
    {
        // The flag itself, the moves in and out of 'validating', and the verdicts that cause them.
        foreach (var type in new[]
                 {
                     "task.attended", "task.attended_cleared", "task.implemented", "task.validation_blocked",
                     "validation.passed", "validation.blocked",
                 })
        {
            Assert.True(NeedsYouBadge.Watches(type), type);
            Assert.True(AttendedPanel.Watches(type), type);
        }

        // The three the badge already counted, which this task must not have cost it.
        foreach (var type in new[] { "request.answered", "message.sent", "outbound.approved" })
        {
            Assert.True(NeedsYouBadge.Watches(type), type);
            Assert.False(AttendedPanel.Watches(type), type);   // none of them puts a task on a person's queue
        }

        // And what neither may reload on: an agent coming back, and the conductor thinking out loud.
        foreach (var type in new[] { "agent.registered", "conductor.staffing" })
        {
            Assert.False(NeedsYouBadge.Watches(type), type);
            Assert.False(AttendedPanel.Watches(type), type);
        }
    }

    private async Task<string> RenderPanelAsync()
    {
        using var scope = _hub.Services.CreateScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AttendedPanel>(ParameterView.Empty)).ToHtmlString());
    }
}
