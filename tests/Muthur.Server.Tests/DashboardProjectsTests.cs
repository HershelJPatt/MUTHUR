namespace Muthur.Server.Tests;

public sealed class DashboardProjectsTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task A_project_without_validators_shows_its_ungated_status_and_consequence()
    {
        await _hub.AddProjectAsync("solo");

        var page = await _hub.CreateClient().GetStringAsync("/projects");

        Assert.Contains("pill-ungated", page);
        Assert.Contains(">ungated<", page);
        Assert.Contains("goes straight to validated", page);
    }

    [Fact]
    public async Task A_project_with_a_required_validator_lists_it_without_ungated_messaging()
    {
        await _hub.AddProjectAsync("gated", validators: ["win-validator"]);

        var page = await _hub.CreateClient().GetStringAsync("/projects");

        Assert.DoesNotContain("pill-ungated", page);
        Assert.DoesNotContain("ungated", page);
        Assert.DoesNotContain("goes straight to validated", page);
        Assert.Contains("<span class=\"pill pill-role\">win-validator</span>", page);
    }
}
