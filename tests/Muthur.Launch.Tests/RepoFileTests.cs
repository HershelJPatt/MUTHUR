using System.Diagnostics;
using Muthur.Launch;

namespace Muthur.Launch.Tests;

/// <summary>
/// The two rules every client of `role define` obeys. The dirty and provenance ones run against a real
/// temporary repository, because what is being tested is what git actually says — a faked runner would only
/// test the story we told ourselves about git.
/// </summary>
public sealed class RepoFileTests : IDisposable
{
    private const string Branch = "repofile";

    private readonly string dir = Directory.CreateTempSubdirectory("muthur-repofile-").FullName;
    private readonly IProcessRunner processes = new ProcessRunner();

    public RepoFileTests()
    {
        Git("init", "-q", "-b", Branch);
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Write("brief.md", "the brief as committed");
        Write("sibling.md", "another brief in the same tree");
        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");
    }

    private string Path(string name) => System.IO.Path.Combine(dir, name);

    private void Write(string name, string content) => File.WriteAllText(Path(name), content);

    private string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal); // git marks objects read-only
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_committed_file_matches_a_commit_and_names_the_one_it_came_from()
    {
        Assert.False(await RepoFile.IsDirtyAsync(processes, Path("brief.md")));

        var source = await RepoFile.DescribeAsync(processes, Path("brief.md"));

        Assert.NotNull(source);
        Assert.Equal(Branch, source.Value.Ref);
        Assert.Equal(Git("rev-parse", "--short", "HEAD"), source.Value.Commit);
    }

    [Fact]
    public async Task The_same_file_edited_is_dirty()
    {
        Write("brief.md", "words the hub has never seen");

        Assert.True(await RepoFile.IsDirtyAsync(processes, Path("brief.md")));
    }

    [Fact]
    public async Task A_file_staged_but_never_committed_is_dirty()
    {
        // It matches no commit, which is exactly what the refusal is for.
        Write("fresh.md", "added but not committed");
        Git("add", "fresh.md");

        Assert.True(await RepoFile.IsDirtyAsync(processes, Path("fresh.md")));
    }

    [Fact]
    public async Task A_file_the_repository_has_never_heard_of_is_dirty()
    {
        // The gap `status --untracked-files=no` leaves: no commit contains this, so a brief installed from it
        // would match none.
        Write("never-added.md", "a brief in the tree that no commit contains");

        Assert.True(await RepoFile.IsDirtyAsync(processes, Path("never-added.md")));
    }

    [Fact]
    public async Task Only_the_named_file_counts()
    {
        Write("sibling.md", "someone else's edit, in the same tree");

        Assert.False(await RepoFile.IsDirtyAsync(processes, Path("brief.md")));
        Assert.True(await RepoFile.IsDirtyAsync(processes, Path("sibling.md")));
    }

    [Fact]
    public async Task A_file_outside_any_repository_has_no_provenance_and_is_not_dirty()
    {
        var outside = Directory.CreateTempSubdirectory("muthur-repofile-loose-").FullName;
        var file = System.IO.Path.Combine(outside, "brief.md");
        File.WriteAllText(file, "a brief that belongs to nothing");
        try
        {
            Assert.Null(await RepoFile.DescribeAsync(processes, file));
            Assert.False(await RepoFile.IsDirtyAsync(processes, file));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_path_under_the_root_is_inside_it_however_deep()
    {
        Assert.True(RepoFile.IsInside(dir, Path("brief.md")));
        Assert.True(RepoFile.IsInside(dir, System.IO.Path.Combine(dir, "specs", "briefs", "role.md")));
    }

    [Fact]
    public void A_trailing_separator_on_the_root_changes_nothing()
    {
        Assert.True(RepoFile.IsInside(dir + System.IO.Path.DirectorySeparatorChar, Path("brief.md")));
    }

    [Fact]
    public void The_root_itself_is_not_a_file_inside_it()
    {
        Assert.False(RepoFile.IsInside(dir, dir));
    }

    [Fact]
    public void A_sibling_sharing_the_roots_prefix_is_not_inside_it()
    {
        // The whole reason the rule tests the character after the root: as a plain prefix match, '/repo-evil'
        // is inside '/repo'.
        Assert.False(RepoFile.IsInside(dir, System.IO.Path.Combine(dir + "-evil", "brief.md")));
    }

    [Fact]
    public void A_path_that_climbs_out_with_dot_dot_is_not_inside()
    {
        Assert.False(RepoFile.IsInside(dir, System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "..", "brief.md"))));
    }

    [Fact]
    public void Case_decides_containment_the_way_the_filesystem_does()
    {
        // Windows would open the file either way, so a root differing only in case must still contain it;
        // elsewhere those are two different directories and the Ordinal answer is the safe one.
        var shouted = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir)!, System.IO.Path.GetFileName(dir).ToUpperInvariant());

        Assert.Equal(OperatingSystem.IsWindows(), RepoFile.IsInside(shouted, Path("brief.md")));
    }

    [Fact]
    public void A_root_this_machine_will_not_accept_contains_nothing()
    {
        // A root that cannot be full-pathed is refused rather than thrown out of: false is the answer that
        // keeps the caller's guard a guard.
        Assert.False(RepoFile.IsInside("\0no-such-root", Path("brief.md")));
    }
}
