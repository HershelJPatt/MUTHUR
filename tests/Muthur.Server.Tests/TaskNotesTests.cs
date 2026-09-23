using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// An owner's working notes are the understanding one session hands to the next on the same task. Without them a
/// resumption re-reads the codebase to rebuild what the last session already knew, which is the single most
/// expensive thing a session does; with them the resumed session starts where the last one stopped.
/// </summary>
public sealed class TaskNotesTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public TaskNotesTests()
    {
        _hub.Settings["Muthur:ConductorEnabled"] = "true";
        _hub.Settings["Muthur:ConductorSessionsPerTaskDay"] = "0";
    }

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private ConductorService Conductor => _hub.Services.GetRequiredService<ConductorService>();

    private static Task<HttpResponseMessage> NotesAsync(HttpClient client, string id, string notes) =>
        client.PostActionAsync(id, "notes", new TaskNotesRequest(notes));

    private async Task<(HttpClient Owner, string Id)> ClaimedAsync(string name = "corner")
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync(name);
        var task = await owner.AddTaskAsync("Ship the export");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    [Fact]
    public async Task The_owner_leaves_notes_and_the_task_shows_them()
    {
        var (owner, id) = await ClaimedAsync();

        (await NotesAsync(owner, id, "Became the expert: the export is CSV.\nLeft: the spec.")).EnsureSuccessStatusCode();

        var detail = await _hub.Founder().GetTaskAsync(id);
        Assert.Equal("Became the expert: the export is CSV.\nLeft: the spec.", detail.Notes);
        var set = Assert.Single(detail.Events, e => e.Type == "task.notes_set");
        Assert.Equal("corner", set.Actor);
    }

    [Fact]
    public async Task Notes_replace_rather_than_accumulate_and_blank_clears_them()
    {
        var (owner, id) = await ClaimedAsync();
        (await NotesAsync(owner, id, "first")).EnsureSuccessStatusCode();

        (await NotesAsync(owner, id, "second")).EnsureSuccessStatusCode();
        Assert.Equal("second", (await _hub.Founder().GetTaskAsync(id)).Notes);

        (await NotesAsync(owner, id, "   ")).EnsureSuccessStatusCode();
        var detail = await _hub.Founder().GetTaskAsync(id);
        Assert.Null(detail.Notes);
        Assert.Single(detail.Events, e => e.Type == "task.notes_cleared");
    }

    [Fact]
    public async Task Only_the_owner_writes_them()
    {
        var (_, id) = await ClaimedAsync();
        var bystander = await _hub.RegisterAgentAsync("bystander");

        var refused = await NotesAsync(bystander, id, "my account of somebody else's work");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("not_owner", (await refused.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task A_transcript_is_refused_so_the_resume_prompt_stays_a_prompt()
    {
        var (owner, id) = await ClaimedAsync();

        var refused = await NotesAsync(owner, id, new string('x', TaskNotesRequest.MaxChars + 1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("notes_too_long", (await refused.ReadErrorAsync()).Code);
        Assert.Null((await _hub.Founder().GetTaskAsync(id)).Notes);
    }

    [Fact]
    public async Task A_resumed_session_is_handed_the_notes_the_last_one_left()
    {
        // The session asked the founder, waited as long as it could, wrote what it knew and exited. Its claim
        // lapsed and the sweep returned the task to the backlog. The next session's prompt carries the notes,
        // quoted, so it starts from the understanding rather than from the codebase.
        (await _hub.Founder().PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true))).EnsureSuccessStatusCode();
        var (owner, id) = await ClaimedAsync();
        (await owner.PostActionAsync(id, "spec", new SetSpecRequest(_repo.WriteSpec(id)))).EnsureSuccessStatusCode();
        (await NotesAsync(owner, id, "The export is CSV; the founder said so.\nLeft: write the file.")).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await _hub.Services.GetRequiredService<TaskService>().SweepExpiredClaimsAsync());

        var planned = Assert.Single(await Conductor.PlanOrchestratorsAsync());
        Assert.True(planned.Resuming);
        Assert.Equal("The export is CSV; the founder said so.\nLeft: write the file.", planned.Notes);

        var prompt = OrchestratorSessionLauncher.Prompt(planned, new Muthur.Launch.HarnessCandidate("codex", "gpt", "acct"));
        Assert.Contains("The last session left its working notes", prompt, StringComparison.Ordinal);
        Assert.Contains("    The export is CSV; the founder said so.", prompt, StringComparison.Ordinal);
        Assert.Contains("    Left: write the file.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fresh_start_carries_no_notes_even_when_the_task_has_some()
    {
        // Notes on a task nobody has begun cannot exist — only the owner writes them — but the founder can, and
        // a fresh start reads the task rather than a note; the resume packet is for resumptions.
        (await _hub.Founder().PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true))).EnsureSuccessStatusCode();
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var author = await _hub.RegisterAgentAsync("author");
        var id = (await author.AddTaskAsync("Nobody has begun this")).Id;
        (await NotesAsync(_hub.Founder(), id, "founder's aside")).EnsureSuccessStatusCode();

        var planned = Assert.Single(await Conductor.PlanOrchestratorsAsync());

        Assert.False(planned.Resuming);
        Assert.Null(planned.Notes);
        Assert.DoesNotContain("working notes", OrchestratorSessionLauncher.Prompt(planned, new Muthur.Launch.HarnessCandidate("codex", "gpt", "acct")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launcher_tells_a_session_to_wait_for_an_answer_before_it_leaves_notes()
    {
        // The rule the notes exist for. A question used to end the session, and the answer started a new one
        // that read everything again. Waiting on the inbox costs nothing; only an expired wait leaves and notes.
        (await _hub.Founder().PostAsJsonAsync(Routes.ConductorOrchestrators, new ConductorOrchestratorSwitch(true))).EnsureSuccessStatusCode();
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var author = await _hub.RegisterAgentAsync("author");
        var id = (await author.AddTaskAsync("Needs a decision")).Id;

        var prompt = OrchestratorSessionLauncher.Prompt(Assert.Single(await Conductor.PlanOrchestratorsAsync()), new Muthur.Launch.HarnessCandidate("codex", "gpt", "acct"));

        Assert.Contains("ask and wait in one command", prompt, StringComparison.Ordinal);
        Assert.Contains("--wait 900", prompt, StringComparison.Ordinal);
        Assert.Contains($"muthur task notes {id} --file", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`muthur ask` and stop", prompt, StringComparison.Ordinal);
    }
}
