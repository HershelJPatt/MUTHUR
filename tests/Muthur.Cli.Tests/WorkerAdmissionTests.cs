using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

public sealed class WorkerAdmissionClientTests
{
    [Fact]
    public async Task Missing_task_and_unconfirmed_release_fail_before_process_or_http()
    {
        var root = new System.CommandLine.RootCommand();
        Globals.AddTo(root);
        Muthur.Cli.Commands.WorkerCommands.AddTo(root, new NeverRunner());
        Assert.Equal(2, await root.Parse("worker run --spec legacy.md").InvokeAsync());
        Assert.Equal(2, await root.Parse("worker release reservation").InvokeAsync());
    }

    private sealed class NeverRunner : Muthur.Launch.IProcessRunner
    {
        public Task<Muthur.Launch.ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null) =>
            throw new Xunit.Sdk.XunitException("Refusal must precede subprocesses.");
    }
    [Fact]
    public void Source_generated_contracts_preserve_the_execution_grant_and_request_identity()
    {
        var request = new WorkerAdmissionRequest("T-1", "worker", "fixture", "model", "account", Guid.NewGuid().ToString("N"), new string('a', 64));
        var json = JsonSerializer.Serialize(request, MuthurJsonContext.Default.WorkerAdmissionRequest);
        Assert.Equal(request, JsonSerializer.Deserialize(json, MuthurJsonContext.Default.WorkerAdmissionRequest));
        foreach (var granted in new[] { true, false })
        {
            var response = new WorkerAdmissionDto("reservation", request.Task, request.RunId, granted);
            var wire = JsonSerializer.Serialize(response, MuthurJsonContext.Default.WorkerAdmissionDto);
            Assert.Equal(response, JsonSerializer.Deserialize(wire, MuthurJsonContext.Default.WorkerAdmissionDto));
            Assert.Contains("\"mayExecute\":", wire);
        }
        var release = new WorkerReleaseRequest("reservation", true);
        Assert.Equal(release, JsonSerializer.Deserialize(JsonSerializer.Serialize(release,
            MuthurJsonContext.Default.WorkerReleaseRequest), MuthurJsonContext.Default.WorkerReleaseRequest));
    }

    [Fact]
    public async Task Unreachable_hub_is_a_refusal_and_release_failure_names_reservation()
    {
        Assert.Equal(HubIsolation.UnreachableUrl, MuthurEnvironment.Url);
        var adapter = new WorkerAdmissionClient(new HubClient(null, TimeSpan.FromSeconds(1)));
        var request = new WorkerAdmissionRequest("T-1", "worker", "fixture", "model", "account", Guid.NewGuid().ToString("N"), new string('a', 64));
        Assert.Contains("refused", (await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.AdmitAsync(request))).Message);
        Assert.Contains("reservation", (await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ReleaseAsync(new("reservation", true)))).Message);
    }
}

