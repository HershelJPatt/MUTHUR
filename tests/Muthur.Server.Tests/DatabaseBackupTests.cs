using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Muthur.Contracts;
using Muthur.Data;
using Muthur.Server.Infrastructure;

namespace Muthur.Server.Tests;

public sealed class DatabaseBackupTests : IDisposable
{
    private const string BackupsDirectory = "backups";

    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (var dir in _temp)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void File_name_carries_the_clock_and_the_migration_it_precedes()
    {
        var name = DatabaseBackup.FileName(
            new DateTimeOffset(2026, 9, 18, 21, 33, 55, TimeSpan.Zero), "20260918180843_ValidationClaim");

        Assert.Equal("muthur-20260918-213355-before-20260918180843_ValidationClaim.db", name);
    }

    [Fact]
    public void To_delete_keeps_the_newest_five_whatever_order_it_is_given()
    {
        var names = Enumerable.Range(1, 8).Select(i => $"muthur-2026091{i}-120000-before-{i}_M.db").ToList();

        var oldest = DatabaseBackup.ToDelete(names, keep: 5);
        Assert.Equal(
            new[] { "muthur-20260913-120000-before-3_M.db", "muthur-20260912-120000-before-2_M.db", "muthur-20260911-120000-before-1_M.db" },
            oldest);

        // Reversed and shuffled inputs answer the same: the order comes from the names, not the sequence.
        Assert.Equal(oldest, DatabaseBackup.ToDelete(Enumerable.Reverse(names), keep: 5));
        Assert.Equal(oldest, DatabaseBackup.ToDelete(
            [names[4], names[0], names[7], names[2], names[6], names[1], names[5], names[3]], keep: 5));

        Assert.Empty(DatabaseBackup.ToDelete(names.Take(5), keep: 5));
        Assert.Empty(DatabaseBackup.ToDelete(names.Take(3), keep: 5));
        Assert.Empty(DatabaseBackup.ToDelete([], keep: 5));
    }

    [Fact]
    public void A_fresh_hub_writes_no_backup()
    {
        using var hub = new HubFactory();

        _ = hub.FounderToken; // forces startup, which migrates a database that does not exist yet

        Assert.True(File.Exists(Path.Combine(hub.DataDir, MuthurEnvironment.DatabaseFile)));
        Assert.False(Directory.Exists(Path.Combine(hub.DataDir, BackupsDirectory)),
            "a first start has nothing to lose and must not litter the data directory");
    }

