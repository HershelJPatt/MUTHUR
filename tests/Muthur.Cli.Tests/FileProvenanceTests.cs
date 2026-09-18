using System.Diagnostics;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur role define --brief-file` reads a file out of a working tree that moves under the founder about
/// twice an hour. These run against a real temporary repository, because what is being tested is what git
/// actually says — a faked runner would only test the story we told ourselves about git.
/// </summary>
public sealed class FileProvenanceTests : IDisposable
{
    private const string Branch = "provenance";

    private readonly string dir = Directory.CreateTempSubdirectory("muthur-provenance-").FullName;

    public FileProvenanceTests()
    {
        Git("init", "-q", "-b", Branch);
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Write("brief.md", "the brief as committed");
        Write("role brief.md", "a name with a space in it");
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
    public void A_committed_file_names_the_ref_and_commit_it_came_from()
    {
        var source = FileProvenance.Describe(Path("brief.md"));

        Assert.NotNull(source);
        Assert.Equal(Branch, source.Value.Ref);
        Assert.Equal(Git("rev-parse", "--short", "HEAD"), source.Value.Commit);
        Assert.False(FileProvenance.IsDirty(Path("brief.md")));
    }

    [Fact]
    public void The_same_file_edited_is_dirty()
    {
        Write("brief.md", "words the hub has never seen");

        Assert.True(FileProvenance.IsDirty(Path("brief.md")));
    }

    [Fact]
    public void A_file_outside_any_repository_has_no_provenance_and_is_not_dirty()
    {
        var outside = Directory.CreateTempSubdirectory("muthur-provenance-loose-").FullName;
        var file = System.IO.Path.Combine(outside, "brief.md");
        File.WriteAllText(file, "a brief that belongs to nothing");
        try
        {
            Assert.Null(FileProvenance.Describe(file));
            Assert.False(FileProvenance.IsDirty(file));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Only_the_named_file_counts()
    {
        Write("role brief.md", "someone else's edit, in the same tree");

        Assert.False(FileProvenance.IsDirty(Path("brief.md")));
    }

    [Fact]
    public void A_path_with_a_space_is_read_like_any_other()
    {
        Assert.False(FileProvenance.IsDirty(Path("role brief.md")));
        Write("role brief.md", "edited");

        Assert.True(FileProvenance.IsDirty(Path("role brief.md")));
        Assert.Equal(Branch, FileProvenance.Describe(Path("role brief.md"))?.Ref);
    }

    [Fact]
    public void A_file_staged_but_never_committed_is_dirty()
    {
        // It matches no commit, which is exactly what the refusal is for.
        Write("fresh.md", "added but not committed");
        Git("add", "fresh.md");

        Assert.True(FileProvenance.IsDirty(Path("fresh.md")));
    }
}
