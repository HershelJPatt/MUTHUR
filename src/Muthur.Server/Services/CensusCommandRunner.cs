using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var variable in new[] { "MUTHUR_AGENT", "MUTHUR_TOKEN", "Muthur__DiscordBotToken" })
            info.Environment.Remove(variable);

        using var process = new Process { StartInfo = info };
        ProcessJob? job = null;
        try
        {
            job = OperatingSystem.IsWindows() ? ProcessJob.Create() : null;
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            job?.Dispose();
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(127, "", "Census startup failed.");
        }
        using var jobLifetime = job;

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

        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        try
        {
            job?.Assign(process);
            process.StandardInput.Close();
            // WaitForExit alone is insufficient: descendants can hold the redirected pipes open.
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) Kill();
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Win32Exception or InvalidOperationException)
        {
            deadline.Cancel();
            Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception drainError) when (drainError is OperationCanceledException or IOException) { }
            ct.ThrowIfCancellationRequested();
            return exceeded != 0
                ? new ProcessResult(125, "", "Census output limit exceeded.")
                : new ProcessResult(ex is OperationCanceledException ? 124 : 126, "", "Census execution failed.");
        }

        void Kill()
        {
            job?.Terminate();
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    // A Windows job retains descendants even after the immediate child has exited. Closing it also
    // prevents a successful check from leaving background processes running beyond its poll.
    private sealed class ProcessJob(SafeFileHandle handle) : IDisposable
    {
        public static ProcessJob Create()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                handle.Dispose();
                throw new Win32Exception();
            }
            return new ProcessJob(handle);
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle) && !process.HasExited) throw new Win32Exception();
        }

        public void Terminate()
        {
            if (!TerminateJobObject(handle, 1)) throw new Win32Exception();
        }

        public void Dispose() => handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
