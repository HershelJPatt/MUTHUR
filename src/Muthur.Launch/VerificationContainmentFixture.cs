using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Muthur.Launch;

// A bounded, hub-free installed diagnostic. Its evidence can never report recipe success.
public static class VerificationContainmentFixture
{
    public static async Task<int> RunAsync(string output, string barrier, CancellationToken ct = default)
    {
        output = VerificationFiles.PlainPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Fixture output must be new.");
        var runId = Guid.NewGuid();
        var scratch = Path.Combine(output, "scratch-" + runId.ToString("N"));
        Directory.CreateDirectory(scratch);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        using var control = new NamedPipeClientStream(".", barrier, PipeDirection.InOut, PipeOptions.Asynchronous);
        await control.ConnectAsync(budget.Token);
        using var reader = new StreamReader(control);
        using var writer = new StreamWriter(control) { AutoFlush = true };
        using var current = Process.GetCurrentProcess();
        var owner = new VerificationOwnership
        {
            RunId = runId, Output = output, Repository = output, Scratch = scratch, Checkout = Path.Combine(scratch, "checkout"),
            Commit = new string('0', 40), Runner = new(current.Id, current.StartTime.ToUniversalTime().Ticks, current.MainModule!.FileName),
            Url = "http://127.0.0.1:1", Containment = VerificationJob.Name(runId), CommandRunning = true,
        };
        var evidence = new VerificationEvidence
        {
            RunId = runId, Repository = output, GitDirectory = output, Commit = owner.Commit, Tree = owner.Commit,
            SpecPath = "containment-diagnostic", SpecSha256 = new string('0', 64), RecipeSha256 = new string('0', 64),
            Recipe = VerificationRecipes.Muthur, StartedUtc = DateTimeOffset.UtcNow, CacheStatus = "bypassed",
            Error = "Containment diagnostic only; no recipe tests or approval.",
        };
        void Save()
        {
            VerificationFiles.Atomic(Path.Combine(output, "ownership.json"), owner, VerificationJsonContext.Default.VerificationOwnership);
            VerificationFiles.Atomic(Path.Combine(output, "evidence.json"), evidence, VerificationJsonContext.Default.VerificationEvidence);
        }
        Save();
        using var job = new VerificationJob(runId)
        {
            BeforeResume = process =>
            {
                owner.ActiveCommand = new(process.Id, process.StartTime.ToUniversalTime().Ticks,
                    ExecutableResolver.Resolve("pwsh")!.Value.FileName);
                Save();
                writer.WriteLine("suspended:" + process.Id);
                if (reader.ReadLineAsync(budget.Token).AsTask().GetAwaiter().GetResult() != "go")
                    throw new IOException("Fixture launch barrier was not released.");
            },
        };
        var environment = VerificationEnvironment.Create(scratch, owner.Url);
        var childScript = Path.Combine(scratch, "child.ps1");
        var parentScript = Path.Combine(scratch, "parent.ps1");
        File.WriteAllText(childScript, """
            param($Pipe, $ParentPid)
            $pipeClient = [IO.Pipes.NamedPipeClientStream]::new('.', $Pipe, [IO.Pipes.PipeDirection]::InOut)
            $pipeClient.Connect(30000)
            $writer = [IO.StreamWriter]::new($pipeClient); $writer.AutoFlush = $true
            $writer.WriteLine("${PID}:$ParentPid")
            $reader = [IO.StreamReader]::new($pipeClient)
            $reader.ReadLine() | Out-Null
            """);
        File.WriteAllText(parentScript, """
            param($Pipe, $Child)
            $info = [Diagnostics.ProcessStartInfo]::new('pwsh')
            $info.UseShellExecute = $false; $info.CreateNoWindow = $true
            foreach ($argument in @('-NoProfile', '-File', $Child, $Pipe, $PID)) { $info.ArgumentList.Add([string]$argument) }
            $null = [Diagnostics.Process]::Start($info)
            """);
        var childPipeName = "muthur-fixture-" + runId.ToString("N");
        using var childPipe = new NamedPipeServerStream(childPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task<ProcessResult>? running = null;
        Process? parent = null;
        try
        {
            await writer.WriteLineAsync("registered:" + runId.ToString("N"));
            if (await reader.ReadLineAsync(budget.Token) != "go") throw new IOException("Fixture registration barrier was not released.");
            running = new ProcessRunner().RunObservedAsync("pwsh", ["-NoProfile", "-File", parentScript, childPipeName, childScript], scratch,
                ct: budget.Token, scrubEnvironment: VerificationEnvironment.Scrub(), environment: environment, started: process => parent = process);
            await childPipe.WaitForConnectionAsync(budget.Token);
            using var childReader = new StreamReader(childPipe, leaveOpen: true);
            var identities = await childReader.ReadLineAsync(budget.Token);
            await parent!.WaitForExitAsync(budget.Token);
            await writer.WriteLineAsync("ready:" + identities + ":" + runId.ToString("N"));
            if (await reader.ReadLineAsync(budget.Token) != "cancel") throw new IOException("Fixture expects cancellation.");
            budget.Cancel();
            try { await running; }
            catch (ProcessCancelledException) { }
            evidence.Status = "cancelled";
            return 130;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await job.StopAsync(cleanup.Token);
            if (running is not null)
            {
                budget.Cancel();
                try { await running.WaitAsync(cleanup.Token); }
                catch (OperationCanceledException) { }
            }
            VerificationFiles.DeleteOwned(scratch, output);
            owner.Cleaned = true; owner.CommandRunning = false; owner.ActiveCommand = null;
            evidence.CleanupSucceeded = true; evidence.EndedUtc = DateTimeOffset.UtcNow;
            if (evidence.Status == "running") evidence.Status = "failed";
            Save();
        }
    }
}
