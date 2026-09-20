using System.Diagnostics;
using System.Text.Json;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

[CollectionDefinition("Census process environment", DisableParallelization = true)]
public sealed class CensusProcessEnvironmentCollection;

[Collection("Census process environment")]
public sealed class CensusCommandRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("muthur-census-runner-").FullName;
    private readonly CensusCommandRunner _runner = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [CensusNonWindowsFact]
    public async Task Unsupported_platform_is_rejected_before_launching_a_fixture()
    {
        var marker = Path.Combine(_root, "launched");
        var result = await _runner.RunAsync("/bin/sh", ["-c", "touch \"$1\"", "fixture", marker], _root, Eventually.Budget);
        Assert.Equal(127, result.ExitCode);
        Assert.Equal("Census execution requires Windows.", result.StdErr);
        Assert.Empty(result.StdOut);
        Assert.False(File.Exists(marker));
    }

    private Task<ProcessResult> Run(string script, TimeSpan? timeout = null, CancellationToken ct = default, params string[] arguments)
    {
        var file = Path.Combine(_root, "script with spaces " + Guid.NewGuid() + ".ps1");
        File.WriteAllText(file, script);
        return _runner.RunAsync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", file, .. arguments],
            _root, timeout ?? Eventually.Budget, ct);
    }

    [CensusWindowsFact]
    public async Task Arguments_are_literal_streams_are_utf8_stdin_is_closed_and_secrets_are_scrubbed()
    {
        string[] secrets = ["MUTHUR_AGENT", "MUTHUR_TOKEN", "Muthur__DiscordBotToken"];
        var saved = secrets.ToDictionary(s => s, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in secrets) Environment.SetEnvironmentVariable(name, "secret");
            string[] arguments = ["space separated", "quote\"literal", "$env:MUTHUR_TOKEN", "; exit 17", "é漢字", "", @"C:\trailing\", @"C:\space separated\", "\"C:\\quoted path\\file.txt\"", "slash\\\"quote"];
            var result = await Run("""
                [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                $value = @{
                    arguments = @($args)
                    cwd = [Environment]::CurrentDirectory
                    eof = [Console]::In.Read() -eq -1
                    secrets = @($env:MUTHUR_AGENT, $env:MUTHUR_TOKEN, $env:Muthur__DiscordBotToken)
                }
                [Console]::Out.Write(($value | ConvertTo-Json -Compress))
                [Console]::Error.Write('é漢字')
                """, arguments: arguments);
            Assert.Equal(0, result.ExitCode);
            using var doc = JsonDocument.Parse(result.StdOut);
            Assert.Equal(arguments, doc.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(_root, doc.RootElement.GetProperty("cwd").GetString());
            Assert.True(doc.RootElement.GetProperty("eof").GetBoolean());
            Assert.All(doc.RootElement.GetProperty("secrets").EnumerateArray(), s => Assert.Equal(JsonValueKind.Null, s.ValueKind));
            Assert.Equal("é漢字", result.StdErr);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [CensusWindowsFact]
    public async Task Startup_failure_has_a_fixed_code_and_message()
    {
        var result = await _runner.RunAsync(Path.Combine(_root, "missing-secret-command"), ["secret"], _root, Eventually.Budget);
        Assert.Equal(127, result.ExitCode);
        Assert.DoesNotContain("secret", result.Message);
    }

    [CensusWindowsTheory]
    [InlineData("Out")]
    [InlineData("Error")]
    public async Task Each_stream_is_capped_while_reading_and_the_child_is_reaped(string stream)
    {
        var pidFile = Path.Combine(_root, "pid");
        var script = """
            [IO.File]::WriteAllText($args[0], [string]$PID)
            $chunk = 'x' * 4096
            while ($true) { [Console]::STREAM.Write($chunk) }
            """.Replace("STREAM", stream, StringComparison.Ordinal);
        var result = await Eventually.CompletesAsync(Run(script, arguments: [pidFile]), "output cap did not stop the child");
        Assert.Equal(125, result.ExitCode);
        Assert.True(result.StdOut.Length <= CensusCommandRunner.OutputLimit);
        Assert.True(result.StdErr.Length <= CensusCommandRunner.OutputLimit);
        AssertStopped(int.Parse(File.ReadAllText(pidFile)));
    }

    [CensusWindowsFact]
    public async Task Exactly_the_stream_limit_is_accepted()
    {
        var result = await Run("[Console]::Out.Write(('x' * 1048576)); [Console]::Error.Write(('y' * 1048576))");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CensusCommandRunner.OutputLimit, result.StdOut.Length);
        Assert.Equal(CensusCommandRunner.OutputLimit, result.StdErr.Length);
    }

    [CensusWindowsFact]
    public async Task Timeout_covers_execution_and_reaps_the_child()
    {
        var pidFile = Path.Combine(_root, "pid");
        var result = await Eventually.CompletesAsync(Run("""
            [IO.File]::WriteAllText($args[0], [string]$PID)
            [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null
            """, TimeSpan.FromSeconds(3), arguments: [pidFile]), "timeout did not stop the child");
        Assert.Equal(124, result.ExitCode);
        AssertStopped(int.Parse(File.ReadAllText(pidFile)));
    }

    [CensusWindowsFact]
    public async Task Cancellation_reaps_the_child_before_rethrowing()
    {
        var pidFile = Path.Combine(_root, "pid");
        using var cancellation = new CancellationTokenSource();
        var running = Run("""
            [IO.File]::WriteAllText($args[0], [string]$PID)
            [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null
            """, ct: cancellation.Token, arguments: [pidFile]);
        try
        {
            await Eventually.TrueAsync(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, "the child never became ready");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Eventually.Budget));
            AssertStopped(int.Parse(File.ReadAllText(pidFile)));
        }
        finally
        {
            cancellation.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
        }
    }

    [CensusWindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_also_covers_descendants_holding_pipes_after_parent_exit(bool parentExits)
    {
        var childFile = Path.Combine(_root, "child.ps1");
        File.WriteAllText(childFile, """
            [IO.File]::WriteAllText($args[0], [string]$PID)
            [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null
            """);
        var pidFile = Path.Combine(_root, "child-pid");
        var result = await Eventually.CompletesAsync(Run("""
            $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            foreach ($arg in @('-NoProfile', '-File', $args[0], $args[1])) { $start.ArgumentList.Add($arg) }
            $child = [Diagnostics.Process]::Start($start)
            if ($args[2] -eq 'exit') { exit 0 }
            $child.WaitForExit()
            """, TimeSpan.FromSeconds(5), arguments: [childFile, pidFile, parentExits ? "exit" : "wait"]), "descendant kept the runner alive beyond its timeout");
        Assert.Equal(124, result.ExitCode);
        AssertStopped(int.Parse(File.ReadAllText(pidFile)));
    }

    [CensusWindowsFact]
    public async Task Nonzero_exit_reaps_descendants_even_when_the_parent_has_exited()
    {
        var childFile = Path.Combine(_root, "child.ps1");
        File.WriteAllText(childFile, "[Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null");
        var pidFile = Path.Combine(_root, "child-pid");
        var result = await Eventually.CompletesAsync(Run("""
            $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            foreach ($arg in @('-NoProfile', '-File', $args[0])) { $start.ArgumentList.Add($arg) }
            $child = [Diagnostics.Process]::Start($start)
            [IO.File]::WriteAllText($args[1], [string]$child.Id)
            exit 17
            """, arguments: [childFile, pidFile]), "nonzero exit left a descendant holding pipes");
        Assert.Equal(17, result.ExitCode);
        AssertStopped(int.Parse(File.ReadAllText(pidFile)));
    }

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"Census child {pid} remains alive.");
        }
        catch (ArgumentException) { }
    }
}

public sealed class CensusWindowsFactAttribute : FactAttribute
{
    public CensusWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Census execution requires Windows.";
    }
}

public sealed class CensusWindowsTheoryAttribute : TheoryAttribute
{
    public CensusWindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Census execution requires Windows.";
    }
}

public sealed class CensusNonWindowsFactAttribute : FactAttribute
{
    public CensusNonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Exercises rejection on non-Windows platforms.";
    }
}
