# T-33 — A copy of the database before every upgrade, so an interrupted migration is recoverable

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a hub that is about to apply migrations takes a consistent copy of its database first and
says where it put it. An upgrade interrupted at the wrong moment stops being "the hub will not start; repair
the schema by hand" and becomes "restore the copy".

Nothing about the migrations themselves changes.

## Why, established rather than assumed

A validator's run logged, for the `ValidatorConcurrency` and `ValidationClaim` migrations:

> PRAGMA foreign_keys = 0 cannot execute in a transaction; interrupted migration may remain partially applied.

A specialist read the generated SQL rather than the warning text
(`dotnet ef migrations script OutboundFlags ValidationClaim`) and established what it actually means. The
conclusion, which this task is built on:

- It is EF's generic advisory for **any** SQLite table rebuild, and both migrations legitimately need one:
  one changes `role_holds`' primary key, the other adds a foreign key, and SQLite can ALTER neither. Neither
  migration is written badly.
- EF emits **four transactions, not one**. The `PRAGMA` statements are transaction-suppressed, so EF commits
  the in-flight transaction before each, and the `__EFMigrationsHistory` row lands **last**.
- Killed *inside* any single transaction → SQLite rolls it back cleanly. The `foreign_keys = 0` window covers
  only DROP + RENAME, which roll back together: **no corruption and no orphan rows.**
- Killed *between* transactions → steps are applied with no history row, so the next start replays the whole
  migration and hits `duplicate column name: holders` (or `table ef_temp_role_holds already exists`). **The
  hub then fails to start on every restart** until someone repairs the schema by hand. The tell is a leftover
  `ef_temp_*` table.

So the risk is loud rather than silent, it exists only at the moment of an upgrade, and a hub that has
already migrated is permanently past it. What is missing is a way back.

## Context

- `src/Muthur.Server/Infrastructure/Startup.cs` line 104–119 — the whole of the change site. A
  `MuthurDb` is created from the factory, `await db.Database.MigrateAsync()` runs unconditionally, then WAL
  is set and the instance id read.
- `src/Muthur.Server/MuthurOptions.cs` — `DataDir`, and `ResolveConnectionString()`, which defaults to
  `{DataDir}/muthur.db` but **may be overridden**, so never reconstruct the path by hand.
- `Microsoft.Data.Sqlite`'s `SqliteConnection.BackupDatabase(SqliteConnection)` is SQLite's online backup
  API. It produces a consistent copy **including anything still in the WAL**, which a file copy of
  `muthur.db` alone would miss — line 108 puts the database in WAL mode, and that setting persists across
  restarts, so `-wal` and `-shm` files are the normal case rather than the exception.
- `tests/Muthur.Server.Tests/HubFactory.cs` — each hub gets an isolated temp `DataDir`.

Constraints that are not obvious:

- **This code runs before the database is migrated, so there is no ledger and no `Ledger.MutateAsync`.**
  Nothing here can record a ledger event. `ILogger` is the only way to say anything.
- Time comes from the injected `TimeProvider`, which is already resolved at line 101.
- The hub is started by a founder at a terminal and by `muthur up`; anything written to the log at
  Information level is what they will see.
- Warnings are errors.

## Non-goals

- Changing any migration, or how EF generates them. The advisory is correct and the migrations are fine.
- Making the upgrade atomic. It cannot be, on SQLite, for a table rebuild.
- Restoring automatically. A hub that silently rolled its own database back would be worse than one that
  will not start: the founder must choose. This task makes the choice possible, not automatic.
- Backing up on every start. Only when there is something to apply.
- Any other provider. SQLite is the only one with a file to copy; a future provider brings its own answer.
- Pruning by age, or a configurable retention. A fixed small count is enough (see Design).

## Design

New `src/Muthur.Server/Infrastructure/DatabaseBackup.cs`, `internal static class DatabaseBackup`.

### When it runs

In `Startup`, immediately before `await db.Database.MigrateAsync()`:

```csharp
await DatabaseBackup.BeforeMigratingAsync(db, options, clock, logger, ct);
await db.Database.MigrateAsync();
```

`BeforeMigratingAsync` does nothing at all unless **all** of these hold:

1. `db.Database.IsSqlite()`.
2. `(await db.Database.GetPendingMigrationsAsync(ct)).FirstOrDefault()` is not null — there is an upgrade to
   protect.
3. The database file already exists and is longer than zero bytes. A fresh install has nothing to lose, and
   backing up an empty database on first start would put a useless file in every new hub's data directory.

The path comes from the open connection's `DataSource`, never from `DataDir` — `ConnectionString` may have
been overridden.

### What it writes

Into `{DataDir}/backups/`, created if absent:

```
muthur-{yyyyMMdd-HHmmss}-before-{first pending migration id}.db
```

