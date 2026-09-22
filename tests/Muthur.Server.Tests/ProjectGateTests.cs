using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>A project with no required validator says so, in the response and in the ledger.</summary>
public sealed class ProjectGateTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private Task<HttpResponseMessage> AddAsync(string key, params string[] validators) =>
        _hub.Founder().PostAsJsonAsync(Routes.Projects, new AddProjectRequest(key, _hub.DataDir, RequiredValidators: validators, IngestSources: []));

    private Task<HttpResponseMessage> SetValidatorsAsync(string key, params string[] validators) =>
        _hub.Founder().PutAsJsonAsync(Routes.Project(key), new UpdateProjectRequest(RequiredValidators: validators));

    /// <summary>Reads the field an agent or a founder actually sees on their terminal, off the wire.</summary>
    private static async Task<bool> UngatedOnTheWireAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ungated\"", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("ungated").GetBoolean();
    }

    private async Task<IReadOnlyList<EventDto>> EventsAsync() =>
        (await _hub.Founder().GetFromJsonAsync($"{Routes.Events}?limit=200", MuthurJsonContext.Default.IReadOnlyListEventDto))!;

    [Fact]
    public async Task A_project_with_no_required_validator_comes_back_ungated()
    {
        Assert.True(await UngatedOnTheWireAsync(await AddAsync("solo")));

        var shown = await _hub.Founder().GetFromJsonAsync(Routes.Project("solo"), MuthurJsonContext.Default.ProjectDto);
        Assert.True(shown!.Ungated);
        Assert.True(await UngatedOnTheWireAsync(await _hub.Founder().GetAsync(Routes.Project("solo"))));
    }

    [Fact]
    public async Task A_project_with_a_required_validator_comes_back_gated()
    {
        Assert.False(await UngatedOnTheWireAsync(await AddAsync("gated", "win-validator")));

        var shown = await _hub.Founder().GetFromJsonAsync(Routes.Project("gated"), MuthurJsonContext.Default.ProjectDto);
        Assert.False(shown!.Ungated);
    }

    [Fact]
    public async Task Adding_a_validator_flips_the_project_in_the_response_that_changed_it()
    {
        Assert.True(await UngatedOnTheWireAsync(await AddAsync("solo")));

        Assert.False(await UngatedOnTheWireAsync(await SetValidatorsAsync("solo", "win-validator")));

        // And taking the last one away says so again, in the same breath.
        Assert.True(await UngatedOnTheWireAsync(await SetValidatorsAsync("solo")));
    }

    [Fact]
    public async Task The_ledger_records_whether_each_project_was_gated()
    {
        (await AddAsync("solo")).EnsureSuccessStatusCode();
        (await AddAsync("gated", "win-validator")).EnsureSuccessStatusCode();
        (await SetValidatorsAsync("solo", "win-validator")).EnsureSuccessStatusCode();

        var events = await EventsAsync();
        var added = events.Where(e => e.Type == "project.added").ToList();
        Assert.True(added.Single(e => e.Payload.GetProperty("project").GetString() == "solo").Payload.GetProperty("ungated").GetBoolean());
        Assert.False(added.Single(e => e.Payload.GetProperty("project").GetString() == "gated").Payload.GetProperty("ungated").GetBoolean());

        var updated = Assert.Single(events, e => e.Type == "project.updated");
        Assert.False(updated.Payload.GetProperty("ungated").GetBoolean());
    }
}

/// <summary>
/// What the live hub's ledger looked like after an upgrade: a list column that a later migration added with an
/// empty default, on a row written before the migration. Every read of the project - the board, the doctor, the
/// projects API - failed on it.
/// </summary>
public sealed class ProjectListColumnTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task A_list_column_left_empty_by_a_migration_reads_as_no_entries()
    {
        (await _hub.Founder().PostAsJsonAsync(Routes.Projects,
            new AddProjectRequest("old", _hub.DataDir, RequiredValidators: ["win-validator"], IngestSources: []))).EnsureSuccessStatusCode();
        await using (var db = await _hub.Services.GetRequiredService<IDbContextFactory<Muthur.Data.MuthurDb>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE projects SET ingest_sources = '' WHERE key = 'old'");

        var projects = await _hub.Founder().GetFromJsonAsync(Routes.Projects, MuthurJsonContext.Default.IReadOnlyListProjectDto);

        var project = Assert.Single(projects!);
        Assert.Empty(project.IngestSources);
        Assert.Equal(["win-validator"], project.RequiredValidators);
        Assert.Equal([], Muthur.Data.StringListConverter.Read(""));
        Assert.Equal([], Muthur.Data.StringListConverter.Read("   "));
    }
}
