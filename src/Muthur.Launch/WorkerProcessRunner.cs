using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Muthur.Launch;

public enum WorkerCleanup { NotStarted, ExitedAndTreeEmpty, CleanupUncertain }
public sealed record WorkerProcessResult(ProcessResult Result, bool Started, WorkerCleanup Cleanup);

public interface IWorkerProcessRunner
{
    bool Supported { get; }
    Task<WorkerProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>Windows workers start suspended and cannot execute until assigned to a non-breakaway job.</summary>
public sealed class WorkerProcessRunner : IWorkerProcessRunner
{
    public bool Supported => OperatingSystem.IsWindows();

    public async Task<WorkerProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Supported) return new(new(127, "", "Full-worker process containment is unsupported on this platform."), false, WorkerCleanup.NotStarted);
        if (ct.IsCancellationRequested) return new(new(130, "", "Cancelled before process creation."), false, WorkerCleanup.NotStarted);
        if (!Path.IsPathRooted(fileName))
        {
            var resolved = CapabilityExecutable.Resolve(fileName, ExecutableResolver.Resolve);
            if (resolved is null) return new(new(127, "", "Executable could not be resolved."), false, WorkerCleanup.NotStarted);
            fileName = resolved.Value.FileName;
            arguments = [.. resolved.Value.Prefix, .. arguments];
        }
        nint job = 0;
        nint attributes = 0, handles = 0;
        var attributesInitialized = false;
        Native.ProcessInformation child = default;
        var assigned = false;
        var started = false;
        var cleanup = WorkerCleanup.NotStarted;
        var result = new ProcessResult(127, "", "Process not started.");
        Task<string>? output = null, error = null;
        Task? input = null;
        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stdinPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit) budget.CancelAfter(limit);
        try
        {
            job = Native.CreateJobObjectW(0, null);
            if (job == 0) throw new Win32Exception();
            var limits = new Native.ExtendedLimits { Basic = new() { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE, no breakaway
            if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>())) throw new Win32Exception();
            var info = new Native.StartupInfoEx { Startup = new Native.StartupInfo
            {
                Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100,
                Input = stdinPipe.ClientSafePipeHandle.DangerousGetHandle(),
                Output = stdout.ClientSafePipeHandle.DangerousGetHandle(), Error = stderr.ClientSafePipeHandle.DangerousGetHandle(),
            } };
            nuint attributeSize = 0;
            Native.InitializeProcThreadAttributeList(0, 1, 0, ref attributeSize);
            attributes = Marshal.AllocHGlobal((nint)attributeSize);
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize)) throw new Win32Exception();
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(nint.Size * 3);
            Marshal.WriteIntPtr(handles, 0, info.Startup.Input);
            Marshal.WriteIntPtr(handles, nint.Size, info.Startup.Output);
            Marshal.WriteIntPtr(handles, nint.Size * 2, info.Startup.Error);
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(nint.Size * 3), 0, 0)) throw new Win32Exception();
            info.Attributes = attributes;
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables[(string)entry.Key] = (string)entry.Value!;
            foreach (var name in scrubEnvironment ?? []) variables.Remove(name);
            foreach (var pair in environment ?? new Dictionary<string, string>()) variables[pair.Key] = pair.Value;
            // Credentials are never restored by an environment override.
            variables.Remove("MUTHUR_AGENT"); variables.Remove("MUTHUR_TOKEN");
            var block = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(p => p.Key + "=" + p.Value)) + "\0\0");
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                var command = new StringBuilder(string.Join(' ', new[] { fileName }.Concat(arguments).Select(Quote)));
                if (!Native.CreateProcessW(fileName, command, 0, 0, true, 0x08000004 | 0x400 | 0x80000, block,
                    workingDirectory, ref info, out child)) throw new Win32Exception();
                started = true;
                cleanup = WorkerCleanup.CleanupUncertain;
            }
            finally { Marshal.FreeHGlobal(block); }
            stdout.DisposeLocalCopyOfClientHandle(); stderr.DisposeLocalCopyOfClientHandle(); stdinPipe.DisposeLocalCopyOfClientHandle();
            if (!Native.AssignProcessToJobObject(job, child.Process)) throw new Win32Exception();
            assigned = true;
            if (Native.ResumeThread(child.Thread) == uint.MaxValue) throw new Win32Exception();
            output = ReadAsync(stdout);
            error = ReadAsync(stderr);
            input = WriteAsync(stdinPipe, stdin, budget.Token);
            await input.WaitAsync(budget.Token);
            while (Native.WaitForSingleObject(child.Process, 0) == 0x102)
                await Task.Delay(20, budget.Token);
            if (!Native.GetExitCodeProcess(child.Process, out var exitCode)) throw new Win32Exception();
            // The root may exit while descendants still hold output pipes. Terminate the whole job before draining.
            if (!Native.TerminateJobObject(job, exitCode)) throw new Win32Exception();
            using var drainBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitEmptyAsync(job, drainBudget.Token);
            cleanup = WorkerCleanup.ExitedAndTreeEmpty;
            result = new((int)exitCode, await output.WaitAsync(drainBudget.Token), await error.WaitAsync(drainBudget.Token));
        }
        catch (OperationCanceledException)
        { result = new(ct.IsCancellationRequested ? 130 : 124, "", ct.IsCancellationRequested ? "Worker cancelled." : "Worker timed out."); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        { result = new(127, "", ex.Message); }
        finally
        {
            if (started)
            {
                using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (assigned)
                    {
                        if (!Native.TerminateJobObject(job, 130)) throw new Win32Exception();
                        await WaitEmptyAsync(job, cleanupBudget.Token);
                        cleanup = WorkerCleanup.ExitedAndTreeEmpty;
                    }
                    else
                    {
                        // Still suspended: no user code or children could have executed. Assignment failure
                        // nevertheless retains admission, as the intended containment was never established.
                        Native.TerminateProcess(child.Process, 130);
                    }
                    if (input is not null) { try { await input.WaitAsync(cleanupBudget.Token); } catch (IOException) { } catch (OperationCanceledException) when (!cleanupBudget.IsCancellationRequested) { } }
                    if (output is not null) await output.WaitAsync(cleanupBudget.Token);
                    if (error is not null) await error.WaitAsync(cleanupBudget.Token);
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or OperationCanceledException)
                { cleanup = WorkerCleanup.CleanupUncertain; result = result with { StdErr = result.StdErr + " Cleanup: " + ex.Message }; }
            }
            if (child.Thread != 0) Native.CloseHandle(child.Thread);
            if (child.Process != 0) Native.CloseHandle(child.Process);
            if (job != 0) Native.CloseHandle(job);
            if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (handles != 0) Marshal.FreeHGlobal(handles);
        }
        return new(result, started, cleanup);
    }

    private static async Task WriteAsync(Stream stream, string? text, CancellationToken ct)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        if (text is not null) await writer.WriteAsync(text.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    private static async Task<string> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task WaitEmptyAsync(nint job, CancellationToken ct)
    {
        while (true)
        {
            if (!Native.QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Native.Accounting>(), 0)) throw new Win32Exception();
            if (accounting.ActiveProcesses == 0) return;
            await Task.Delay(20, ct);
        }
    }

    internal static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfo
        {
            public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
            public ushort Show, ReservedSize; public nint ReservedData, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
        { public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
        { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)] internal struct Accounting
        { public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateJobObjectW(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(nint job, int type, ref ExtendedLimits value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(nint job, int type, out Accounting value, uint size, nint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(nint job, nint process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(nint thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(nint job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(nint process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(nint process, out uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    }
}
