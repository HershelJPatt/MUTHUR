using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Muthur.Launch;

// Windows assigns JOB_LIST during CreateProcess, before even suspended child code can run.
// The job handle is never inherited; losing the supervisor closes its last handle and kills descendants.
public sealed class VerificationJob : IDisposable
{
    private static readonly AsyncLocal<VerificationJob?> ambient = new();
    private readonly nint handle;
    private readonly VerificationJob? previous;
    public static VerificationJob? Current => ambient.Value;
    public Action<Process>? BeforeResume { get; init; }
    public static string Name(Guid runId) => @"Local\Muthur.Verification." + runId.ToString("N");

    public VerificationJob(Guid runId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Verification containment requires Windows Job Objects.");
        handle = CreateJobObjectW(0, Name(runId));
        if (handle == 0) throw Error("CreateJobObject");
        if (Marshal.GetLastPInvokeError() == 183)
        {
            CloseHandle(handle);
            throw new IOException("Verification job already exists.");
        }
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimits>());
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, 9, buffer, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Error("SetInformationJobObject");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { CloseHandle(handle); throw; }
        previous = ambient.Value;
        ambient.Value = this;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!TerminateJobObject(handle, 130)) throw Error("TerminateJobObject");
        await WaitEmpty(handle, ct);
    }

    public static async Task RecoverAsync(Guid runId, CancellationToken ct)
    {
        var job = OpenJobObjectW(0x0008 | 0x0004, false, Name(runId));
        if (job == 0)
        {
            if (Marshal.GetLastPInvokeError() == 2) return;
            throw Error("OpenJobObject");
        }
        try
        {
            if (!TerminateJobObject(job, 130)) throw Error("TerminateJobObject");
            await WaitEmpty(job, ct);
        }
        finally { CloseHandle(job); }
    }

    private static async Task WaitEmpty(nint job, CancellationToken ct)
    {
        var buffer = Marshal.AllocHGlobal(48);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!QueryInformationJobObject(job, 1, buffer, 48, out _)) throw Error("QueryInformationJobObject");
                if (Marshal.ReadInt32(buffer, 40) == 0) return; // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.ActiveProcesses
                await Task.Delay(20, ct);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal Child Start(ProcessStartInfo info)
    {
        var resolved = ExecutableResolver.Resolve(info.FileName) ?? throw new Win32Exception(2, "Executable not found: " + info.FileName);
        if (resolved.Prefix.Count != 0) throw new IOException("Verification commands must resolve to native executables.");
        using var attributes = new Attributes(handle);
        var child = new Child();
        nint environment = 0;
        ProcessInformation created = default;
        try
        {
            attributes.SetHandles(child.Input.ClientSafePipeHandle.DangerousGetHandle(), child.Output.ClientSafePipeHandle.DangerousGetHandle(), child.Error.ClientSafePipeHandle.DangerousGetHandle());
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = child.Input.ClientSafePipeHandle.DangerousGetHandle(),
                    Output = child.Output.ClientSafePipeHandle.DangerousGetHandle(), Error = child.Error.ClientSafePipeHandle.DangerousGetHandle(),
                },
                Attributes = attributes.Pointer,
            };
            var block = string.Join('\0', info.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var command = new StringBuilder(string.Join(' ', new[] { resolved.FileName }.Concat(info.ArgumentList).Select(Quote)));
            if (!CreateProcessW(resolved.FileName, command, 0, 0, true, 0x80000 | 0x400 | 0x4 | 0x08000000,
                environment, info.WorkingDirectory, ref startup, out created)) throw Error("CreateProcess with verification job (Windows 10+ required)");
            child.Process = Process.GetProcessById((int)created.ProcessId);
            // Acquire the managed handle before releasing the native handle, even for immediately exiting children.
            _ = child.Process.Handle;
            child.Input.DisposeLocalCopyOfClientHandle();
            child.Output.DisposeLocalCopyOfClientHandle();
            child.Error.DisposeLocalCopyOfClientHandle();
            BeforeResume?.Invoke(child.Process);
            if (ResumeThread(created.Thread) == uint.MaxValue) throw Error("ResumeThread");
            return child;
        }
        catch
        {
            if (created.Process != 0) TerminateProcess(created.Process, 1);
            child.Dispose();
            throw;
        }
        finally
        {
            if (created.Thread != 0) CloseHandle(created.Thread);
            if (created.Process != 0) CloseHandle(created.Process);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Dispose()
    {
        ambient.Value = previous;
        CloseHandle(handle);
    }

    internal sealed class Child : IDisposable
    {
        internal readonly AnonymousPipeServerStream Input = new(PipeDirection.Out, HandleInheritability.Inheritable);
        internal readonly AnonymousPipeServerStream Output = new(PipeDirection.In, HandleInheritability.Inheritable);
        internal readonly AnonymousPipeServerStream Error = new(PipeDirection.In, HandleInheritability.Inheritable);
        internal Process Process = null!;
        public void Dispose() { Input.Dispose(); Output.Dispose(); Error.Dispose(); Process?.Dispose(); }
    }

    private sealed class Attributes : IDisposable
    {
        public nint Pointer { get; }
        private readonly nint jobs;
        private readonly nint handles;
        public Attributes(nint job)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            jobs = Marshal.AllocHGlobal(nint.Size);
            handles = Marshal.AllocHGlobal(nint.Size * 3);
            try
            {
                if (!InitializeProcThreadAttributeList(Pointer, 2, 0, ref size)) throw Error("InitializeProcThreadAttributeList");
                Marshal.WriteIntPtr(jobs, job);
                if (!UpdateProcThreadAttribute(Pointer, 0, 0x2000D, jobs, (nuint)nint.Size, 0, 0)) throw Error("JOB_LIST attribute");
            }
            catch { Dispose(); throw; }
        }
        public void SetHandles(params nint[] values)
        {
            for (var i = 0; i < values.Length; i++) Marshal.WriteIntPtr(handles, i * nint.Size, values[i]);
            if (!UpdateProcThreadAttribute(Pointer, 0, 0x20002, handles, (nuint)(nint.Size * values.Length), 0, 0)) throw Error("HANDLE_LIST attribute");
        }
        public void Dispose()
        {
            DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(handles);
        }
    }

    private static Win32Exception Error(string operation) => new(Marshal.GetLastPInvokeError(), operation);
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint CreateJobObjectW(nint attributes, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(nint job, int information, nint buffer, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(nint job, int information, nint buffer, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
