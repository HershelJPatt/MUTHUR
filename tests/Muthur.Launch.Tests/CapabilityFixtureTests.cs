namespace Muthur.Launch.Tests;

public sealed class CapabilityFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muthur-tests", "capability-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly CapabilityProcessRunner _runner = new();

    public CapabilityFixtureTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Exact_sdk_commands_assert_build_output_before_emitting_test_output()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var project = Path.Combine(_root, "fixture.proj");
        File.WriteAllText(project, CapabilityFixture.Project(nonce));
        async Task<ProcessResult> Run(string command) => await _runner.RunAsync("dotnet", [command, project, "--no-restore"], _root,
            timeout: TimeSpan.FromSeconds(30));
        var missing = await Run("test");
        Assert.False(missing.Ok);
        Assert.Contains("Build nonce output is missing", missing.Message);
        Assert.False(File.Exists(Path.Combine(_root, "test.txt")));
        var build = await Run("build");
        Assert.True(build.Ok, build.Message);
        Assert.Equal(nonce, File.ReadAllText(Path.Combine(_root, "build.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(_root, "test.txt")));
        File.WriteAllText(Path.Combine(_root, "build.txt"), "wrong");
        var mismatch = await Run("test");
        Assert.False(mismatch.Ok);
        Assert.Contains("Build nonce output does not match", mismatch.Message);
        Assert.False(File.Exists(Path.Combine(_root, "test.txt")));
        Assert.True((await Run("build")).Ok);
        var test = await Run("test");
        Assert.True(test.Ok, test.Message);
        Assert.Equal(nonce, File.ReadAllText(Path.Combine(_root, "test.txt")).Trim());
    }

    [Theory]
    [InlineData(false, 65537, 0)]
    [InlineData(false, 1048576, 0)]
    [InlineData(false, 1048577, 125)]
    [InlineData(true, 1048577, 125)]
    public async Task Output_overflow_is_explicit_and_drained(bool stderr, int size, int exit)
    {
        var stream = stderr ? "Error" : "Out";
        var result = await _runner.RunAsync("pwsh", ["-NoProfile", "-Command", $"[Console]::{stream}.Write(('x' * {size}))"],
            _root, timeout: TimeSpan.FromSeconds(30));
        Assert.Equal(exit, result.ExitCode);
        if (exit == 0) Assert.Equal(size, result.StdOut.Length);
        else
        {
            Assert.Empty(result.StdOut);
            Assert.Contains("exceeded", result.StdErr);
        }
    }

    [Fact]
    public void Prompt_keeps_native_command_and_receipt_in_one_invocation()
    {
        var steps = CapabilityFixture.Steps(_root, "nonce");
        var prompt = CapabilityFixture.Prompt(steps);
        Assert.Contains("ONE shell-tool invocation", prompt);
        Assert.Contains("SAME invocation", prompt);
        Assert.Contains("LASTEXITCODE", prompt);
        Assert.StartsWith("dotnet build ", Assert.Single(steps, s => s.Key == "build").Command);
        Assert.EndsWith(" --no-restore", Assert.Single(steps, s => s.Key == "test").Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_owned_stdin_or_wait_confirms_process_exit(bool blockedStdin)
    {
        var ready = Path.Combine(_root, "ready.txt");
        var script = Path.Combine(_root, "wait.ps1");
        File.WriteAllText(script, $"[IO.File]::WriteAllText('{ready.Replace("'", "''")}', [string]$PID)\nwhile ($true) {{ [Threading.Thread]::Yield() | Out-Null }}");
        using var cancel = new CancellationTokenSource();
        var run = _runner.RunAsync("pwsh", ["-NoProfile", "-File", script], _root,
            stdin: blockedStdin ? new string('x', 8 * 1024 * 1024) : null, timeout: TimeSpan.FromSeconds(30), ct: cancel.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!File.Exists(ready))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (run.IsCompleted) await run;
                await Task.Yield();
            }
        }
        finally { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var pid = int.Parse(File.ReadAllText(ready));
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException) { /* The OS has already removed the confirmed-exited process. */ }
    }
}
