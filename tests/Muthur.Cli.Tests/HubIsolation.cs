using System.Runtime.CompilerServices;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// Every CLI test runs with the hub environment pointed at nothing. A clean environment is not safe:
/// MuthurEnvironment.Url falls back to the live hub's own default address, so safety means setting these
/// variables rather than clearing them. A module initializer runs before any test in the assembly, so no
/// test class has to opt in and none can forget.
/// </summary>
internal static class HubIsolation
{
    /// <summary>Port 1 is reserved and never listened on: a connection attempt fails at once rather than hanging.</summary>
    public const string UnreachableUrl = "http://127.0.0.1:1";

    [ModuleInitializer]
    internal static void Isolate()
    {
        // A fresh empty home, so Globals.ResolveToken finds no founder token to fall back to. It holds
        // nothing but a token file the tests may write, so it goes again at process exit — one directory
        // per run, and a run that cannot delete it has nothing to say about it.
        var home = Path.Combine(Path.GetTempPath(), "muthur-cli-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };

        Environment.SetEnvironmentVariable(MuthurEnvironment.UrlVariable, UnreachableUrl);
        Environment.SetEnvironmentVariable(MuthurEnvironment.HomeVariable, home);
        Environment.SetEnvironmentVariable(MuthurEnvironment.TokenVariable, null);
        Environment.SetEnvironmentVariable(Globals.AgentVariable, null);
    }
}
