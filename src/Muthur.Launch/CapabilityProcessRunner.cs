using System.Diagnostics;
using System.Text;

namespace Muthur.Launch;

/// <summary>A probe runner returns or throws only after confirming exit, or throws ProbeCleanupUncertainException.</summary>
public interface ICapabilityProcessRunner : IProcessRunner;

/// <summary>Owns the launched process through stdin, wait and output drain, including cancellation.</summary>
public sealed class CapabilityProcessRunner : ICapabilityProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit) budget.CancelAfter(limit);
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var variable in scrubEnvironment ?? []) info.Environment.Remove(variable);
        foreach (var pair in environment ?? new Dictionary<string, string>()) info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info };
        budget.Token.ThrowIfCancellationRequested();
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        { return new(127, "", "Probe executable could not be started."); }

        // Drains remain alive during termination; cancellation of the caller must not abandon owned pipes.
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        try
        {
            if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), budget.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(budget.Token);
            var output = await stdout.WaitAsync(budget.Token);
            var error = await stderr.WaitAsync(budget.Token);
            if (output.Overflow || error.Overflow)
                return new(125, "", "Capability process output exceeded the 1 MiB input limit; identity/evidence is incomplete.");
            return new(process.ExitCode, output.Text, error.Text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(124, "", "Probe command execution budget expired."); }
        finally
        {
            using var cleanup = new CancellationTokenSource(CapabilityBudget.Step);
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cleanup.Token);
                await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token);
            }
            catch (Exception ex)
            { throw new ProbeCleanupUncertainException("Probe process exit/output cleanup could not be confirmed; retain the reservation.", ex); }
        }
    }

    private static async Task<(string Text, bool Overflow)> DrainAsync(StreamReader reader)
    {
        const int maximum = 1_048_576;
        var output = new StringBuilder();
        var buffer = new char[4096];
        var overflow = false;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (read > maximum - output.Length) overflow = true;
            output.Append(buffer, 0, Math.Min(read, maximum - output.Length));
        }
        return (output.ToString(), overflow);
    }
}
