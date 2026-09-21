using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

public sealed class IncidentCommandTests
{
    private static RootCommand Root()
    {
        var root = new RootCommand();
        IncidentCommands.AddTo(root);
        return root;
    }

    [Theory]
    [InlineData("incident add title --project p --signature s --path p --configuration c --recovery r")]
    [InlineData("incident list --project p")]
    [InlineData("incident show I-1")]
    [InlineData("incident update I-1 --diagnosis d --workaround w --authorization a")]
    [InlineData("incident transition I-1 --state confirmed --evidence e")]
    [InlineData("incident observe I-1 --task T-2 --evidence e --signature s --path p --configuration c --run run-1")]
    [InlineData("incident unlink I-1 --observation 7 --reason r")]
    [InlineData("incident match --project p --signature s --path p --configuration c")]
    [InlineData("incident suppress I-1 --task T-2 --assignment #orchestrator --observation 7 --reason r")]
    [InlineData("incident recover I-1 --kind probe --evidence e")]
    [InlineData("incident recover I-1 --kind configuration --configuration changed --evidence e")]
    [InlineData("incident metrics I-1 --hours 720")]
    public void All_incident_actions_are_routed_and_parse(string command) => Assert.Empty(Root().Parse(command).Errors);

    [Fact]
    public void Opaque_match_identifiers_are_escaped_independently()
    {
        Assert.Equal("?project=a%26b&signature=a%3Fb%3Dc%23d&path=C%3A%5Ca%20b&configuration=%E2%9C%93%2Bv1",
            IncidentCommands.MatchQuery("a&b", "a?b=c#d", @"C:\a b", "✓+v1"));
        Assert.Equal("/api/v1/incidents/I-1%2Fbad/recover", Routes.IncidentAction("I-1/bad", "recover"));
    }

    [Fact]
    public void Probe_help_requires_attestation_and_metrics_default_is_24()
    {
        var root = Root();
        var incident = Assert.Single(root.Subcommands);
        Assert.Contains("successful bounded probe for this exact condition", incident.Subcommands.Single(c => c.Name == "recover").Description);
        var metrics = incident.Subcommands.Single(c => c.Name == "metrics");
        var hours = Assert.IsType<Option<int>>(Assert.Single(metrics.Options));
        Assert.Equal(24, root.Parse("incident metrics I-1").GetValue(hours));
        Assert.Equal("--hours", hours.Name);
    }

    [Fact]
    public void Requests_and_responses_have_source_generated_metadata()
    {
        Type[] types = [typeof(AddIncidentRequest), typeof(UpdateIncidentRequest), typeof(TransitionIncidentRequest),
            typeof(ObserveIncidentRequest), typeof(UnlinkIncidentRequest), typeof(SuppressIncidentRequest), typeof(RecoverIncidentRequest),
            typeof(IncidentDto), typeof(IncidentDetailDto), typeof(IncidentMatchesDto), typeof(IncidentMetricsDto), typeof(TaskIncidentDto)];
        foreach (var type in types) Assert.NotNull(MuthurJsonContext.Default.GetTypeInfo(type));
        var request = new ObserveIncidentRequest("T-1", "<raw> \"evidence\"", "s", "p", "c", "run");
        var json = JsonSerializer.Serialize(request, MuthurJsonContext.Default.ObserveIncidentRequest);
        Assert.Equal(request, JsonSerializer.Deserialize(json, MuthurJsonContext.Default.ObserveIncidentRequest));
        var update = JsonSerializer.Deserialize("{\"diagnosis\":\"shared\"}", MuthurJsonContext.Default.UpdateIncidentRequest)!;
        Assert.Null(update.Authorization);
        Assert.Null(update.Workaround);
        var metric = new IncidentMetricsDto("I-1", default, default, [], 0, 0, 0, 0, 0, 0, 0, 0, null, 0, 0, null, [], [], "unknown", ["missing"]);
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(metric, MuthurJsonContext.Default.IncidentMetricsDto));
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("actualProcessStarts").ValueKind);
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("diagnosisSessions").ValueKind);
    }
}
