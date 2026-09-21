using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Muthur.Launch;

namespace Muthur.Launch.Tests;

public sealed class VerificationContainmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "muthur-cli-tests", Guid.NewGuid().ToString("N"));
    public VerificationContainmentTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("registered")]
    [InlineData("suspended")]
    [InlineData("ready")]
    public async Task Hard_interruption_at_observable_boundaries_kills_owned_descendants_and_recovers(string boundary)
    {
        using var fixture = new Fixture(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await fixture.Connect(deadline.Token);
        var message = await fixture.Read(deadline.Token);
        Assert.StartsWith("registered:", message);
        if (boundary != "registered")
        {
            await fixture.Write("go");
            message = await fixture.Read(deadline.Token);
            Assert.StartsWith("suspended:", message);
        }
        if (boundary == "ready")
        {
            await fixture.Write("go");
            message = await fixture.Read(deadline.Token);
            Assert.StartsWith("ready:", message);
        }
        using var child = boundary == "registered" ? null : Process.GetProcessById(int.Parse(message.Split(':')[1]));
        fixture.Process.Kill(); // Deliberately kill only the supervisor, never its tree.
        await fixture.Process.WaitForExitAsync(deadline.Token);
        if (child is not null) await child.WaitForExitAsync(deadline.Token);
        var runner = new VerificationRunner(new ProcessRunner());
        Assert.Equal("interrupted", (await runner.CleanupAsync(fixture.Output, deadline.Token)).Status);
        Assert.Equal("interrupted", (await runner.CleanupAsync(fixture.Output, deadline.Token)).Status);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Output, "scratch-*"));
    }

    [Fact]
    public async Task Concurrent_jobs_isolate_cancellation_after_intermediate_parent_exit()
    {
        using var first = new Fixture(root);
        using var second = new Fixture(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ready = await Task.WhenAll(first.Ready(deadline.Token), second.Ready(deadline.Token));
        using var firstChild = Process.GetProcessById(int.Parse(ready[0].Split(':')[1]));
        using var secondChild = Process.GetProcessById(int.Parse(ready[1].Split(':')[1]));
        await first.Write("cancel");
        await first.Process.WaitForExitAsync(deadline.Token);
        await firstChild.WaitForExitAsync(deadline.Token);
        Assert.Equal(130, first.Process.ExitCode);
        Assert.False(secondChild.HasExited);
        await second.Write("cancel");
        await second.Process.WaitForExitAsync(deadline.Token);
        await secondChild.WaitForExitAsync(deadline.Token);
        Assert.Equal(130, second.Process.ExitCode);
        foreach (var fixture in new[] { first, second })
        {
            var evidence = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(fixture.Output, "evidence.json")), VerificationJsonContext.Default.VerificationEvidence)!;
            Assert.True(evidence.CleanupSucceeded);
            Assert.Equal("cancelled", evidence.Status);
            Assert.False(evidence.TestsStarted);
            Assert.Empty(Directory.EnumerateDirectories(fixture.Output, "scratch-*"));
        }
    }

    [Fact]
    public async Task Contained_launch_failure_never_reports_started()
    {
        using var job = new VerificationJob(Guid.NewGuid());
        var result = await new ProcessRunner().RunAsync(Path.Combine(root, "absent.exe"), [], root);
        Assert.False(result.Started);
        Assert.Equal(127, result.ExitCode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        public Process Process { get; }
        public string Output { get; }
        public Fixture(string root)
        {
            var id = Guid.NewGuid().ToString("N");
            Output = Path.Combine(root, id);
            var pipeName = "muthur-verification-test-" + id;
            pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            reader = new(pipe, leaveOpen: true);
            writer = new(pipe, leaveOpen: true);
            var script = Path.Combine(root, id + ".ps1");
            File.WriteAllText(script, """
                param($Contracts, $Launch, $Output, $Pipe)
                $ErrorActionPreference = 'Stop'
                [Reflection.Assembly]::LoadFrom($Contracts) | Out-Null
                [Reflection.Assembly]::LoadFrom($Launch) | Out-Null
                $code = [Muthur.Launch.VerificationContainmentFixture]::RunAsync($Output, $Pipe, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
                exit $code
                """);
            var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-File", script, typeof(Muthur.Contracts.MuthurEnvironment).Assembly.Location,
                typeof(VerificationJob).Assembly.Location, Output, pipeName }) info.ArgumentList.Add(argument);
            Process = Process.Start(info)!;
        }
        public async Task Connect(CancellationToken ct)
        {
            await pipe.WaitForConnectionAsync(ct);
            writer.AutoFlush = true;
        }
        public async Task<string> Read(CancellationToken ct) => await reader.ReadLineAsync(ct) ?? throw new IOException("Fixture barrier closed.");
        public Task Write(string command) => writer.WriteLineAsync(command);
        public async Task<string> Ready(CancellationToken ct)
        {
            await Connect(ct);
            Assert.StartsWith("registered:", await Read(ct));
            await Write("go");
            Assert.StartsWith("suspended:", await Read(ct));
            await Write("go");
            var ready = await Read(ct);
            Assert.StartsWith("ready:", ready);
            return ready;
        }
        public void Dispose()
        {
            if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(10000); }
            Process.Dispose(); writer.Dispose(); reader.Dispose(); pipe.Dispose();
        }
    }
}