for example `muthur-20260918-213355-before-20260918180843_ValidationClaim.db`. The timestamp comes from the
injected `TimeProvider`; the migration id is EF's own and is already filesystem-safe.

The copy is taken with the online backup API, not `File.Copy`:

```csharp
var source = (SqliteConnection)db.Database.GetDbConnection();
if (source.State != ConnectionState.Open) await source.OpenAsync(ct);
using var destination = new SqliteConnection($"Data Source={path}");
destination.Open();
source.BackupDatabase(destination);
```

This is the point of the task: with WAL on, `muthur.db` by itself is not the database, and a file copy can
be missing the most recent commits.

### Then it says so, at Information

```
Database copied to {Path} before applying {Count} migration(s). Restore it over {DataSource} if the upgrade is interrupted.
```

One line, naming both files, because a founder reading it after a failed start needs to know what to copy
where without consulting a document.

### If the backup fails

Log at **Warning** — `"Could not copy the database before migrating: {Message}. Continuing; an interrupted upgrade would have to be repaired by hand."` — and **continue to migrate.**

This is a deliberate choice and the reasoning belongs in the code as a comment. Refusing to start when the
*backup* fails would introduce a new way for the hub not to come up, in the name of a safety net that did
not exist at all until this task. The net is worth having; it is not worth making startup more fragile than
it is today. Catch `Exception`; let `OperationCanceledException` through.

### Retention

Keep the newest **five** backups in that directory and delete the rest, newest-first by filename — the
timestamp is the leading sortable component, so ordinal descending by name is the order. A failure to delete
an old file is logged at Debug and otherwise ignored.

Five is a judgement, not a configurable: this hub's database is **1.09 MB** after a day that landed six
tasks, so five copies is a few megabytes, and five upgrades back is further than anyone will reasonably
restore from.

### The testable seams

Two pure functions, `internal static`, so the naming and the pruning can be tested without a database:

```csharp
internal static string FileName(DateTimeOffset now, string firstPendingMigration);
internal static IReadOnlyList<string> ToDelete(IEnumerable<string> existingFileNames, int keep);
```

`ToDelete` takes bare file names, sorts ordinal descending, skips `keep`, and returns the rest. It must not
touch the filesystem.

## Units of work

### Unit A — the whole task
- **Files:** new `src/Muthur.Server/Infrastructure/DatabaseBackup.cs`; modified
  `src/Muthur.Server/Infrastructure/Startup.cs`; `tests/Muthur.Server.Tests/`.
- **Does:** everything above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. `FileName` produces the documented shape for a known clock and migration id.
  2. `ToDelete` keeps the newest five and returns the rest; returns empty for fewer than five; is stable for
     exactly five; and does not depend on the order it is given.
  3. **A fresh hub writes no backup.** Start a `HubFactory` hub and assert `{DataDir}/backups` does not exist
     — first start has pending migrations but no database worth copying, and this is the case that would
     otherwise litter every new install.
  4. **The copy is real and complete.** Drive the backup directly against a temp SQLite database that has a
     table with a row in it, **with WAL enabled and the row written but not checkpointed**, then open the
     copy and assert the row is there. That is the assertion that distinguishes the online backup API from
     `File.Copy`; write it so it would fail if someone replaced the call with a file copy, and say in your
     report whether you confirmed that.
  5. A backup failure does not stop the hub: point the backup at a directory that cannot be created (or
     otherwise force it to throw) and assert the hub still starts and still migrates.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t33
$env:MUTHUR_HOME = "$PWD/artifacts/t33-home"; $env:MUTHUR_URL = "http://127.0.0.1:7444"
./artifacts/t33/muthur.exe up
```

- First start on an empty home: the hub comes up and `{MUTHUR_HOME}/backups` **does not exist**.
- `muthur down`, then start again: still no backup, because there are no pending migrations.
- To see the real thing, the database must be behind the binary. The honest way without hand-editing a
  database: keep the scratch `muthur.db` from a build that predates a migration, point a newer build's
  `MUTHUR_HOME` at it, and start. A backup appears in `backups/`, the log names it, and `muthur status`
  answers normally. If that is not practical in the validator's environment, say so — acceptance test 4 is
  the load-bearing check and it does not need two builds.
- The backup file opens as a SQLite database and contains the ledger's rows.

## Out of scope / follow-ups

- **T-32** is the sibling of this one: `install.ps1` publishes whatever working tree it is run from, which is
  how a hub ends up running a binary nobody meant to install. This task makes the *database* recoverable; that
  one makes the *binary* deliberate.
- `muthur doctor` could report the newest backup and its age. It would be one more check in a pattern that
  already exists, and it is not this task.
- Nothing prunes backups for a hub that is never started again. Five files of about a megabyte is not worth
  a sweeper.
