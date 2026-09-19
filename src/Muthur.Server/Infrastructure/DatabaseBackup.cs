using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Muthur.Data;

namespace Muthur.Server.Infrastructure;

/// <summary>
/// Copies the database aside before migrations are applied, so an upgrade interrupted between EF's
/// transactions can be recovered by restoring the copy instead of repairing the schema by hand.
/// </summary>
internal static class DatabaseBackup
{
    /// <summary>How many copies are kept. A few megabytes, and further back than anyone will restore from.</summary>
    private const int Keep = 5;

    private const string DirectoryName = "backups";

    /// <summary>
    /// Copies the database into <c>{DataDir}/backups</c> when — and only when — there is an upgrade to
    /// protect and a database worth copying. Never throws: see the comment on the catch.
    /// </summary>
    public static async Task BeforeMigratingAsync(
        MuthurDb db, MuthurOptions options, TimeProvider clock, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            // SQLite is the only provider with a file to copy; a future one brings its own answer.
            if (!db.Database.IsSqlite()) return;

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.FirstOrDefault() is not { } first) return;

            // Never reconstruct {DataDir}/muthur.db: MuthurOptions.ConnectionString may have been overridden.
            // An open connection reports the absolute path SQLite actually attached.
            var source = (SqliteConnection)db.Database.GetDbConnection();
            if (source.State != ConnectionState.Open) await source.OpenAsync(ct);
            var dataSource = source.DataSource;

            // A fresh install has nothing to lose, and a copy of an empty database in every new hub's data
            // directory would be litter.
            if (new FileInfo(dataSource) is not { Exists: true, Length: > 0 }) return;

            var directory = Path.Combine(options.DataDir, DirectoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName(clock.GetUtcNow(), first));

            // SQLite's online backup API, not File.Copy: the hub runs in WAL mode, so the most recent
            // commits can still be in the -wal file and muthur.db alone is not the database.
            using (var destination = new SqliteConnection($"Data Source={path}"))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }

            logger.LogInformation(
                "Database copied to {Path} before applying {Count} migration(s). Restore it over {DataSource} if the upgrade is interrupted.",
                path, pending.Count, dataSource);

            Prune(directory, logger);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately not fatal, and the next reader's instinct will be to make it fatal. Refusing to
            // start when the *backup* fails would add a new way for the hub not to come up, in the name of a
            // safety net that did not exist at all before this. The net is worth having; it is not worth
            // making startup more fragile than it already is.
            logger.LogWarning(
                "Could not copy the database before migrating: {Message}. Continuing; an interrupted upgrade would have to be repaired by hand.",
                ex.Message);
        }
    }

    internal static string FileName(DateTimeOffset now, string firstPendingMigration) =>
        $"muthur-{now:yyyyMMdd-HHmmss}-before-{firstPendingMigration}.db";

    /// <summary>
    /// The names to delete, keeping the newest <paramref name="keep"/>. The timestamp is the leading
    /// sortable component of the name, so ordinal descending is newest-first. Touches no filesystem.
    /// </summary>
    internal static IReadOnlyList<string> ToDelete(IEnumerable<string> existingFileNames, int keep) =>
        [.. existingFileNames.OrderByDescending(n => n, StringComparer.Ordinal).Skip(keep)];

    private static void Prune(string directory, ILogger logger)
    {
        string[] existing;
        try
        {
            existing = Directory.GetFiles(directory, "muthur-*.db");
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not list old backups in {Directory}: {Message}", directory, ex.Message);
            return;
        }

        foreach (var name in ToDelete(existing.Select(p => Path.GetFileName(p)), Keep))
        {
            try
            {
                File.Delete(Path.Combine(directory, name));
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not delete the old backup {Name}: {Message}", name, ex.Message);
            }
        }
    }
}
