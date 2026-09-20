using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Muthur.Server.Services;

// A suspended child joins its kill-on-close job before it can execute or create descendants.
internal sealed class CensusWindowsProcess : IDisposable
{
    private SafeFileHandle? _job;
    private SafeFileHandle? _process;
    private Process? _managedProcess;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private Task<bool>? _stop;

    public Process Process => _managedProcess!;
    public StreamReader StandardOutput => _stdout!;
    public StreamReader StandardError => _stderr!;

    public static CensusWindowsProcess Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var child = new CensusWindowsProcess();
        try
        {
            child._job = CreateJobObject(IntPtr.Zero, null);
            if (child._job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(child._job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception();

            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
            using var stdinRead = Pipe(out var stdinWrite, ref attributes);
            using var stdinParent = stdinWrite;
            using var stdoutRead = Pipe(out var stdoutWrite, ref attributes);
            using var stdoutChild = stdoutWrite;
            using var stderrRead = Pipe(out var stderrWrite, ref attributes);
            using var stderrChild = stderrWrite;
            foreach (var handle in new[] { stdinParent, stdoutRead, stderrRead })
                if (!SetHandleInformation(handle, 1, 0)) throw new Win32Exception();

            // Duplicate the parent ends into the streams; the originals stay scoped to startup.
            child._stdout = Reader(stdoutRead);
            child._stderr = Reader(stderrRead);
            using var list = new AttributeList([stdinRead, stdoutChild, stderrChild]);
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StandardInput = stdinRead.DangerousGetHandle(),
                    StandardOutput = stdoutChild.DangerousGetHandle(),
                    StandardError = stderrChild.DangerousGetHandle(),
                },
                Attributes = list.Pointer,
            };
            var info = new ProcessStartInfo(fileName);
            foreach (var variable in new[] { "MUTHUR_AGENT", "MUTHUR_TOKEN", "Muthur__DiscordBotToken" })
                info.Environment.Remove(variable);
            var environment = string.Concat(info.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}={p.Value}\0")) + "\0";
            var command = new StringBuilder(string.Join(" ", new[] { fileName }.Concat(arguments).Select(Quote)));
            var environmentPointer = Marshal.StringToHGlobalUni(environment);
            ProcessInformation created;
            try
            {
                // CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT
                if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080404,
                        environmentPointer, workingDirectory, ref startup, out created))
                    throw new Win32Exception();
            }
            finally { Marshal.FreeHGlobal(environmentPointer); }
            child._process = new SafeFileHandle(created.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(created.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(child._job, child._process)) throw new Win32Exception();
            child._managedProcess = Process.GetProcessById((int)created.ProcessId);
            // Open the managed wait handle while the native handle still guarantees process identity.
            _ = child._managedProcess.Handle;
            stdinParent.Dispose();
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            return child;
        }
        catch
        {
            // Assignment or resume may fail: the still-suspended process must also be terminated and reaped.
            try
            {
                if (child._process is { IsInvalid: false })
                {
                    TerminateProcess(child._process, 1);
                    if (child._job is { IsInvalid: false }) TerminateJobObject(child._job, 1);
                    child._job?.Dispose();
                    WaitForSingleObject(child._process, uint.MaxValue);
                }
            }
            finally { child.Dispose(); }
            throw;
        }
    }

    private static SafeFileHandle Pipe(out SafeFileHandle write, ref SecurityAttributes attributes)
    {
        if (!CreatePipe(out var read, out write, ref attributes, 0))
        {
            read.Dispose();
            write.Dispose();
            throw new Win32Exception();
        }
        return read;
    }

    private static StreamReader Reader(SafeFileHandle pipe)
    {
        var current = GetCurrentProcess();
        if (!DuplicateHandle(current, pipe, current, out var copy, 0, false, 2)) throw new Win32Exception();
        try { return new StreamReader(new FileStream(copy, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8); }
        catch { copy.Dispose(); throw; }
    }

    internal static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public Task<bool> StopAsync() => _stop ??= StopCoreAsync();

    private async Task<bool> StopCoreAsync()
    {
        // Do not let one failed cleanup operation prevent the remaining attempts.
        var terminated = TerminateJobObject(_job!, 1);
        if (!terminated)
        {
            TerminateProcess(_process!, 1);
            _job!.Dispose(); // Kill-on-close is the fallback for the entire owned tree.
        }
        var reaped = await Task.Run(() => WaitForSingleObject(_process!, uint.MaxValue)) == 0;
        if (!terminated) return false;
        while (true)
        {
            if (!QueryInformationJobObject(_job!, 1, out var accounting,
                    (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) return false;
            if (accounting.ActiveProcesses == 0) return reaped;
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        try { _job?.Dispose(); }
        finally
        {
            try { _managedProcess?.Dispose(); }
            finally
            {
                try { _process?.Dispose(); }
                finally
                {
                    try { _stdout?.Dispose(); }
                    finally { _stderr?.Dispose(); }
                }
            }
        }
    }

    private sealed class AttributeList : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private IntPtr _handles;
        private bool _initialized;

        public AttributeList(SafeFileHandle[] handles)
        {
            try
            {
                nuint size = 0;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                Pointer = Marshal.AllocHGlobal(checked((nint)size));
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref size)) throw new Win32Exception();
                _initialized = true;
                _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                for (var i = 0; i < handles.Length; i++)
                    Marshal.WriteIntPtr(_handles, i * IntPtr.Size, handles[i].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, 0x20002, _handles,
                        (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (_initialized) DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_handles);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out BasicAccounting information, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value,
        nuint size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
}
