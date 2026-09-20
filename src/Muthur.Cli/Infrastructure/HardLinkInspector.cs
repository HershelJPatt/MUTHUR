using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Muthur.Cli.Infrastructure;

internal readonly record struct HardLinkResult(bool Success, uint Count, string Reason)
{
    internal static HardLinkResult Failure(string reason) => new(false, 0, reason);
    internal static HardLinkResult FromCount(uint count) => count == 0
        ? Failure("native inspection returned zero hard links")
        : new(true, count, "");
}

internal static partial class HardLinkInspector
{
    internal static string? PlatformFailure(OSPlatform platform, Architecture architecture, bool littleEndian)
    {
        if (architecture is not (Architecture.X64 or Architecture.Arm64))
            return $"unsupported architecture {architecture}";
        if (platform != OSPlatform.Windows && platform != OSPlatform.Linux && platform != OSPlatform.OSX)
            return $"unsupported platform {platform}";
        return platform == OSPlatform.Linux && !littleEndian ? "unsupported Linux byte order" : null;
    }

    internal static HardLinkResult Inspect(string path)
    {
        var platform = OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Create("unknown");
        if (PlatformFailure(platform, RuntimeInformation.ProcessArchitecture, BitConverter.IsLittleEndian) is { } failure)
            return HardLinkResult.Failure(failure);

        var api = platform == OSPlatform.Windows ? "File.OpenHandle"
            : platform == OSPlatform.Linux ? "statx"
            : RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "stat$INODE64" : "stat";
        try
        {
            path = Path.GetFullPath(path);
            if (platform == OSPlatform.Windows)
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                api = "GetFileInformationByHandle";
                return GetFileInformationByHandle(handle, out var info) != 0
                    ? HardLinkResult.FromCount(info.NumberOfLinks)
                    : NativeFailure(api, Marshal.GetLastPInvokeError());
            }
            if (platform == OSPlatform.Linux)
            {
                var status = Statx(-100, path, 0, 4, out var info);
                return status == 0 ? StatxResult(info.Mask, info.NumberOfLinks)
                    : NativeFailure(api, Marshal.GetLastPInvokeError());
            }
            DarwinInfo darwin;
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? StatInode64(path, out darwin) : Stat(path, out darwin);
            return result == 0 ? HardLinkResult.FromCount(darwin.NumberOfLinks)
                : NativeFailure(api, Marshal.GetLastPInvokeError());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HardLinkResult.Failure($"{api} failed: OS error {ex.HResult & 0xffff}: {ex.Message}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return HardLinkResult.Failure($"{api} unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static HardLinkResult NativeFailure(string api, int error) =>
        HardLinkResult.Failure($"{api} failed: OS error {error}");

    internal static HardLinkResult StatxResult(uint mask, uint count) => (mask & 4) == 0
        ? HardLinkResult.Failure("statx returned no STATX_NLINK result mask")
        : HardLinkResult.FromCount(count);

    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    internal struct WindowsInfo
    {
        [FieldOffset(40)] internal uint NumberOfLinks;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetFileInformationByHandle(SafeFileHandle handle, out WindowsInfo info);

    // Linux UAPI: https://raw.githubusercontent.com/torvalds/linux/master/include/uapi/linux/stat.h
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct LinuxInfo
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint NumberOfLinks;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out LinuxInfo info);

    // Darwin LP64: https://raw.githubusercontent.com/apple-oss-distributions/xnu/main/bsd/sys/stat.h
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct DarwinInfo
    {
        [FieldOffset(6)] internal ushort NumberOfLinks;
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "stat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatInode64(string path, out DarwinInfo info);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(string path, out DarwinInfo info);
}
