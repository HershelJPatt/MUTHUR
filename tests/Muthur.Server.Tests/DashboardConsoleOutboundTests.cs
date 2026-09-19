using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;
using ConsolePage = Muthur.Server.Components.Pages.Console;

namespace Muthur.Server.Tests;

/// <summary>
/// The console's Outbound and Tasks controls. T-24 is <c>attended</c>, so nothing here presses a button: every
/// control rides a SignalR circuit. What a machine checks is everything short of the click — and on this half of
/// the page one of those checks is not cosmetic. A target's address is routinely a credential, and the assertion
/// that an added one never reaches the fetched HTML is the single security-shaped thing a test can prove about
/// this page, so it is proved against a target that really was added rather than against an empty section.
/// </summary>
public sealed class DashboardConsoleOutboundTests : IDisposable
{
    /// <summary>
    /// A webhook URL, which is the case that matters: the address <em>is</em> the authority to post, so anything
    /// that echoed it back would be handing out the credential rather than describing the target.
    /// </summary>
    private const string Secret = "https://example.invalid/hooks/s3cret-never-render-this";

    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private OutboundService Outbound => _hub.Services.GetRequiredService<OutboundService>();

    private TaskService Tasks => _hub.Services.GetRequiredService<TaskService>();

    private Task<string> PageAsync() => _hub.CreateClient().GetStringAsync("/console");

    // -- Outbound, write-only ------------------------------------------------------------------------------

