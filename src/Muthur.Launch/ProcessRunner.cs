using System.Diagnostics;
using System.Text;

namespace Muthur.Launch;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public bool Started { get; init; } = true;

    /// <summary>Whichever stream says something, for error messages.</summary>
    public string Message => (StdErr.Trim().Length > 0 ? StdErr : StdOut).Trim();
}

public sealed class ProcessCancelledException(ProcessResult result, CancellationToken token)
    : OperationCanceledException("Child process cancelled after bounded termination.", token)
{
    public ProcessResult Result { get; } = result;
}

public interface IProcessRunner
{
    /// <param name="scrubEnvironment">Variables removed from the child's environment (e.g. hub credentials a worker must not inherit).</param>
    /// <param name="environment">Variables set on the child (e.g. the identity a validator session acts as). Applied after scrubbing.</param>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
        IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>Runs a child process to completion, capturing output. Arguments are passed as a list, never through a shell.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        RunObservedAsync(fileName, arguments, workingDirectory, stdin, timeout, ct, scrubEnvironment, environment);

    public async Task<ProcessResult> RunObservedAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
        IReadOnlyDictionary<string, string>? environment = null, Action<Process>? started = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var variable in scrubEnvironment ?? []) info.Environment.Remove(variable);
        // Set after scrubbing: an identity the launcher grants outranks whatever this process inherited.
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) info.Environment[key] = value;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit) timeoutSource.CancelAfter(limit);
        ct.ThrowIfCancellationRequested();
        VerificationJob.Child? contained = null;
        Process process;
        try
        {
            if (VerificationJob.Current is { } job)
            {
                contained = job.Start(info);
                process = contained.Process;
            }
            else
            {
                process = new Process { StartInfo = info };
                try { process.Start(); }
                catch { process.Dispose(); throw; }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new ProcessResult(127, "", $"Could not start '{fileName}': {ex.Message}") { Started = false };
        }

        using var processLifetime = process;
        using var containedLifetime = contained;
        using var input = contained is null ? process.StandardInput : new StreamWriter(contained.Input, new UTF8Encoding(false));
        using var outputReader = contained is null ? process.StandardOutput : new StreamReader(contained.Output, Encoding.UTF8);
        using var errorReader = contained is null ? process.StandardError : new StreamReader(contained.Error, Encoding.UTF8);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdout = DrainAsync(outputReader, output);
        var stderr = DrainAsync(errorReader, error);
        try
        {
            started?.Invoke(process);
            if (stdin is not null) await input.WriteAsync(stdin.AsMemory(), timeoutSource.Token);
            input.Close();
            await process.WaitForExitAsync(timeoutSource.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (VerificationJob.Current is { } job) await job.StopAsync(cleanup.Token);
                else if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cleanup.Token);
                await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new IOException("Child process termination or output drain could not be confirmed.", ex);
            }
            var result = new ProcessResult(ct.IsCancellationRequested ? 130 : 124, output.ToString(),
                error + (ct.IsCancellationRequested ? "\nCommand cancelled." : $"\n'{fileName}' timed out after {timeout}."));
            if (ct.IsCancellationRequested) throw new ProcessCancelledException(result, ct);
            return result;
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (VerificationJob.Current is { } job) await job.StopAsync(cleanup.Token);
            else if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cleanup.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token);
            throw;
        }
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder destination)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0) destination.Append(buffer, 0, count);
    }
}
