using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>The project and repo checks: is this project gated, and is its checkout somewhere MUTHUR could land?</summary>
public sealed class DoctorProjectTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private async Task<IReadOnlyList<CheckDto>> ReportAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe=false", MuthurJsonContext.Default.DoctorDto))!.Checks;

    /// <summary>A project yields more than one "project" row, so a row is found by what it says.</summary>
    private static CheckDto Row(IReadOnlyList<CheckDto> checks, string category, string subject, string says = "") =>
        checks.Single(c => c.Category == category && c.Subject == subject && c.Detail.Contains(says, StringComparison.Ordinal));

    [Fact]
    public async Task A_project_nobody_validates_says_so_out_loud()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path);

        var gate = Row(await ReportAsync(), "project", "scratch", "No required validators");
        Assert.Equal(CheckStatus.Warn, gate.Status);
        Assert.Contains("muthur project set scratch --validator <role>", gate.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_gated_by_a_role_nobody_defined_is_not_gated_at_all()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path, validators: ["win-validator"]);

        var gate = Row(await ReportAsync(), "project", "scratch", "Requires validator");
        Assert.Equal(CheckStatus.Fail, gate.Status);
        Assert.Equal("Requires validator 'win-validator', which is not a defined role. muthur role define win-validator --founder", gate.Detail);
    }

    [Fact]
    public async Task A_repository_that_is_not_there_is_said_once()
    {
        var gone = Path.Combine(Path.GetTempPath(), "muthur-tests", "gone-" + Guid.NewGuid().ToString("n"));
        await _hub.AddProjectAsync("scratch", gone);

        var checks = await ReportAsync();
        var repo = Row(checks, "repo", "scratch");
        Assert.Equal(CheckStatus.Fail, repo.Status);
        Assert.Equal($"{gone} does not exist; nothing in this project can be built or landed.", repo.Detail);

        // Two checks shouting the same missing directory is noise: the manifest check stays quiet.
        Assert.DoesNotContain(checks, c => c.Detail.Contains("muthur.project.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dirty_working_tree_is_a_warning_because_MUTHUR_will_not_merge_in_one()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path);
        _repo.Write("README.md", "# uncommitted\n");

        var repo = Row(await ReportAsync(), "repo", "scratch");
        Assert.Equal(CheckStatus.Warn, repo.Status);
        Assert.Equal("Working tree is dirty; MUTHUR refuses to merge in a checkout with uncommitted changes.", repo.Detail);
    }

    [Fact]
    public async Task A_clean_checkout_of_the_default_branch_with_a_manifest_is_ok()
    {
        _repo.Write("muthur.project.json", """{"key":"scratch","build":"dotnet build","test":"dotnet test"}""");
        _repo.Commit("add the project manifest");
        await _hub.AddProjectAsync("scratch", _repo.Path);

        var checks = await ReportAsync();
        var repo = Row(checks, "repo", "scratch");
        Assert.Equal(CheckStatus.Ok, repo.Status);
        Assert.Equal("Clean checkout of 'main'.", repo.Detail);

        var manifest = Row(checks, "project", "scratch", "muthur.project.json");
        Assert.Equal(CheckStatus.Ok, manifest.Status);
        Assert.Equal("muthur.project.json names how to build, test and run it.", manifest.Detail);
    }
}
