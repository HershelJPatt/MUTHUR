using System.Diagnostics;
using System.Text;

namespace Muthur.Launch.Tests;

public sealed class WorkerProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muthur-tests", "worker-tree-" + Guid.NewGuid().ToString("N"));
    public WorkerProcessRunnerTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        // Windows can finish filesystem teardown just after the job becomes empty. Wait on deletion itself.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return true; }
            catch (IOException) { return false; }
        }, TimeSpan.FromSeconds(10)), "Synthetic fixture directory remained locked after process cleanup: " + _root);
    }

    [Fact]
    public async Task Native_start_failure_proves_no_process_was_created()
    {
        var result = await new WorkerProcessRunner().RunAsync(Path.Combine(_root, "missing.exe"), [], _root);
        Assert.False(result.Started); Assert.Equal(WorkerCleanup.NotStarted, result.Cleanup);
    }

    [Fact]
    public async Task Native_job_redirects_input_output_and_scrubs_credentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = ExecutableResolver.Resolve("pwsh")!.Value.FileName;
        var result = await new WorkerProcessRunner().RunAsync(shell,
            ["-NoProfile", "-Command", "[Console]::WriteLine([Console]::ReadLine()); [Console]::Error.WriteLine('stderr'); if ($env:MUTHUR_TOKEN -or $env:MUTHUR_AGENT) { exit 9 }"],
            _root, "fixture input\n", TimeSpan.FromSeconds(30), environment: new Dictionary<string, string> { ["MUTHUR_TOKEN"] = "must-not-leak", ["MUTHUR_AGENT"] = "must-not-leak" });
        Assert.True(result.Started); Assert.Equal(WorkerCleanup.ExitedAndTreeEmpty, result.Cleanup);
        Assert.Equal(0, result.Result.ExitCode); Assert.Contains("fixture input", result.Result.StdOut); Assert.Contains("stderr", result.Result.StdErr);
    }

    [Fact]
    public async Task Cancellation_during_blocked_stdin_still_empties_the_job()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "Local\\muthur-stdin-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var cancel = new CancellationTokenSource();
        var shell = ExecutableResolver.Resolve("pwsh")!.Value.FileName;
        var run = new WorkerProcessRunner().RunAsync(shell,
            ["-NoProfile", "-Command", "$e=[Threading.EventWaitHandle]::OpenExisting($env:T121_EVENT); $e.Set() | Out-Null; [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null"],
            _root, new string('x', 2_000_000), TimeSpan.FromSeconds(40), cancel.Token,
            environment: new Dictionary<string, string> { ["T121_EVENT"] = name });
        try
        {
            Assert.True(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(25))));
            cancel.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(130, result.Result.ExitCode);
            Assert.Equal(WorkerCleanup.ExitedAndTreeEmpty, result.Cleanup);
        }
        finally { cancel.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_exit_or_cancellation_cannot_leave_children_or_grandchildren(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = ExecutableResolver.Resolve("pwsh")!.Value.FileName;
        var eventName = "Local\\muthur-worker-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var cancellation = new CancellationTokenSource();
        var pids = Path.Combine(_root, "pids.txt");
        var grandchildFile = Path.Combine(_root, "grandchild.ps1");
        var childFile = Path.Combine(_root, "child.ps1");
        var rootFile = Path.Combine(_root, "root.ps1");
        // The grandchild signals only after all three identities are recorded. No timing sleep synchronizes this test.
        File.WriteAllText(grandchildFile, "$PID | Add-Content -LiteralPath $env:T121_PIDS; $ready=[Threading.EventWaitHandle]::OpenExisting($env:T121_EVENT); $ready.Set() | Out-Null; [Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null");
        File.WriteAllText(childFile, "$PID | Add-Content -LiteralPath $env:T121_PIDS; & $env:T121_SHELL -NoProfile -File $env:T121_GRANDCHILD");
        File.WriteAllText(rootFile, "$PID | Set-Content -LiteralPath $env:T121_PIDS; $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('& $env:T121_CHILD')); $child=Start-Process $env:T121_SHELL -ArgumentList @('-NoProfile','-EncodedCommand',$encoded) -WindowStyle Hidden -PassThru; $ready=[Threading.EventWaitHandle]::OpenExisting($env:T121_EVENT); if (-not $ready.WaitOne(20000)) { exit 8 }; " +
            (cancel ? "[Threading.ManualResetEvent]::new($false).WaitOne() | Out-Null" : "exit 0"));
        var variables = new Dictionary<string, string> { ["T121_PIDS"] = pids, ["T121_EVENT"] = eventName, ["T121_SHELL"] = shell,
            ["T121_CHILD"] = childFile, ["T121_GRANDCHILD"] = grandchildFile };
        var run = new WorkerProcessRunner().RunAsync(shell, ["-NoProfile", "-File", rootFile], _root,
            timeout: TimeSpan.FromSeconds(40), ct: cancellation.Token, environment: variables);
        try
        {
            Assert.True(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(25))));
            if (cancel) cancellation.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(WorkerCleanup.ExitedAndTreeEmpty, result.Cleanup);
            Assert.Equal(cancel ? 130 : 0, result.Result.ExitCode);
            var ids = File.ReadAllLines(pids).Select(int.Parse).ToArray();
            Assert.Equal(3, ids.Length);
            foreach (var id in ids)
            {
                try { using var process = Process.GetProcessById(id); Assert.True(process.HasExited); }
                catch (ArgumentException) { }
            }
        }
        finally { cancellation.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(30)); }
    }
}
