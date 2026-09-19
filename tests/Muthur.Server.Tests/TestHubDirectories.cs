using System.Runtime.CompilerServices;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// What becomes of a hub's data directory when its <see cref="HubFactory"/> is disposed, and the count that
/// says so out loud. A directory is kept only when its hub logged an error it was not expecting: T-41's root
/// cause was read off exactly such a log and nothing else here records an unhandled server error, while
/// keeping every directory is what left 49,437 of them in the temp directory without anyone noticing.
/// </summary>
internal static class TestHubDirectories
{
    private enum Disposition { Removed, Kept, Failed }

    private readonly record struct Outcome(Disposition What, string Note);

    private static readonly Lock Gate = new();

    /// <summary>One entry per data directory, holding the latest outcome for it — see <see cref="Release"/>.</summary>
    private static readonly Dictionary<string, Outcome> Outcomes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The directory every hub's <c>DataDir</c> is created under, named so a reader can go and look.</summary>
    private static string Root => Path.Combine(Path.GetTempPath(), "muthur-tests");

    internal static void Release(string dataDir, bool expectsLoggedErrors = false)
    {
        // A restart test brings a second hub up over the same DataDir; whichever disposes second finds it
        // already gone. That is not a failure, and the outcome already recorded for the path stands.
        if (!Directory.Exists(dataDir)) return;

        // WebApplicationFactory.Dispose() reaches Dispose(bool) twice — once directly, and once through the
        // DisposeAsync() it starts — and only by the second arrival has the host let go of its log. So both
        // arrivals attempt the delete, and the outcome recorded for a path is the latest one: a first attempt
        // that fails and a second that succeeds is one directory, removed.
        var outcome = Attempt(dataDir, expectsLoggedErrors);
        lock (Gate) Outcomes[dataDir] = outcome;
    }

    private static Outcome Attempt(string dataDir, bool expectsLoggedErrors)
    {
        if (!expectsLoggedErrors && Evidence(dataDir) is { } note) return new(Disposition.Kept, note);
        try
        {
            Directory.Delete(dataDir, recursive: true);
            return new(Disposition.Removed, "");
        }
        catch (IOException ex)
        {
            return new(Disposition.Failed, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(Disposition.Failed, ex.Message);
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

    /// <summary>The summary as it will be written, so a test can read back what was recorded for a path.</summary>
    internal static List<string> Summary()
    {
        lock (Gate)
        {
            if (Outcomes.Count == 0) return [];
            var kept = Outcomes.Where(o => o.Value.What is Disposition.Kept).ToList();
            var failed = Outcomes.Where(o => o.Value.What is Disposition.Failed).ToList();
            // The first line is written whenever anything was released, even with nothing kept and nothing
            // failed: a number that is always there is what stops this going unnoticed again.
            List<string> lines =
            [
                $"muthur-tests: {Outcomes.Count - kept.Count - failed.Count} removed, {kept.Count} kept (logged an error), {failed.Count} could not be removed. Root: {Root}",
                .. kept.Select(o => $"  kept    {o.Key}  ({o.Value.Note})"),
                .. failed.Select(o => $"  failed  {o.Key}  ({o.Value.Note})"),
            ];
            return lines;
        }
    }

    // A module initializer runs before any test in the assembly, so no test class has to opt in and none can
    // forget. The summary itself waits for process exit, when every hub has been disposed.
    [ModuleInitializer]
    internal static void ReportAtExit() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        foreach (var line in Summary()) Console.Error.WriteLine(line);
    };
}
