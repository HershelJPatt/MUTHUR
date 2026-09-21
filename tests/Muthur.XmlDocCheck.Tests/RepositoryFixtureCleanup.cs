using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Muthur.XmlDocCheck.Tests;

internal static class RepositoryFixtureCleanup
{
    internal static void Delete(string root)
    {
        var elapsed = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(5);
        Delete(root, () => DeleteAttempt(root), OperatingSystem.IsWindows(), condition =>
        {
            var remaining = budget - elapsed.Elapsed;
            return remaining > TimeSpan.Zero && SpinWait.SpinUntil(
                () => elapsed.Elapsed < budget && condition(), remaining);
        });
    }

    internal static void Delete(string root, Action deleteAttempt, bool isWindows,
        Func<Func<bool>, bool> retryDriver)
    {
        root = Path.GetFullPath(root);
        var elapsed = Stopwatch.StartNew();
        var attempts = 0;
        IOException? lastError = null;

        bool TryDelete()
        {
            attempts++;
            try
            {
                deleteAttempt();
                if (Path.Exists(root))
                    throw new IOException($"Fixture cleanup left '{root}' present.");
                return true;
            }
            catch (IOException error) when (isWindows
                && (error.HResult == unchecked((int)0x80070020)
                    || error.HResult == unchecked((int)0x80070021)))
            {
                lastError = error;
                return false;
            }
        }

        if (TryDelete()) return;
        if (retryDriver(TryDelete) && !Path.Exists(root)) return;
        throw new IOException(
            $"Fixture cleanup failed for '{root}' after {attempts} attempts in {elapsed.Elapsed}; "
            + $"last HResult: 0x{lastError!.HResult:X8}.", lastError);
    }

    internal static void DeleteAttempt(string root)
    {
        if (!Path.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }))
                File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }

    internal static void Run(string root, Action body, Action? cleanup = null)
    {
        ExceptionDispatchInfo? bodyFailure = null;
        try { body(); }
        catch (Exception error) { bodyFailure = ExceptionDispatchInfo.Capture(error); }

        try { (cleanup ?? (() => Delete(root)))(); }
        catch (Exception error) when (bodyFailure is not null)
        {
            throw new AggregateException(bodyFailure.SourceException, error);
        }
        bodyFailure?.Throw();
    }
}