    [Fact]
    public async Task The_copy_contains_rows_that_are_still_only_in_the_write_ahead_log()
    {
        var dir = NewTempDirectory();
        var path = Path.Combine(dir, MuthurEnvironment.DatabaseFile);
        var options = new MuthurOptions { DataDir = dir, ConnectionString = $"Data Source={path};Pooling=False" };
        var logger = new RecordingLogger();

        await using var db = new MuthurDb(
            new DbContextOptionsBuilder<MuthurDb>().UseSqlite(options.ResolveConnectionString()).Options);

        // The connection stays open for the whole test: closing the last one checkpoints the WAL away, and an
        // uncheckpointed row is exactly what this test is about.
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE probe (note TEXT);");
        await ExecuteAsync(connection, "INSERT INTO probe (note) VALUES ('checkpointed');");
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;");
        await ExecuteAsync(connection, "INSERT INTO probe (note) VALUES ('still in the wal');");

        var wal = new FileInfo(path + "-wal");
        Assert.True(wal is { Exists: true, Length: > 0 }, "the second row should be in the -wal file, not in muthur.db");

        await DatabaseBackup.BeforeMigratingAsync(
            db, options, new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 21, 33, 55, TimeSpan.Zero)), logger);

        var copy = Assert.Single(Directory.GetFiles(Path.Combine(dir, BackupsDirectory)));
        Assert.Equal($"muthur-20260918-213355-before-{FirstMigration}.db", Path.GetFileName(copy));

        // A File.Copy of muthur.db alone would carry 'checkpointed' and lose 'still in the wal'.
        Assert.Equal(new[] { "checkpointed", "still in the wal" }, ReadNotes(copy));

        var announcement = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains(copy, announcement.Message, StringComparison.Ordinal);
        Assert.Contains(path, announcement.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_five_newest_copies_are_kept()
    {
        var dir = NewTempDirectory();
        var path = Path.Combine(dir, MuthurEnvironment.DatabaseFile);
        SeedUnmigratedDatabase(path);
        var backups = Directory.CreateDirectory(Path.Combine(dir, BackupsDirectory));
        foreach (var day in Enumerable.Range(11, 6))
            await File.WriteAllTextAsync(Path.Combine(backups.FullName, $"muthur-202609{day}-120000-before-old_M.db"), "stale");

        var options = new MuthurOptions { DataDir = dir, ConnectionString = $"Data Source={path};Pooling=False" };
        await using var db = new MuthurDb(
            new DbContextOptionsBuilder<MuthurDb>().UseSqlite(options.ResolveConnectionString()).Options);

        await DatabaseBackup.BeforeMigratingAsync(
            db, options, new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 21, 33, 55, TimeSpan.Zero)), new RecordingLogger());

        var kept = Directory.GetFiles(backups.FullName).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                "muthur-20260913-120000-before-old_M.db",
                "muthur-20260914-120000-before-old_M.db",
                "muthur-20260915-120000-before-old_M.db",
                "muthur-20260916-120000-before-old_M.db",
                $"muthur-20260918-213355-before-{FirstMigration}.db",
            },
            kept);
    }

    [Fact]
    public async Task A_hub_with_migrations_to_apply_copies_the_database_and_starts()
    {
        var dir = NewTempDirectory();
        SeedUnmigratedDatabase(Path.Combine(dir, MuthurEnvironment.DatabaseFile));

        using var hub = new HubFactory { DataDir = dir };
        var status = await hub.CreateClient().GetFromJsonAsync(Routes.Status, MuthurJsonContext.Default.StatusResponse);

        Assert.NotNull(status);
        Assert.Equal(32, status.InstanceId.Length);
        var copy = Assert.Single(Directory.GetFiles(Path.Combine(dir, BackupsDirectory)));
        // The hub's injected clock reads 2026-01-01 12:00:00, and the copy predates the migration it names.
        Assert.Equal(DatabaseBackup.FileName(hub.Clock.GetUtcNow(), FirstMigration), Path.GetFileName(copy));
        Assert.Equal(new[] { "probe" }, ReadTableNames(copy));
    }

    [Fact]
    public async Task A_backup_that_cannot_be_written_does_not_stop_the_hub()
    {
        var dir = NewTempDirectory();
        SeedUnmigratedDatabase(Path.Combine(dir, MuthurEnvironment.DatabaseFile));
        // A file where the directory has to go: nothing can be copied into it.
        await File.WriteAllTextAsync(Path.Combine(dir, BackupsDirectory), "not a directory");

        using var hub = new HubFactory { DataDir = dir };
        var status = await hub.CreateClient().GetFromJsonAsync(Routes.Status, MuthurJsonContext.Default.StatusResponse);

        // Answering at all means startup got past the failed backup; the instance id means migrations ran.
        Assert.NotNull(status);
        Assert.Equal(32, status.InstanceId.Length);
        Assert.True(File.Exists(Path.Combine(dir, BackupsDirectory)));
    }

    /// <summary>The oldest migration, which is the first pending one against a database EF has never touched.</summary>
    private static string FirstMigration
    {
        get
        {
            using var db = new MuthurDb(new DbContextOptionsBuilder<MuthurDb>().UseSqlite("Data Source=:memory:").Options);
            return db.Database.GetMigrations().First();
        }
    }

    private string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    /// <summary>A non-empty SQLite file that EF has never migrated, so every migration is pending.</summary>
    private static void SeedUnmigratedDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe (note TEXT);";
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static List<string> ReadTableNames(string databasePath) =>
        Read(databasePath, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

    private static List<string> ReadNotes(string databasePath) =>
        Read(databasePath, "SELECT note FROM probe ORDER BY rowid;");

    private static List<string> Read(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
