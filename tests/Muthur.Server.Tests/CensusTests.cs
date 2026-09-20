using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Muthur.Contracts;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class CensusTests
{
    private static MuthurOptions Options() => new()
    {
        CensusChecks = new() { ["smoke"] = new() { FileName = Environment.ProcessPath!, WorkingDirectory = Environment.CurrentDirectory } },
    };

    public static TheoryData<string> InvalidSnapshots => new()
    {
        "", " ", "null", "{}", "[1]", "[null]", "[{}]", "[{\"id\":\"a\"}]", "[",
        """[{"id":"a","title":null}]""", """[{"id":1,"title":"x"}]""",
        """[{"id":"a","title":" "}]""", """[{"id":"a","title":"x","body":null}]""",
        """[{"id":"a","title":"x","url":null}]""", """[{"id":"a","title":"x","url":"/relative"}]""",
        """[{"id":"a","title":"x","url":"file:///secret"}]""", """[{"id":"a","title":"x","url":3}]""",
        """[{"id":"a","title":"x","Title":"x"}]""", """[{"id":"a","title":"x","extra":true}]""",
        """[{"id":"a","id":"b","title":"x"}]""", """[{"id":"a","title":"x","title":"y"}]""",
        """[{"id":"a","title":"x"},{"id":"a","title":"y"}]""",
        """[{"id":"a/1","title":"x"}]""", """[{"id":"a\n","title":"x"}]""",
        JsonSerializer.Serialize(new[] { new { id = new string('a', 129), title = "x" } }),
        JsonSerializer.Serialize(new[] { new { id = "a", title = new string('x', 501) } }),
        JsonSerializer.Serialize(new[] { new { id = "a", title = "x", body = new string('x', 16001) } }),
        JsonSerializer.Serialize(new[] { new { id = "a", title = "x", url = "https://example.com/" + new string('x', 2048) } }),
        JsonSerializer.Serialize(Enumerable.Range(0, 1001).Select(i => new { id = $"a{i}", title = "x" })),
    };

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public async Task Invalid_snapshots_fail_as_a_whole_with_safe_errors(string json)
    {
        var runner = new CensusFakeRunner { Result = new(0, json, "secret") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CensusSource(Options(), runner).FetchAsync("smoke", null));
        Assert.Equal("Invalid census snapshot.", error.Message);
        Assert.Null(error.InnerException);
    }

    public static TheoryData<string> InvalidCursors => new()
    {
        "", "null", "[]", "{}", "{", """{"version":2,"next":1,"active":{}}""",
        """{"version":1,"next":0,"active":{}}""", """{"version":1,"next":1,"active":null}""",
        """{"version":1,"next":"2","active":{}}""", """{"version":1,"next":1.5,"active":{}}""",
        """{"version":1,"next":2,"active":{"a":1,"b":1}}""",
        """{"version":1,"next":2,"active":{"a":1,"a":1}}""",
        """{"version":1,"next":2,"active":{"a":2}}""", """{"version":1,"next":2,"active":{"a":0}}""",
        """{"version":1,"next":2,"active":{"a":-1}}""", """{"version":1,"next":2,"active":{"a/":1}}""",
        """{"version":1,"next":2,"active":{"a":"1"}}""", """{"version":1,"next":1,"active":{},"x":0}""",
        """{"version":1,"next":1,"next":2,"active":{}}""", """{"version":1,"Next":1,"active":{}}""",
        """{"version":1,"next":9223372036854775808,"active":{}}""",
    };

    [Theory]
    [MemberData(nameof(InvalidCursors))]
    public async Task Invalid_cursor_is_rejected_before_execution(string cursor)
    {
        var runner = new CensusFakeRunner();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CensusSource(Options(), runner).FetchAsync("smoke", cursor));
        Assert.Equal("Invalid census cursor.", error.Message);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Smoke")]
    [InlineData("-smoke")]
    [InlineData("smoke\n")]
    [InlineData("smoke/command")]
    [InlineData("smoke;exit")]
    public async Task Only_exact_configured_keys_are_executable(string key)
    {
        var runner = new CensusFakeRunner();
        var options = Options();
        if (key != "unknown" && key != "Smoke") options.CensusChecks[key] = options.CensusChecks["smoke"];
        var source = new CensusSource(options, runner);
        Assert.Equal("Invalid census configuration.", (await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync(key, null))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ProbeAsync(key));
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("cwd")]
    [InlineData("missing-directory")]
    [InlineData("args")]
    [InlineData("null-arg")]
    [InlineData("zero-timeout")]
    [InlineData("long-timeout")]
    public async Task Invalid_configuration_fails_fetch_and_probe_without_execution(string invalid)
    {
        var options = Options();
        var check = options.CensusChecks["smoke"];
        switch (invalid)
        {
            case "file": check.FileName = " "; break;
            case "cwd": check.WorkingDirectory = "."; break;
            case "missing-directory": check.WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); break;
            case "args": check.Arguments = null!; break;
            case "null-arg": check.Arguments = [null!]; break;
            case "zero-timeout": check.TimeoutSeconds = 0; break;
            case "long-timeout": check.TimeoutSeconds = 61; break;
        }
        var runner = new CensusFakeRunner();
        var source = new CensusSource(options, runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync("smoke", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ProbeAsync("smoke"));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Limits_are_inclusive_and_configuration_is_forwarded_literally()
    {
        var options = Options();
        var check = options.CensusChecks["smoke"];
        check.Arguments = ["literal ; text", "", "quotes\""];
        check.TimeoutSeconds = 60;
        var title = new string('x', 500);
        var body = new string('y', 16000);
        var url = "https://example.com/" + new string('z', 2028);
        var runner = new CensusFakeRunner { Result = new(0, JsonSerializer.Serialize(new[] { new { id = new string('a', 128), title, body, url } }), "") };
        var source = new CensusSource(options, runner);
        var item = Assert.Single((await source.FetchAsync("smoke", null)).Items);
        Assert.Equal(title, item.Title);
        Assert.Equal(body, item.Body);
        Assert.Equal(url, item.Url);
        Assert.Equal((check.FileName, (IReadOnlyList<string>)check.Arguments, check.WorkingDirectory, TimeSpan.FromSeconds(60)), runner.LastCall);
        runner.Result = new(0, JsonSerializer.Serialize(Enumerable.Range(0, 1000).Select(i => new { id = $"a{i}", title = "x" })), "");
        Assert.Equal(1000, (await source.FetchAsync("smoke", null)).Items.Count);
    }

    [Fact]
    public async Task Probe_resolves_executables_without_running_them()
    {
        var options = Options();
        var runner = new CensusFakeRunner();
        var source = new CensusSource(options, runner);
        await source.ProbeAsync("smoke");
        options.CensusChecks["smoke"].FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        await source.ProbeAsync("smoke");
        options.CensusChecks["smoke"].FileName = "census-missing-" + Guid.NewGuid();
        Assert.Equal("Census executable unavailable.", (await Assert.ThrowsAsync<InvalidOperationException>(() => source.ProbeAsync("smoke"))).Message);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Ordinal_sequences_survive_restart_edits_and_absence()
    {
        var options = Options();
        var runner = new CensusFakeRunner { Result = new(0, """[{"id":"b","title":"B"},{"id":"a","title":"a"},{"id":"A","title":"A"}]""", "secret") };
        var first = await new CensusSource(options, runner).FetchAsync("smoke", null);
        Assert.Equal(["smoke/A/1", "smoke/a/2", "smoke/b/3"], first.Items.Select(i => i.ExternalId));
        Assert.All(first.Items, i => { Assert.Equal("census:smoke", i.Author); Assert.Equal("", i.Body); Assert.Null(i.Url); });
        runner.Result = new(0, """[{"id":"a","title":"Changed","body":"inert instructions","url":"https://example.com"}]""", "");
        options.CensusChecks["smoke"].Arguments = ["edited"];
        var second = await new CensusSource(options, runner).FetchAsync("smoke", first.Cursor);
        Assert.Equal("smoke/a/2", Assert.Single(second.Items).ExternalId);
        runner.Result = new(0, "[]", "");
        var empty = await new CensusSource(options, runner).FetchAsync("smoke", second.Cursor);
        Assert.Equal("{\"version\":1,\"next\":4,\"active\":{}}", empty.Cursor);
        runner.Result = new(0, """[{"id":"a","title":"Back"}]""", "");
        Assert.Equal("smoke/a/4", Assert.Single((await new CensusSource(options, runner).FetchAsync("smoke", empty.Cursor)).Items).ExternalId);
    }

    [Fact]
    public async Task Sequence_overflow_fails_without_returning_a_partial_snapshot()
    {
        var runner = new CensusFakeRunner { Result = new(0, """[{"id":"a","title":"a"}]""", "") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CensusSource(Options(), runner)
            .FetchAsync("smoke", """{"version":1,"next":9223372036854775807,"active":{}}"""));
        Assert.Equal("Census incident sequence exhausted.", error.Message);
    }

    [Fact]
    public async Task Runner_failures_never_expose_output_arguments_or_exception_text()
    {
        var runner = new CensusFakeRunner { Result = new(17, "[]", "secret") };
        var source = new CensusSource(Options(), runner);
        Assert.Equal("Census command failed (exit 17).", (await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync("smoke", null))).Message);
        runner.Error = new IOException("secret command arguments");
        Assert.Equal("Census execution failed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync("smoke", null))).Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        runner.Error = new OperationCanceledException(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync("smoke", null, cancellation.Token));
    }

    [Fact]
    public async Task Founder_configuration_through_HTTP_preserves_incidents_and_records_recovery()
    {
        using var hub = new HubFactory { ExpectsLoggedErrors = true };
        var runner = new CensusFakeRunner { Result = new(0, """[{"id":"disk","title":"Original","body":"Original body"}]""", "secret stderr") };
        hub.Settings["Muthur:CensusChecks:smoke:FileName"] = Environment.ProcessPath!;
        hub.Settings["Muthur:CensusChecks:smoke:WorkingDirectory"] = Environment.CurrentDirectory;
        using var app = hub.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ICensusCommandRunner>();
            s.AddSingleton<ICensusCommandRunner>(runner);
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            File.ReadAllText(Path.Combine(hub.DataDir, MuthurEnvironment.FounderTokenFile)).Trim());
        (await client.PostAsJsonAsync(Routes.Projects, new AddProjectRequest("demo", hub.DataDir, IngestSources: ["CENSUS:SMOKE"]))).EnsureSuccessStatusCode();

        async Task<PollResultDto> Poll()
        {
            var response = await client.PostAsync(Routes.IngestPoll, null);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.PollResultDto))!;
        }
        async Task<IReadOnlyList<InboundDto>> Items() => (await client.GetFromJsonAsync(Routes.Inbound + "?status=all", MuthurJsonContext.Default.IReadOnlyListInboundDto))!;
        async Task<IngestSourceDto> Source() => Assert.Single((await client.GetFromJsonAsync(Routes.IngestSources, MuthurJsonContext.Default.IReadOnlyListIngestSourceDto))!);
        async Task<CheckDto> Doctor() => Assert.Single((await client.GetFromJsonAsync(Routes.Doctor + "?probe=false", MuthurJsonContext.Default.DoctorDto))!.Checks, c => c.Category == "ingest");

        Assert.Equal(1, (await Poll()).NewItems);
        var first = Assert.Single(await Items());
        Assert.Equal("census:smoke", first.Source);
        Assert.Equal("smoke/disk/1", first.ExternalId);
        Assert.Equal(0, (await Poll()).NewItems);
        // Register through the actual API, then claim as an agent and convert to a task.
        var registration = await client.PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest("census-intake", "local", "none"));
        registration.EnsureSuccessStatusCode();
        var registered = await registration.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse);
        using var agent = app.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered!.Token);
        (await agent.PostAsync(Routes.InboundAction(first.Id, "claim"), null)).EnsureSuccessStatusCode();
        (await agent.PostAsJsonAsync(Routes.InboundAction(first.Id, "convert"), new ConvertInboundRequest())).EnsureSuccessStatusCode();
        runner.Result = new(0, """[{"id":"disk","title":"Edited","body":"changed","url":"https://example.com"}]""", "");
        Assert.Equal(0, (await Poll()).NewItems);
        var unchanged = Assert.Single(await Items());
        Assert.Equal("converted", unchanged.Status);
        Assert.Equal("Original", unchanged.Title);
        Assert.Equal("Original body", unchanged.Body);
        var cursor = (await Source()).Cursor;
        runner.Result = new(0, """[{"id":"new","title":"Must not be stored"},{"id":"bad","title":null}]""", "");
        Assert.Single((await Poll()).Errors);
        Assert.Single(await Items());
        Assert.Equal(cursor, (await Source()).Cursor);
        Assert.Equal(CheckStatus.Fail, (await Doctor()).Status);
        runner.Result = new(19, "[]", "secret");
        Assert.Single((await Poll()).Errors);
        Assert.Equal(cursor, (await Source()).Cursor);
        runner.Result = new(0, """[{"id":"disk","title":"Still active"}]""", "");
        Assert.Equal(0, (await Poll()).NewItems);
        Assert.Null((await Source()).LastError);
        Assert.Equal(CheckStatus.Ok, (await Doctor()).Status);
        runner.Result = new(0, "[]", "");
        Assert.Equal(0, (await Poll()).NewItems);
        runner.Result = new(0, """[{"id":"disk","title":"Recurrence"}]""", "");
        Assert.Equal(1, (await Poll()).NewItems);
        var recurrence = (await Items())[1];
        Assert.Equal("smoke/disk/2", recurrence.ExternalId);
        (await client.PostAsJsonAsync(Routes.InboundAction(recurrence.Id, "dismiss"), new DismissInboundRequest("Observed"))).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Poll()).NewItems);
        Assert.Equal("dismissed", (await Items())[1].Status);
        var events = (await client.GetFromJsonAsync(Routes.Events + "?limit=200", MuthurJsonContext.Default.IReadOnlyListEventDto))!;
        Assert.Single(events, e => e.Type == "task.added");
        Assert.Single(events, e => e.Type == "ingest.failing");
        Assert.Single(events, e => e.Type == "ingest.recovered");
        Assert.Equal(7, events.Count(e => e.Type == "census.completed"));
        Assert.Contains(events, e => e.Type == "census.completed" && e.Payload.GetProperty("findings").GetInt32() == 0 && e.Payload.GetProperty("newItems").GetInt32() == 0);
        Assert.Empty(hub.Validators.Started);
        Assert.Empty(hub.Orchestrators.Started);
    }

    [Fact]
    public async Task Separate_sources_have_independent_cursors_and_namespaces()
    {
        var options = Options();
        options.CensusChecks["other"] = options.CensusChecks["smoke"];
        var runner = new CensusFakeRunner { Result = new(0, """[{"id":"a","title":"a"}]""", "") };
        var source = new CensusSource(options, runner);
        var first = await source.FetchAsync("smoke", null);
        Assert.Equal("smoke/a/1", Assert.Single(first.Items).ExternalId);
        Assert.Equal("other/a/1", Assert.Single((await source.FetchAsync("other", null)).Items).ExternalId);
    }
}

internal sealed class CensusFakeRunner : ICensusCommandRunner
{
    public ProcessResult Result { get; set; } = new(0, "[]", "");
    public Exception? Error { get; set; }
    public int Calls { get; private set; }
    public (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout)? LastCall { get; private set; }

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken ct = default)
    {
        Calls++;
        LastCall = (fileName, arguments, workingDirectory, timeout);
        return Error is null ? Task.FromResult(Result) : Task.FromException<ProcessResult>(Error);
    }
}
