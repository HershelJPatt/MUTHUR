using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muthur.Contracts;
using Muthur.Server.Components.Shared;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// A task can carry the fact that it needs a human validator, together with the reason. Nothing sets that
/// automatically: a person reads the evidence and decides, and lifts it when the reason stops being true.
/// </summary>
public sealed class TaskAttendedTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public TaskAttendedTests() => _hub.Settings["Muthur:ConductorEnabled"] = "true";

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();

    private Task SetUpAsync(params string[] validators) => _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators);

    private async Task DefineAsync(params string[] roles)
    {
        foreach (var role in roles)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(role, $"# {role}\nDrive it."))).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> AttendedAsync(HttpClient client, string taskId, string? reason) =>
        client.PostActionAsync(taskId, "attended", new AttendedRequest(reason));

    private async Task<IReadOnlyList<EventDto>> EventsAsync() =>
        (await _hub.Founder().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto))!;

    private async Task<TaskDto> ClaimedTaskAsync(HttpClient owner, string title)
    {
        var task = await owner.AddTaskAsync(title);
        return await (await owner.ClaimAsync(task.Id)).ReadTaskAsync();
    }

    private async Task<string> ValidatingTaskAsync(HttpClient owner, string title, string branch, string file)
    {
        var task = await ClaimedTaskAsync(owner, title);
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        _repo.BranchWithFile(branch, file, "feature\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return task.Id;
    }

    [Fact]
    public async Task The_reason_is_on_the_task_and_in_the_ledger()
    {
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await ClaimedTaskAsync(owner, "Check the Doctor panel");

        var updated = await (await AttendedAsync(owner, task.Id, "  the panel is only visible in a browser  ")).ReadTaskAsync();

        Assert.Equal("the panel is only visible in a browser", updated.AttendedReason);
        var shown = await owner.GetTaskAsync(task.Id);
        Assert.Equal("the panel is only visible in a browser", shown.Task.AttendedReason);
        var recorded = Assert.Single(shown.Events, e => e.Type == "task.attended");
        Assert.Equal("the panel is only visible in a browser", recorded.Payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_request_carrying_no_reason_is_the_clear_which_is_why_the_CLI_refuses_it_first()
    {
        // The wire cannot tell "lift it" from "set it, but I forgot --reason": both arrive as a null reason. So the
        // hub reads it as the clear, and `muthur task attended` refuses a missing --reason before it sends anything
        // (TaskAttendedFlagTests). An unattended task is left exactly as it was, and nothing is recorded.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await ClaimedTaskAsync(owner, "Nothing to say about it");

        var unchanged = await (await AttendedAsync(owner, task.Id, null)).ReadTaskAsync();

        Assert.Null(unchanged.AttendedReason);
        Assert.Equal(task.UpdatedAt, unchanged.UpdatedAt);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type is "task.attended" or "task.attended_cleared");
    }

    [Fact]
    public async Task Clearing_lifts_the_flag_and_the_conductor_staffs_it_again()
    {
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = await ValidatingTaskAsync(owner, "Needs a browser", "task/T-1-feature", "feature.txt");
        (await AttendedAsync(owner, id, "the Doctor panel has no API")).EnsureSuccessStatusCode();
        Assert.Empty(await Conductor.PlanAsync());

        var lifted = await (await AttendedAsync(owner, id, null)).ReadTaskAsync();

        Assert.Null(lifted.AttendedReason);
        var cleared = Assert.Single(await EventsAsync(), e => e.Type == "task.attended_cleared");
        Assert.Equal("the Doctor panel has no API", cleared.Payload.GetProperty("was").GetString());
        Assert.Equal(id, Assert.Single(await Conductor.PlanAsync()).TaskKey);
    }

    [Fact]
    public async Task The_conductor_skips_the_attended_task_and_staffs_the_one_beside_it()
    {
        // The whole point of the flag, and the only thing it does: two tasks alike in every way the conductor looks
        // at, one of which a human has said needs them. Spending a session on it buys what the task already says.
        await SetUpAsync("win-validator");
        await DefineAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var attended = await ValidatingTaskAsync(owner, "Export the report", "task/T-1-export", "one.txt");
        var unattended = await ValidatingTaskAsync(owner, "Export the report", "task/T-2-export", "two.txt");
        (await AttendedAsync(owner, attended, "the export only renders in a browser")).EnsureSuccessStatusCode();

        var plan = await Conductor.PlanAsync();

        Assert.Equal(unattended, Assert.Single(plan).TaskKey);
    }

    [Fact]
    public async Task An_owned_task_is_its_owners_to_flag_and_an_unowned_one_is_anyones()
    {
        // Two tasks alike but for having an owner, and one stranger refused the first and allowed the second. An
        // unowned backlog task is exactly the case worth recording early - the author who already knows it will
        // need a browser has not claimed it yet - and the flag is reversible and cheap, so it is not the founder's.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var owned = await ClaimedTaskAsync(owner, "Export the report");
        var unowned = await owner.AddTaskAsync("Export the report");
        var stranger = await _hub.RegisterAgentAsync("stranger");

        var refused = await AttendedAsync(stranger, owned.Id, "I think it needs eyes");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("not_owner", (await refused.ReadErrorAsync()).Code);

        var flagged = await (await AttendedAsync(stranger, unowned.Id, "the importer needs a real file dialog")).ReadTaskAsync();
        Assert.Equal("the importer needs a real file dialog", flagged.AttendedReason);

        // Loosened for registered agents, not for the anonymous.
        Assert.Equal(HttpStatusCode.Unauthorized, (await AttendedAsync(_hub.CreateClient(), unowned.Id, "whoever I am")).StatusCode);

        // On the owned one, its owner and the founder both may.
        (await AttendedAsync(owner, owned.Id, "a device has to be held")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AttendedAsync(stranger, owned.Id, null)).StatusCode);
        (await AttendedAsync(_hub.Founder(), owned.Id, null)).EnsureSuccessStatusCode();
        Assert.Null((await owner.GetTaskAsync(owned.Id)).Task.AttendedReason);
    }

    [Fact]
    public async Task Saying_it_twice_is_not_an_error_and_records_it_once()
    {
        // It is an assertion about the world, not a toggle: repeating it is agreement, not a change.
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await ClaimedTaskAsync(owner, "Say it twice");

        (await AttendedAsync(owner, task.Id, "a browser")).EnsureSuccessStatusCode();
        var setAt = (await owner.GetTaskAsync(task.Id)).Task.UpdatedAt;
        _hub.Clock.Advance(TimeSpan.FromMinutes(5));
        (await AttendedAsync(owner, task.Id, "a browser")).EnsureSuccessStatusCode();

        var detail = await owner.GetTaskAsync(task.Id);
        Assert.Single(detail.Events, e => e.Type == "task.attended");
        Assert.Equal(setAt, detail.Task.UpdatedAt);

        (await AttendedAsync(owner, task.Id, null)).EnsureSuccessStatusCode();
        (await AttendedAsync(owner, task.Id, null)).EnsureSuccessStatusCode();
        Assert.Single((await owner.GetTaskAsync(task.Id)).Events, e => e.Type == "task.attended_cleared");
    }

    [Fact]
    public async Task The_board_card_says_a_human_is_needed_and_the_task_page_says_why()
    {
        const string reason = "the Doctor panel is only visible in a browser";
        await SetUpAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await ClaimedTaskAsync(owner, "Check the Doctor panel");
        var attended = await (await AttendedAsync(owner, task.Id, reason)).ReadTaskAsync();

        var card = await RenderCardAsync(attended);
        var page = await _hub.CreateClient().GetStringAsync($"/tasks/{task.Id}");

        Assert.Contains("needs a human", card);
        Assert.DoesNotContain(reason, card);   // a card has no room for a sentence; the detail page has
        Assert.Contains("Needs a human", page);
        Assert.Contains(reason, page);

        Assert.DoesNotContain("needs a human", await RenderCardAsync(task));
    }

    /// <summary>
    /// The board's cards sit inside a &lt;Virtualize&gt;, which renders nothing until a browser reports a viewport,
    /// so a GET of "/" carries no card to assert on. Render the board's own card component instead.
    /// </summary>
    private async Task<string> RenderCardAsync(TaskDto task)
    {
        using var scope = _hub.Services.CreateScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<TaskCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TaskCard.Task)] = task,
                [nameof(TaskCard.Now)] = _hub.Clock.GetUtcNow(),
            }))).ToHtmlString());
    }
}
