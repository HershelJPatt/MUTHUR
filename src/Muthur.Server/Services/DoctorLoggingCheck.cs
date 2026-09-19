using Muthur.Contracts;

namespace Muthur.Server.Services;

/// <summary>Whether the hub can write the log it is configured to write — the record every validator reads.</summary>
public sealed class DoctorLoggingCheck(MuthurOptions options) : IDoctorCheck
{
    public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CheckDto>>([Inspect(Path.Combine(options.DataDir, MuthurEnvironment.LogFile))]);

    private static CheckDto Inspect(string path)
    {
        // A directory passes every check a write makes until the write itself, which fails with access denied.
        if (Directory.Exists(path))
            return Check(CheckStatus.Fail, $"{path} is a directory; the hub cannot write its log there.");

        try
        {
            // Prove what writing a log line needs: that the path itself opens for append. Not a byte is written,
            // and the file is left where it is even if this created it — the hub is appending to the same file,
            // and a probe that deletes what it created can discard what a concurrent writer just wrote.
            using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Check(CheckStatus.Fail,
                $"The hub cannot write {path}: {ex.Message}. Its own log is the record every validator reads.");
        }

        return Check(CheckStatus.Ok, $"Writing to {path}.");

        // A log path is not a credential, unlike an outbound target's address, so it may appear in the detail.
        static CheckDto Check(CheckStatus status, string detail) => new("logging", MuthurEnvironment.LogFile, status, detail);
    }
}
