using System.Runtime.CompilerServices;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// What becomes of a hub's data directory when its <see cref="HubFactory"/> is disposed, and the count that
/// says so out loud. A directory is kept only when its hub logged an error: T-41's root cause was read off
/// exactly such a log and nothing else here records an unhandled server error, while keeping every directory
/// is what left 49,437 of them in the temp directory without anyone noticing.
/// </summary>
internal static class TestHubDirectories
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Released = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(string Path, string Note)> Kept = [];
    private static readonly List<(string Path, string Message)> Failed = [];
    private static int _removed;

    /// <summary>The directory every hub's <c>DataDir</c> is created under, named so a reader can go and look.</summary>
    private static string Root => Path.Combine(Path.GetTempPath(), "muthur-tests");

    internal static void Release(string dataDir)
    {
        // A restart test brings a second hub up over the same DataDir; whichever disposes second finds it
        // already gone. That is not a failure, and nothing is counted for it.
        if (!Directory.Exists(dataDir)) return;
        // WebApplicationFactory.Dispose() reaches Dispose(bool) twice — once directly, once through the
        // DisposeAsync() it starts — so every hub asks to be released twice. Deleting twice was harmless
        // and invisible; counting twice would not be.
        lock (Gate)
        {
            if (!Released.Add(dataDir)) return;
        }

        if (Evidence(dataDir) is { } note)
        {
            lock (Gate) Kept.Add((dataDir, note));
            return;
        }

        try
        {
            Directory.Delete(dataDir, recursive: true);
            // The path is free again, and a restart test's other hub shares it: let it be released on its
            // own account if it puts the directory back.
            lock (Gate)
            {
                Released.Remove(dataDir);
                _removed++;
            }
        }
        catch (IOException ex)
        {
            lock (Gate) Failed.Add((dataDir, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            lock (Gate) Failed.Add((dataDir, ex.Message));
        }
    }

    /// <summary>
    /// True when any line's level is <c>Error</c> or <c>Critical</c>. FileLogger writes
    /// <c>{timestamp} {level,-11} {category}: {message}</c>, so the level is the second whitespace-separated
    /// token once the padding is dropped.
    /// </summary>
    internal static bool LoggedAnError(IEnumerable<string> logLines) => logLines.Any(IsErrorLine);

    private static bool IsErrorLine(string line)
    {
        var tokens = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2 && tokens[1] is "Error" or "Critical";
    }

    /// <summary>Why this directory is worth keeping, or null when nothing in it is.</summary>
    private static string? Evidence(string dataDir)
    {
        var log = Path.Combine(dataDir, MuthurEnvironment.LogFile);
        if (!File.Exists(log)) return null;
        try
        {
            var lines = ReadLog(log);
            if (!LoggedAnError(lines)) return null;
            var errors = lines.Count(IsErrorLine);
            return $"{errors} error line{(errors == 1 ? "" : "s")} in {MuthurEnvironment.LogFile}";
        }
        catch (Exception ex)
        {
            // Evidence is cheaper than a lost test run: a log that cannot be read is treated as one that
            // recorded an error, so the directory survives to be looked at by hand.
            return $"{MuthurEnvironment.LogFile} could not be read: {ex.Message}";
        }
    }

    /// <summary>
    /// File.ReadAllLines asks for a share that excludes writers, and a hub that has not released its log yet
    /// is exactly that writer — so it would refuse to read precisely the logs worth reading.
    /// </summary>
    private static List<string> ReadLog(string log)
    {
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    // A module initializer runs before any test in the assembly, so no test class has to opt in and none can
    // forget. The summary itself waits for process exit, when every hub has been disposed.
    [ModuleInitializer]
    internal static void ReportAtExit() => AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteSummary();

    private static void WriteSummary()
    {
        lock (Gate)
        {
            if (_removed == 0 && Kept.Count == 0 && Failed.Count == 0) return;
            // Written whenever anything was released, even with nothing kept and nothing failed: a number
            // that is always there is what stops this going unnoticed again.
            Console.Error.WriteLine(
                $"muthur-tests: {_removed} removed, {Kept.Count} kept (logged an error), {Failed.Count} could not be removed. Root: {Root}");
            foreach (var (path, note) in Kept) Console.Error.WriteLine($"  kept    {path}  ({note})");
            foreach (var (path, message) in Failed) Console.Error.WriteLine($"  failed  {path}  ({message})");
        }
    }
}
