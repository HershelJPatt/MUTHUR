using System.Net;
using System.Text.Json;
using Muthur.Contracts;
using Muthur.Server.Components.Shared;

namespace Muthur.Server.Tests;

public sealed class EventTextTests
{
    [Fact]
    public void Describe_lists_scalar_properties_in_payload_order()
    {
        Assert.Equal(
            "title: ship it · priority: 2 · urgent: true",
            Describe("""{"title":"ship it","priority":2,"urgent":true,"reason":null,"before":{"state":"backlog"}}"""));
    }

    [Fact]
    public void Describe_joins_array_items_and_skips_empty_ones()
    {
        Assert.Equal("roles: implementer, validator", Describe("""{"roles":["implementer","validator"],"tags":[]}"""));
    }

    [Fact]
    public void Describe_is_empty_when_nothing_is_usable()
    {
        Assert.Equal("", Describe("{}"));
        Assert.Equal("", Describe("\"a bare string\""));
        Assert.Equal("", Describe("""{"reason":null,"note":"","detail":{"a":1}}"""));
    }

    [Fact]
    public void Describe_truncates_long_payloads_to_160_characters()
    {
        var text = Describe("{\"body\":\"" + new string('x', 300) + "\"}");

        Assert.Equal(160, text.Length);
        Assert.EndsWith("…", text);
    }

    [Theory]
    [InlineData("validation.failed", "fail")]
    [InlineData("lease.expired", "fail")]
    [InlineData("task.rejected", "fail")]
    [InlineData("task.claim_expired", "fail")]
    [InlineData("agent.limited", "warn")]
    [InlineData("task.blocked", "warn")]
    [InlineData("task.cancelled", "warn")]
    [InlineData("task.released", "warn")]
    [InlineData("task.added", "")]
    public void Tone_marks_failures_and_hold_ups(string type, string expected) => Assert.Equal(expected, EventText.Tone(type));

    private static string Describe(string payload) =>
        EventText.Describe(new EventDto(1, DateTimeOffset.UnixEpoch, "someone", null, "task.added", null, Payload(payload)));

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed class StreamPanelTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task The_stream_page_renders_the_panel_and_its_filters()
    {
        var response = await _hub.CreateClient().GetAsync("/stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("panel-title\">Stream", html);
        Assert.Contains(">All<", html);
        Assert.Contains(">Tasks<", html);
        Assert.Contains(">Agents<", html);
    }
}
