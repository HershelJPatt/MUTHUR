using System.ComponentModel;
using System.Text;
using Muthur.Launch;

namespace Muthur.Server.Services;

public interface ICensusCommandRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken ct = default);
}

public sealed class CensusCommandRunner : ICensusCommandRunner
{
    internal const int OutputLimit = 1_048_576;
    internal const string PlatformError = "Census execution requires Windows.";

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return new ProcessResult(127, "", PlatformError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        CensusWindowsProcess child;
        try { child = CensusWindowsProcess.Start(fileName, arguments, workingDirectory); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(127, "", "Census startup failed.");
        }
        using var lifetime = child;
        var exceeded = 0;
        async Task<string> ReadAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0)
                {
                    if (count > OutputLimit - text.Length)
                    {
                        Interlocked.Exchange(ref exceeded, 1);
                        deadline.Cancel();
                        break;
                    }
                    text.Append(buffer, 0, count);
                }
                return text.ToString();
            }
            catch
            {
                deadline.Cancel();
                throw;
            }
        }

        var stdout = ReadAsync(child.StandardOutput);
        var stderr = ReadAsync(child.StandardError);
        ProcessResult result;
        try
        {
            // Descendants can retain redirected pipes after the immediate child exits.
            await child.Process.WaitForExitAsync(deadline.Token);
            if (child.Process.ExitCode != 0) await child.StopAsync();
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            result = new ProcessResult(child.Process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Win32Exception or InvalidOperationException)
        {
            result = exceeded != 0
                ? new ProcessResult(125, "", "Census output limit exceeded.")
                : new ProcessResult(ex is OperationCanceledException ? 124 : 126, "", "Census execution failed.");
        }
        finally
        {
            deadline.Cancel();
            // Always attempt both tree cleanup and stream cleanup, even if either operation fails.
            try
            {
                if (!await child.StopAsync()) result = new ProcessResult(126, "", "Census cleanup failed.");
            }
            finally
            {
                try { await Task.WhenAll(stdout, stderr); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
