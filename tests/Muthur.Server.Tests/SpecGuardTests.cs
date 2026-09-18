using System.Net;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// `task spec` attaches the document implementers are sent to build from. Ledger ids get reused, so the path
/// has to be checked: a spec that is missing, outside the checkout, or written for another task is refused.
/// </summary>
public sealed class SpecGuardTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "muthur-tests", "outside-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
        try { Directory.Delete(_outside, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A claimed task — always T-1, the first in an empty ledger — over a real checkout.</summary>
    private async Task<(HttpClient Owner, string TaskId)> ClaimedTaskAsync()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    private static async Task<ErrorResponse> RefusedAsync(HttpClient owner, string taskId, string path)
    {
        var response = await owner.PostActionAsync(taskId, "spec", new SetSpecRequest(path));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return await response.ReadErrorAsync();
    }

    [Fact]
    public async Task A_spec_whose_heading_names_this_task_is_attached()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-feature.md", "# T-1 — Build the feature\n\nFrozen spec.\n");

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-feature.md"))).ReadTaskAsync();

        Assert.Equal("specs/T-1-feature.md", task.SpecPath);
    }

    [Fact]
    public async Task A_spec_written_for_another_task_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-9-outbound-gate.md", "# T-9 — The outbound gate\n\nFrozen spec.\n");

        var error = await RefusedAsync(owner, id, "specs/T-9-outbound-gate.md");

        Assert.Equal("spec_id_mismatch", error.Code);
        Assert.Contains("T-9", error.Message);
        Assert.Contains("not T-1", error.Message);
        Assert.Null((await owner.GetTaskAsync(id)).Task.SpecPath);
    }

    [Fact]
    public async Task A_path_to_no_file_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();

        var error = await RefusedAsync(owner, id, "specs/typo.md");

        Assert.Equal("spec_missing", error.Code);
        Assert.Contains("specs/typo.md", error.Message);
    }

    [Fact]
    public async Task A_path_that_escapes_the_repository_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "T-1.md"), "# T-1 — somebody else's file\n");
        var sibling = _repo.Path + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "T-1.md"), "# T-1 — a neighbour that merely shares a prefix\n");

        try
        {
            var up = await RefusedAsync(owner, id, $"../{Path.GetFileName(_outside)}/T-1.md");
            Assert.Equal("spec_outside_repository", up.Code);

            // The subtle one: '<repo>-evil' starts with '<repo>' but is not inside it.
            var neighbour = await RefusedAsync(owner, id, $"../{Path.GetFileName(sibling)}/T-1.md");
            Assert.Equal("spec_outside_repository", neighbour.Code);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task A_heading_that_names_no_task_is_accepted()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/outbound-gate.md", "# The outbound gate\n\nNot every project writes the id into the heading.\n");

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/outbound-gate.md"))).ReadTaskAsync();

        Assert.Equal("specs/outbound-gate.md", task.SpecPath);
    }

    [Fact]
    public async Task Blank_lines_before_the_heading_do_not_hide_it()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-9-leading-space.md", "\n\n   \n# T-9 — The outbound gate\n");

        var error = await RefusedAsync(owner, id, "specs/T-9-leading-space.md");

        Assert.Equal("spec_id_mismatch", error.Code);
    }
}
