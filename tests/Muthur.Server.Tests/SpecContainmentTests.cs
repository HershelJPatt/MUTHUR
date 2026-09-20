using System.Diagnostics;
using System.Net;
using Muthur.Contracts;
using Xunit.Abstractions;

namespace Muthur.Server.Tests;

public sealed class SpecContainmentTests(ITestOutputHelper output) : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "muthur-tests", "containment-" + Guid.NewGuid().ToString("n"));
    private readonly List<(string Path, bool Directory)> _links = [];
    private const string Spec = "# T-1 — Build it\n";

    private string Outside
    {
        get
        {
            var path = Path.Combine(_scratch, "outside");
            Directory.CreateDirectory(Path.Combine(path, "nested"));
            File.WriteAllText(Path.Combine(path, "T-1.md"), Spec + "needs: browser\n");
            File.WriteAllText(Path.Combine(path, "nested/T-1.md"), Spec + "needs: browser\n");
            return path;
        }
    }

    private void Link(string path, string target)
    {
        // Register before creation so a failed fixture is also unlinked before recursive cleanup.
        _links.Add((path, true));
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{path}\" \"{target}\""
        };
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit(10_000), "Junction fixture process did not stop.");
            Assert.Fail("Junction fixture creation timed out.");
        }
        Assert.True(process.ExitCode == 0, $"Cannot create mandatory junction: {stdout.GetAwaiter().GetResult()}{stderr.GetAwaiter().GetResult()}");
    }

    private async Task<(HttpClient Owner, string Id)> ClaimedAsync(string? root = null)
    {
        await _hub.AddProjectAsync(repoPath: root ?? _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build it");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    private static async Task AcceptedAsync(HttpClient owner, string id, string path, string? branch = null)
    {
        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest(path, branch))).ReadTaskAsync();
        Assert.Equal(path, task.SpecPath);
        Assert.Null(task.AttendedReason);
    }

    private static async Task RejectedAsync(HttpClient owner, string id, string path, string code = "spec_outside_repository")
    {
        var before = await owner.GetTaskAsync(id);
        var response = await owner.PostActionAsync(id, "spec", new SetSpecRequest(path)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.ReadErrorAsync();
        Assert.Equal(code, error.Code);
        if (code == "spec_outside_repository")
            Assert.Equal("The spec path must stay inside the project repository.", error.Message);
        if (code == "spec_unreadable")
            Assert.Equal("The spec location could not be determined.", error.Message);
        var after = await owner.GetTaskAsync(id);
        Assert.Equal(before.Task.SpecPath, after.Task.SpecPath);
        Assert.Equal(before.Task.AttendedReason, after.Task.AttendedReason);
        Assert.Equal(before.Events.Select(e => e.Seq), after.Events.Select(e => e.Seq));
    }

    [Fact]
    public async Task Ordinary_and_inward_paths_are_accepted()
    {
        var (owner, id) = await ClaimedAsync();
        _repo.Write("docs/T-1.md", Spec);
        Link(Path.Combine(_repo.Path, "inward"), Path.Combine(_repo.Path, "docs"));
        await AcceptedAsync(owner, id, "docs/T-1.md");
        await AcceptedAsync(owner, id, "inward/T-1.md");
    }

    [Theory]
    [InlineData("T-1.md")]
    [InlineData("nested/T-1.md")]
    [InlineData("missing/T-1.md")]
    public async Task Outward_junctions_refuse_existing_and_missing_suffixes(string suffix)
    {
        var (owner, id) = await ClaimedAsync();
        Link(Path.Combine(_repo.Path, "out"), Outside);
        await RejectedAsync(owner, id, "out/" + suffix);
    }

    [Fact]
    public async Task Rejection_preserves_an_existing_attachment_and_attended_reason()
    {
        var (owner, id) = await ClaimedAsync();
        _repo.Write("docs/T-1.md", Spec + "needs: human\n");
        (await owner.PostActionAsync(id, "spec", new SetSpecRequest("docs/T-1.md"))).EnsureSuccessStatusCode();
        Link(Path.Combine(_repo.Path, "out"), Outside);
        await RejectedAsync(owner, id, "out/T-1.md");
    }

    [Fact]
    public async Task Chained_junctions_are_resolved()
    {
        var (owner, id) = await ClaimedAsync();
        var bridge = Path.Combine(_repo.Path, "bridge");
        Link(bridge, Outside);
        Link(Path.Combine(_repo.Path, "entry"), bridge);
        await RejectedAsync(owner, id, "entry/T-1.md");
    }

    [Fact]
    public async Task Ancestors_of_a_link_target_are_resolved()
    {
        var (owner, id) = await ClaimedAsync();
        var bridge = Path.Combine(_repo.Path, "bridge");
        Link(bridge, Outside);
        Link(Path.Combine(_repo.Path, "entry"), Path.Combine(bridge, "nested"));
        await RejectedAsync(owner, id, "entry/T-1.md");
    }

    [Fact]
    public async Task A_linked_project_root_accepts_its_files_and_refuses_escapes()
    {
        Directory.CreateDirectory(_scratch);
        var via = Path.Combine(_scratch, "via");
        Link(via, _repo.Path);
        var (owner, id) = await ClaimedAsync(via);
        _repo.Write("docs/T-1.md", Spec);
        Link(Path.Combine(_repo.Path, "out"), Outside);
        await AcceptedAsync(owner, id, "docs/T-1.md");
        await RejectedAsync(owner, id, "out/T-1.md");
    }

    [Fact]
    public async Task An_ordinary_missing_file_keeps_its_code()
    {
        var (owner, id) = await ClaimedAsync();
        await RejectedAsync(owner, id, "missing/T-1.md", "spec_missing");
    }

    [Fact]
    public async Task Cyclic_links_are_unreadable_within_a_bounded_time()
    {
        var (owner, id) = await ClaimedAsync();
        var a = Path.Combine(_repo.Path, "a");
        var b = Path.Combine(_repo.Path, "b");
        Link(a, b);
        Link(b, a);
        await RejectedAsync(owner, id, "a/T-1.md", "spec_unreadable");
    }

    [Fact]
    public async Task A_branch_blob_is_accepted_even_when_the_working_tree_path_escapes()
    {
        var (owner, id) = await ClaimedAsync();
        _repo.BranchWithFile("task/T-1-spec", "specs/T-1.md", Spec);
        Link(Path.Combine(_repo.Path, "specs"), Outside);
        await AcceptedAsync(owner, id, "specs/T-1.md", "task/T-1-spec");
    }

    [Fact]
    public async Task File_symlinks_are_checked_where_available()
    {
        var (owner, id) = await ClaimedAsync();
        _repo.Write("docs/T-1.md", Spec);
        var outward = Path.Combine(_repo.Path, "out.md");
        _links.Add((outward, false));
        try
        {
            File.CreateSymbolicLink(outward, Path.Combine(Outside, "T-1.md"));
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 1314)
        {
            output.WriteLine("Optional Windows file symlink fixture unavailable: required privilege is not held.");
            return;
        }
        var inward = Path.Combine(_repo.Path, "in.md");
        _links.Add((inward, false));
        File.CreateSymbolicLink(inward, Path.Combine(_repo.Path, "docs/T-1.md"));
        await RejectedAsync(owner, id, "out.md");
        await AcceptedAsync(owner, id, "in.md");
    }

    public void Dispose()
    {
        _hub.Dispose();
        // Never enumerate through fixture links, including dangling and cyclic links.
        foreach (var (path, directory) in _links.AsEnumerable().Reverse())
        {
            try
            {
                if (directory) Directory.Delete(path);
                else File.Delete(path);
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
        _repo.Dispose();
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }
}
