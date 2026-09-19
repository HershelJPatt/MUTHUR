using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Core;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// The rules the console's Roles control reads a brief under. Both of them: the path stays inside a
/// repository the hub knows, and the file matches a commit. The second is the one the console did not have,
/// which made it the laxer of the two doors onto <c>role define</c>.
/// </summary>
public sealed class BriefFileReaderTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private BriefFileReader Briefs => _hub.Services.GetRequiredService<BriefFileReader>();

    private Task<MuthurException> RefusedAsync(string path, params string[] repositories) =>
        Assert.ThrowsAsync<MuthurException>(() => Briefs.ReadAsync(repositories, path));

    [Fact]
    public async Task A_committed_brief_is_read()
    {
        _repo.Write("briefs/clean.md", "# clean\n\nThe brief as committed.\n");
        _repo.Commit("a brief");

        var brief = await Briefs.ReadAsync([_repo.Path], "briefs/clean.md");

        Assert.Equal("# clean\n\nThe brief as committed.\n", brief);
    }

    /// <summary>
    /// The divergence this task exists to close. The CLI has refused this since T-24; the console installed it
    /// silently. The code is spelled the same in both places — the other is <c>RoleCommands.cs</c>, where
    /// <c>--brief-file</c> refuses a dirty file before it ever reaches the hub.
    /// </summary>
    [Fact]
    public async Task An_edited_brief_is_refused_with_the_code_the_cli_uses()
    {
        _repo.Write("briefs/edited.md", "# committed\n");
        _repo.Commit("a brief");
        _repo.Write("briefs/edited.md", "# what the founder would install instead\n");

        var refused = await RefusedAsync("briefs/edited.md", _repo.Path);

        Assert.Equal("brief_file_dirty", refused.Code);
        Assert.Equal(ErrorKind.RuleViolation, refused.Kind);
        Assert.Contains("has no committed version", refused.Message, StringComparison.Ordinal);
        Assert.Contains("briefs/edited.md", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A file git has never heard of has no committed version either, and answers the same way.</summary>
    [Fact]
    public async Task A_brief_that_was_never_added_is_refused_as_dirty()
    {
        _repo.Write("briefs/never-added.md", "# not in any commit\n");

        var refused = await RefusedAsync("briefs/never-added.md", _repo.Path);

        Assert.Equal("brief_file_dirty", refused.Code);
    }

    /// <summary>
    /// The first repository holding the path is the answer, and a dirty file there is refused rather than
    /// falling through — or the refusal would be skippable by leaving a committed copy in another project, and
    /// a founder with two projects would sometimes install a brief from the one they were not looking at.
    /// </summary>
    [Fact]
    public async Task A_dirty_brief_in_the_first_repository_does_not_fall_through_to_the_second()
    {
        using var second = new TestRepo();
        _repo.Write("briefs/both.md", "# the first\n");
        _repo.Commit("a brief");
        _repo.Write("briefs/both.md", "# the first, edited\n");
        second.Write("briefs/both.md", "# the second, committed\n");
        second.Commit("a brief");

        var refused = await RefusedAsync("briefs/both.md", _repo.Path, second.Path);

        Assert.Equal("brief_file_dirty", refused.Code);

        // And with the order reversed it is the second repository's committed copy that is read, so what the
        // test above proves is the stopping and not simply that one of the two was unreadable.
        Assert.Equal("# the second, committed\n", await Briefs.ReadAsync([second.Path, _repo.Path], "briefs/both.md"));
    }

    /// <summary>
    /// A path that climbs out is refused, and so is a sibling that merely shares the root's prefix. Both answer
    /// <c>brief_file_missing</c> on purpose: a browser tab that said "outside the repository" for one and "no
    /// such file" for the other would tell whoever is typing which absolute paths exist.
    /// </summary>
    [Fact]
    public async Task A_path_that_escapes_the_repository_is_refused()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_repo.Path)!, "brief-outside-" + Guid.NewGuid().ToString("n") + ".md");
        File.WriteAllText(outside, "# somebody else's file\n");
        var sibling = _repo.Path + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "brief.md"), "# a neighbour that merely shares a prefix\n");

        try
        {
            var up = await RefusedAsync($"../{Path.GetFileName(outside)}", _repo.Path);
            Assert.Equal("brief_file_missing", up.Code);

            // The subtle one: '<repo>-evil' starts with '<repo>' but is not inside it.
            var neighbour = await RefusedAsync($"../{Path.GetFileName(sibling)}/brief.md", _repo.Path);
            Assert.Equal("brief_file_missing", neighbour.Code);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_brief_path_the_founder_left_blank_says_what_the_field_is_for(string path)
    {
        var refused = await RefusedAsync(path, _repo.Path);

        Assert.Equal("brief_file_required", refused.Code);
        Assert.Contains("from the repository root", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>With no project there is no repository to resolve against, which is its own answer.</summary>
    [Fact]
    public async Task No_project_configured_is_refused_before_any_path_is_resolved()
    {
        var refused = await RefusedAsync("briefs/clean.md");

        Assert.Equal("no_repository", refused.Code);
        Assert.Contains("muthur project add", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The refusal names the path the founder typed, not the absolute one they did not.</summary>
    [Fact]
    public async Task A_path_to_no_file_names_the_path_the_founder_typed()
    {
        var refused = await RefusedAsync("briefs/typo.md", _repo.Path);

        Assert.Equal("brief_file_missing", refused.Code);
        Assert.Contains("'briefs/typo.md'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(_repo.Path, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page still renders with the new injection. An injected service nobody registered is a runtime
    /// failure on the first request and a compile catches none of it.
    /// </summary>
    [Fact]
    public async Task The_console_page_still_renders_with_the_brief_reader_injected()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path);

        var response = await _hub.CreateClient().GetAsync("/console");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("An unhandled error", await response.Content.ReadAsStringAsync());
    }
}