    /// <summary>
    /// The one check this page exists to survive. A target is defined through the service the button calls, and
    /// the page that then lists it carries its key and its channel and not one character of its address — not in
    /// the row, not in a confirmation, not in a placeholder, not in a value attribute left behind by the form.
    /// The row assertions come first on purpose: without them a page with no Outbound section at all would pass
    /// this, and the check would be measuring nothing.
    /// </summary>
    [Fact]
    public async Task An_added_targets_address_never_appears_on_the_page()
    {
        await Outbound.DefineTargetAsync(Caller.Founder, new DefineTargetRequest("newsroom", "discord-webhook", Secret));

        var page = await PageAsync();

        Assert.Contains("<span class=\"agent-name\">newsroom</span>", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"agent-model\">discord-webhook</span>", page, StringComparison.Ordinal);

        Assert.DoesNotContain(Secret, page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3cret-never-render-this", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why the page cannot leak an address even if a later reader tries: nothing it can read back holds one.
    /// <c>TargetDto</c> has three fields and none of them is the address, so the write-only rule is the shape of
    /// the contract rather than a habit of this markup.
    /// </summary>
    [Fact]
    public async Task What_comes_back_from_the_service_has_no_address_on_it()
    {
        var defined = await Outbound.DefineTargetAsync(Caller.Founder, new DefineTargetRequest("newsroom", "discord-webhook", Secret));

        Assert.Equal("newsroom", defined.Key);
        Assert.Equal("discord-webhook", defined.Channel);
        Assert.DoesNotContain("Address", typeof(TargetDto).GetProperties().Select(p => p.Name));

        var listed = Assert.Single(await Outbound.TargetsAsync());
        Assert.Equal("newsroom", listed.Key);
    }

    /// <summary>The Outbound control, whole: every input the founder fills, labelled, and the button.</summary>
    [Fact]
    public async Task The_page_renders_the_outbound_control_with_every_label_and_its_button()
    {
        var page = await PageAsync();

        Assert.DoesNotContain("An unhandled error", page, StringComparison.Ordinal);
        Assert.Contains("class=\"panel-title\">Outbound<", page, StringComparison.Ordinal);
        Assert.Matches("<label for=\"target-key\">Key</label>", page);
        Assert.Matches("<label for=\"target-channel\">Channel</label>", page);
        Assert.Matches("<label for=\"target-address\">Address</label>", page);
        Assert.Matches("<button class=\"btn btn-go\"[^>]*>Add</button>", page);

        // A hub with no targets says so, because "nothing goes out" is a state worth reading rather than a
        // blank space that looks like a page still loading.
        Assert.Contains("no targets yet", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The channel list comes from the channels this hub has registered, not from a list written into the page.
    /// Every <see cref="IOutboundChannel"/> in the container is offered and nothing else is, so a channel added
    /// to the server appears here without anyone editing the console — and the closed set the founder chooses
    /// from is exactly the closed set <c>FindChannel</c> will accept.
    /// </summary>
    [Fact]
    public async Task The_channel_options_are_the_channels_this_hub_has_registered()
    {
        var registered = _hub.Services.GetServices<IOutboundChannel>().Select(c => c.Name).ToList();
        Assert.NotEmpty(registered);

        var page = await PageAsync();

        // The one standing carries `selected`, so the option is matched around that rather than exactly.
        foreach (var name in registered)
            Assert.Matches($"<option value=\"{name}\"( selected)?>{name}</option>", page);
    }

    /// <summary>
    /// The console must not be the lax way to do what the CLI does carefully. <c>muthur out target</c> carries
    /// <c>--founder-approval</c>, and <c>DefineTargetAsync</c> writes that flag unconditionally on every save —
    /// so a console that did not send it would silently strip founder approval from an existing target the first
    /// time anyone corrected its address. Two clients of one operation, one safety rule.
    /// </summary>
    [Fact]
    public async Task Redefining_a_target_writes_whatever_founder_approval_flag_it_is_sent()
    {
        var guarded = await Outbound.DefineTargetAsync(
            Caller.Founder, new DefineTargetRequest("newsroom", "discord-webhook", Secret, RequiresFounderApproval: true));
        Assert.True(guarded.RequiresFounderApproval);
        Assert.Contains("founder approves", await PageAsync(), StringComparison.Ordinal);

        // The same key again with the flag absent: the gate is gone, and nothing warned anybody.
        var stripped = await Outbound.DefineTargetAsync(
            Caller.Founder, new DefineTargetRequest("newsroom", "discord-webhook", Secret));
        Assert.False(stripped.RequiresFounderApproval);
        Assert.DoesNotContain("founder approves", await PageAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A channel nobody registered is refused in the service's own words, which the console renders rather than
    /// rewording — so the page and the CLI say one thing about one mistake.
    /// </summary>
    [Fact]
    public async Task An_unknown_channel_is_refused_in_the_services_own_words()
    {
        var refused = await Assert.ThrowsAsync<Muthur.Core.MuthurException>(() =>
            Outbound.DefineTargetAsync(Caller.Founder, new DefineTargetRequest("newsroom", "carrier-pigeon", Secret)));

        Assert.Equal("unknown_channel", refused.Code);
        Assert.Contains("is not a channel", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await Outbound.TargetsAsync());
    }

    // -- Tasks ---------------------------------------------------------------------------------------------

    /// <summary>The Tasks control: the typed id, the two values, and the three buttons that act on it.</summary>
    [Fact]
    public async Task The_page_renders_the_tasks_control_with_its_three_buttons()
    {
        var page = await PageAsync();

        Assert.Contains("class=\"panel-title\">Tasks<", page, StringComparison.Ordinal);
        Assert.Matches("<label for=\"task-id\">Task</label>", page);
        Assert.Matches("<label for=\"task-priority\">Priority</label>", page);
        Assert.Matches("<label for=\"task-reason\">Cancel reason</label>", page);
        Assert.Matches("<button class=\"btn btn-go\"[^>]*>Set priority</button>", page);
        Assert.Matches("<button class=\"btn btn-stop\"[^>]*>Cancel</button>", page);
        Assert.Matches("<button class=\"btn\"[^>]*>Reopen</button>", page);
    }

    /// <summary>
    /// The board is the list of tasks and this page never becomes a second one. A task that exists, and is on
    /// the board the founder would act from, appears nowhere on the console — which is what makes the typed id
    /// the only way to name one here, and what stops two renderings of one backlog drifting apart.
    /// </summary>
    [Fact]
    public async Task The_console_is_not_a_second_task_list()
    {
        await _hub.AddProjectAsync();
        var task = await _hub.Founder().AddTaskAsync("Rewire the coolant loop");

        // The task is real and the hub will talk about it, so a console that listed tasks would be naming it.
        Assert.Equal("Rewire the coolant loop", (await _hub.Founder().GetTaskAsync(task.Id)).Task.Title);

        var page = await PageAsync();
        Assert.DoesNotContain("Rewire the coolant loop", page, StringComparison.Ordinal);
        Assert.DoesNotContain($">{task.Id}<", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the three buttons do, driven through the service they call. Priority moves, cancel takes the task out
    /// of the backlog, and reopen puts it back — each as the founder, each a ledger event indistinguishable from
    /// the CLI having done it.
    /// </summary>
    [Fact]
    public async Task Priority_cancel_and_reopen_do_what_the_buttons_claim()
    {
        await _hub.AddProjectAsync();
        var task = await _hub.Founder().AddTaskAsync("Rewire the coolant loop");
        Assert.Equal(0, task.Priority);

        var raised = await Tasks.SetPriorityAsync(Caller.Founder, task.Id, new SetPriorityRequest(7));
        Assert.Equal(7, raised.Priority);

        var cancelled = await Tasks.CancelAsync(Caller.Founder, task.Id, new CancelTaskRequest("superseded by the rebuild"));
        Assert.Equal(TaskState.Cancelled, cancelled.State);

        var reopened = await Tasks.ReopenAsync(Caller.Founder, task.Id);
        Assert.Equal(TaskState.Backlog, reopened.State);
        // Priority survives the round trip: reopening returns a task to the backlog, it does not re-file it.
        Assert.Equal(7, reopened.Priority);
    }

    /// <summary>
    /// Cancel's reason is optional to the hub — <c>CancelTaskRequest.Reason</c> is nullable and nothing refuses a
    /// null one, exactly as <c>muthur task cancel</c> leaves <c>--reason</c> optional. So the console sends what
    /// was typed and validates nothing ahead of the service; an empty box is a cancellation with no reason
    /// recorded, which is the hub's decision to make and not this page's.
    /// </summary>
    [Fact]
    public async Task Cancel_without_a_reason_is_the_hubs_decision_and_the_hub_allows_it()
    {
        await _hub.AddProjectAsync();
        var task = await _hub.Founder().AddTaskAsync("Rewire the coolant loop");

        var cancelled = await Tasks.CancelAsync(Caller.Founder, task.Id, new CancelTaskRequest());

        Assert.Equal(TaskState.Cancelled, cancelled.State);
    }

    /// <summary>
    /// An id that names no task is refused in the service's own words. The console types an id rather than
    /// picking one, so this is the mistake the founder will actually make, and what they are handed is the
    /// sentence the CLI would have printed.
    /// </summary>
    [Fact]
    public async Task An_id_that_names_no_task_is_refused_in_the_services_own_words()
    {
        var refused = await Assert.ThrowsAsync<Muthur.Core.MuthurException>(() =>
            Tasks.SetPriorityAsync(Caller.Founder, "T-404", new SetPriorityRequest(3)));

        Assert.NotEmpty(refused.Code);
        Assert.NotEmpty(refused.Message);
    }

    // -- the Roles checkbox's latch ------------------------------------------------------------------------

    /// <summary>
    /// The override, kept. Inference is a default for a key nobody has ruled on, and the moment the founder
    /// touches the box the key stops driving it — otherwise unticking the box and then going back to fix a typo
    /// in the key silently re-ticks it, and the founder defines a validator they had already said no to. An
    /// override a later keystroke revokes is not an override. Tested as the function it is: the checkbox behind
    /// it lives on a SignalR circuit no test can click.
    /// </summary>
    [Theory]
    // Untouched: the key decides, which is the trap closed and the reason the control exists.
    [InlineData("validator", false, false, true)]
    [InlineData("comms-oncall", true, false, false)]
    [InlineData("win-validator", false, false, true)]
    // Touched: whatever the box reads now survives the next keystroke, in both directions.
    [InlineData("validator", false, true, false)]
    [InlineData("win-validator", false, true, false)]
    [InlineData("comms-oncall", true, true, true)]
    public void A_touched_checkbox_stops_the_key_from_re_inferring(string key, bool current, bool touched, bool expected) =>
        Assert.Equal(expected, ConsolePage.ValidatorAfterKeystroke(key, current, touched));

    /// <summary>
    /// The defect the latch fixes, spelled out: a founder types the key, unticks the box, then corrects the key.
    /// Without the latch the correction reverses their decision; with it, the box still reads what they set.
    /// </summary>
    [Fact]
    public void Fixing_a_typo_in_the_key_does_not_revoke_the_founders_decision()
    {
        // Typed: the key infers the flag, because nobody has said otherwise yet.
        var box = ConsolePage.ValidatorAfterKeystroke("win-validatr", current: false, touched: false);
        Assert.False(box);
        box = ConsolePage.ValidatorAfterKeystroke("win-validator", box, touched: false);
        Assert.True(box);

        // The founder disagrees and unticks it. That is a decision, not a default.
        box = false;

        // And then fixes the spelling of something else in the key. The decision stands.
        Assert.False(ConsolePage.ValidatorAfterKeystroke("winch-validator", box, touched: true));
    }
}
