using System.Net;
using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// A bounced land has to leave something countable behind: the collision rate is the evidence T-16's
/// decision will be revisited on, and prose with a capped file list is not a rate anyone can query.
/// </summary>
public sealed class LandConflictTests : IDisposable
{
    /// <summary>More files than <c>ConflictMessage</c>'s cap of twelve, so the prose and the payload differ.</summary>
    private static readonly string[] ManyFiles = [.. Enumerable.Range(1, 15).Select(i => $"file{i:00}.txt")];

    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private int _owners;

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    /// <summary>Claims a task, attaches a spec, and marks it implemented on <paramref name="branch"/>.</summary>
    private async Task<(HttpClient Owner, string TaskId)> ReadyToLandAsync(string title, string branch)
    {
        var owner = await _hub.RegisterAgentAsync($"owner-{++_owners}");
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest($"specs/{task.Id}.md"))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    private static JsonElement LandFailedPayload(TaskDetailDto detail) =>
        Assert.Single(detail.Events, e => e.Type == "task.land_failed").Payload;

    private static string[] Strings(JsonElement payload, string property) =>
        [.. payload.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task A_conflicting_land_records_the_branch_the_target_the_files_and_what_landed_first()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        // Two tasks that both add shared.txt: they merge cleanly one at a time, and collide the second time.
        _repo.BranchWithFile("task/T-1-alpha", "shared.txt", "alpha\n");
        _repo.BranchWithFile("task/T-2-beta", "shared.txt", "beta\n");
        var (first, firstId) = await ReadyToLandAsync("Build alpha", "task/T-1-alpha");
        var (second, secondId) = await ReadyToLandAsync("Build beta", "task/T-2-beta");
        Assert.Equal(TaskState.Done, (await (await first.PostAsync(Routes.TaskAction(firstId, "land"), null)).ReadTaskAsync()).State);

        var land = await second.PostAsync(Routes.TaskAction(secondId, "land"), null);

        Assert.Equal(HttpStatusCode.Conflict, land.StatusCode);
        Assert.Equal("merge_conflict", (await land.ReadErrorAsync()).Code);
        var payload = LandFailedPayload(await second.GetTaskAsync(secondId));
        Assert.Equal("merge_conflict", payload.GetProperty("code").GetString());
        Assert.Equal("task/T-2-beta", payload.GetProperty("branch").GetString());
        Assert.Equal("main", payload.GetProperty("target").GetString());
        Assert.Contains("shared.txt", Strings(payload, "files"));
        Assert.Contains(firstId, Strings(payload, "landedSince"));
    }

    /// <summary>The payload carries the whole truth even when the prose agents read has to stop at twelve.</summary>
    [Fact]
    public async Task The_recorded_file_list_is_uncapped_while_the_message_still_stops_at_twelve()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        _repo.Git("checkout", "-q", "-b", "task/T-1-wide", "main");
        foreach (var file in ManyFiles) _repo.Write(file, "from the branch\n");
        _repo.Commit("wide branch");
        _repo.Git("checkout", "-q", "main");
        foreach (var file in ManyFiles) _repo.Write(file, "from main\n");
        _repo.Commit("main gets there first");
        var (owner, id) = await ReadyToLandAsync("Touch everything", "task/T-1-wide");

        var land = await owner.PostAsync(Routes.TaskAction(id, "land"), null);

        Assert.Equal(HttpStatusCode.Conflict, land.StatusCode);
        var message = (await land.ReadErrorAsync()).Message;
        Assert.Equal(12, ManyFiles.Count(message.Contains));           // the prose is still capped
        Assert.Equal(ManyFiles, Strings(LandFailedPayload(await owner.GetTaskAsync(id)), "files"));
    }

    /// <summary>
    /// `git merge-base` has no answer across unrelated histories, so `landedSince` cannot be computed. The land
    /// must still bounce the task back with 409 and still record the event: the evidence is best effort, the
    /// behaviour is not.
    /// </summary>
    [Fact]
    public async Task A_conflict_whose_landed_since_cannot_be_computed_still_records_the_event_and_still_returns_409()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        _repo.Git("checkout", "-q", "--orphan", "task/T-1-unrelated");
        _repo.Git("rm", "-rq", "--cached", ".");
        _repo.Write("README.md", "# from an unrelated history\n");
        _repo.Commit("unrelated");
        _repo.Git("checkout", "-q", "-f", "main");
        var mainBefore = _repo.Git("rev-parse", "main");
        var (owner, id) = await ReadyToLandAsync("Land from nowhere", "task/T-1-unrelated");

        var land = await owner.PostAsync(Routes.TaskAction(id, "land"), null);

        Assert.Equal(HttpStatusCode.Conflict, land.StatusCode);
        Assert.Equal("merge_conflict", (await land.ReadErrorAsync()).Code);
        var detail = await owner.GetTaskAsync(id);
        Assert.Equal(TaskState.InProgress, detail.Task.State);
        Assert.Empty(Strings(LandFailedPayload(detail), "landedSince"));
        Assert.Equal(mainBefore, _repo.Git("rev-parse", "main"));
        Assert.Equal("", _repo.Git("status", "--porcelain", "--untracked-files=no"));
    }

    /// <summary>The same record, from the path taken when nobody has the default branch checked out.</summary>
    [Fact]
    public async Task The_no_checkout_path_records_the_same_evidence()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        _repo.BranchWithFile("task/T-1-alpha", "shared.txt", "alpha\n");
        _repo.BranchWithFile("task/T-2-beta", "shared.txt", "beta\n");
        var (first, firstId) = await ReadyToLandAsync("Build alpha", "task/T-1-alpha");
        var (second, secondId) = await ReadyToLandAsync("Build beta", "task/T-2-beta");
        (await first.PostAsync(Routes.TaskAction(firstId, "land"), null)).EnsureSuccessStatusCode();
        _repo.Git("checkout", "-q", "-b", "somewhere-else");   // main is now checked out nowhere

        var land = await second.PostAsync(Routes.TaskAction(secondId, "land"), null);

        Assert.Equal(HttpStatusCode.Conflict, land.StatusCode);
        var payload = LandFailedPayload(await second.GetTaskAsync(secondId));
        Assert.Equal("task/T-2-beta", payload.GetProperty("branch").GetString());
        Assert.Equal("main", payload.GetProperty("target").GetString());
        Assert.Contains("shared.txt", Strings(payload, "files"));
        Assert.Contains(firstId, Strings(payload, "landedSince"));
    }

    [Fact]
    public async Task A_refused_land_is_unchanged_and_carries_no_collision_evidence()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        _repo.BranchWithFile("task/T-1-feature", "feature.txt", "feature\n");
        var (owner, id) = await ReadyToLandAsync("Build the feature", "task/T-1-feature");
        _repo.Write("README.md", "# uncommitted edit\n");

        var land = await owner.PostAsync(Routes.TaskAction(id, "land"), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, land.StatusCode);
        Assert.Equal("dirty_checkout", (await land.ReadErrorAsync()).Code);
        var detail = await owner.GetTaskAsync(id);
        Assert.Equal(TaskState.Validated, detail.Task.State);
        var refused = Assert.Single(detail.Events, e => e.Type == "task.land_refused").Payload;
        Assert.Equal(["code", "message"], refused.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(detail.Events, e => e.Type == "task.land_failed");
    }
}
