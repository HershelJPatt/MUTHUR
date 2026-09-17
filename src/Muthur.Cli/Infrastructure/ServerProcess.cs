using System.Diagnostics;
using Muthur.Contracts;

namespace Muthur.Cli.Infrastructure;

/// <summary>Starts the hub server as a detached local process.</summary>
public static class ServerProcess
{
    public static string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "Muthur.Server.exe" : "Muthur.Server";
        if (Environment.GetEnvironmentVariable(MuthurEnvironment.ServerPathVariable) is { Length: > 0 } configured)
            return File.Exists(configured) ? configured : null;
        var bundled = Path.Combine(AppContext.BaseDirectory, "server", exe);
        return File.Exists(bundled) ? bundled : null;
    }

    public static void StartDetached(string serverPath)
    {
        var dir = Path.GetDirectoryName(serverPath)!;
        if (OperatingSystem.IsWindows())
        {
            // ShellExecute does not inherit our stdout/stderr handles. With plain CreateProcess the server
            // would hold the caller's pipe open and any tool capturing `muthur up` output would hang.
            Process.Start(new ProcessStartInfo(serverPath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = dir,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", $"cd \"{dir}\" && nohup \"{serverPath}\" >/dev/null 2>&1 &" },
                UseShellExecute = false,
            });
        }
    }
}
