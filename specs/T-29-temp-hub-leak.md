# T-29 — The server test suite leaks temp hub directories, silently

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a `dotnet test` run leaves behind only the hub data directories it has a reason to keep, and
it says out loud how many it removed, how many it kept and why, and how many it could not remove. Two real
handle leaks in **production** code are fixed — the file logger and the pre-migration backup — so the
directories can be removed at all. The 49,437 directories already in `%TEMP%\muthur-tests` are cleaned up by a
script that preserves the logs worth preserving.

This matters beyond tidiness. The hub's own `muthur.log` is held open for the life of the process and past it;
a hub that shuts down does not release its log. The backup path leaks a pooled SQLite connection on every
migration. Neither was visible because `HubFactory.Dispose` swallowed the consequence.

## Context — the measurement, already done

The orchestrator ran the experiment the task asked for. Do not repeat it; build from its result.

Method: `dotnet test tests/Muthur.Server.Tests` with `TEMP`/`TMP` pointed at a private scratch directory, so
the count is this run's and not other agents'. 233 tests, 233 hubs.

**Run 1, unchanged code: 185 directories left behind.** Their contents are the finding:

| Count | What was left in the directory |
|---|---|
| 182 | `muthur.log` **and nothing else** |
| 2 | `backups/muthur-*.db` only |
| 1 | `muthur.log` and `backups/muthur-*.db` |

`Directory.Delete(recursive: true)` removes files as it walks, so `muthur.db`, `-wal` and `-shm` were all
deleted successfully in every case. **SQLite was never holding the data file.** Both leads recorded in the
previous handover — synchronous `WebApplicationFactory` disposal, and requests in flight at shutdown — are
wrong. Only 1 of the 185 logs contained `Unhandled error on`.

### Cause 1 — the file logger is never disposed (182 of 185)

`src/Muthur.Server/Infrastructure/Startup.cs`:

```csharp
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDir, MuthurEnvironment.LogFile)));
```

`ILoggingBuilder.AddProvider(ILoggerProvider)` registers an **already-constructed instance**
(`services.AddSingleton(provider)`). Microsoft.Extensions.DependencyInjection does not dispose instances it did
not create, and `LoggerFactory` registers DI-supplied providers with `dispose: false` because it assumes the
container owns them. Neither owner disposes it. `FileLoggerProvider.Dispose` exists and is correct; it is
simply never called.

The `FileStream` handle is therefore released only when the GC finalizes its `SafeFileHandle` — which is why
the leak count varies run to run (115, 305, 280, 199, 185 across five measurements). It is not flakiness; it
is finalization timing.

**Verified fix.** Registering through a factory makes the container the creator, and therefore the disposer:

```csharp
builder.Logging.Services.AddSingleton<ILoggerProvider>(
    _ => new FileLoggerProvider(Path.Combine(options.DataDir, MuthurEnvironment.LogFile)));
```

With only this change, run 2 of the same experiment left **3** directories instead of 185, all 233 tests still
passing.

### Cause 2 — the backup destination connection is pooled (3 of 185)

`src/Muthur.Server/Infrastructure/DatabaseBackup.cs`:

```csharp
using (var destination = new SqliteConnection($"Data Source={path}"))
```

No `Pooling=False`. Disposing the connection returns it to Microsoft.Data.Sqlite's process-global pool, which
keeps the file handle open on the backup `.db`. The hub's own connection string already carries
`Pooling=False` (T-22); this one was missed. These are the 3 directories that survived run 2.

### What the previous handover got right, and the decision it left open

The retention argument is real and is adopted here: T-41's root cause was read off the `muthur.log` of a leaked
directory, and nothing else in this organization records unhandled server errors. Deleting
`%TEMP%\muthur-tests` wholesale at teardown would have made T-41 unsolvable.

The measurement settles the cost, though: **1 directory in 185 logged an error.** So the option the handover
called cheapest is also now measured to be cheap, and it is the one this spec chooses:

> **Keep a hub's data directory when that hub logged an error; delete it otherwise. Never delete wholesale.**

## Non-goals

- Anything about *why* a request is in flight when a hub is disposed. T-41 handled the hub's response; the
  remaining shutdown-race question is out of scope and is listed as a follow-up.
- Changing `WebApplicationFactory` disposal to the async path. The measurement shows it is not the cause; do
  not touch it.
- Log rolling, log format, or `DoctorLoggingCheck`.
- `tests/Muthur.Cli.Tests` hub isolation semantics. Its *directory cleanup* is in scope (Unit B) because
  `HubIsolation.cs` names T-29 as the owner; nothing else about it is.

## Design

### Production changes (Unit A)

1. `Startup.cs`: register `FileLoggerProvider` through a factory delegate, exactly as shown above. Replace the
   `AddProvider` call. Keep `builder.Logging.AddConsole()` and `ClearProviders()` as they are. The existing
   comment above the block stays.
2. `DatabaseBackup.cs`: the destination connection string becomes `$"Data Source={path};Pooling=False"`. Add a
   short comment naming the reason (a pooled connection keeps the backup file's handle open after `Dispose`,
   and the hub's own connection string already sets this).

### Test-suite accounting (Unit B)

New file `tests/Muthur.Server.Tests/TestHubDirectories.cs`, `internal static class TestHubDirectories`:

- `internal static void Release(string dataDir)` — called by `HubFactory.Dispose` in place of the current
  `try { Directory.Delete... } catch`. Behavior, in order:
  - If `!Directory.Exists(dataDir)`, record nothing and return. (A restart test brings a second hub up over the
    same `DataDir`; whichever disposes second finds it gone. That is not a failure.)
  - Read `muthur.log` if present. If `LoggedAnError` returns true, increment *kept*, record the path and the
    number of matching lines, and **leave the directory in place**.
  - Otherwise `Directory.Delete(dataDir, recursive: true)`. On success increment *removed*. On
    `IOException` or `UnauthorizedAccessException`, increment *failed* and record the path and
    `ex.Message`. No other exception is caught.
  - Reading `muthur.log` must not throw: if the read fails for any reason, treat it as "logged an error" and
    keep the directory. Evidence is cheaper than a lost test run.
- `internal static bool LoggedAnError(IEnumerable<string> logLines)` — pure, no filesystem, reachable from the
  test assembly so it can be tested directly. Returns true when any line's second whitespace-separated token
  is `Error` or `Critical`. `FileLogger` writes
  `{DateTimeOffset:O} {logLevel,-11} {category}: {message}`, so the level is the second token; the padding is
  absorbed by splitting on whitespace with `StringSplitOptions.RemoveEmptyEntries`. A line with fewer than two
  tokens is not a match.
- A `[ModuleInitializer]` that hooks `AppDomain.CurrentDomain.ProcessExit` and writes the summary. Follow
  `tests/Muthur.Cli.Tests/HubIsolation.cs` for the module-initializer pattern and comment style.

Counters are shared across parallel test classes: guard them with a `Lock`, as `FileLoggerProvider` does.

**The summary**, written to `Console.Error`, only when at least one directory was released:

```
muthur-tests: 230 removed, 2 kept (logged an error), 1 could not be removed. Root: C:\...\Temp\muthur-tests
  kept    <full path>  (3 error lines in muthur.log)
  failed  <full path>  (The process cannot access the file ... because it is being used by another process.)
```

One `kept` line per kept directory, one `failed` line per failure, in the order they were recorded. The first
line is always written when anything was released, even when kept and failed are both 0 — a number that is
always present is what stops this going unnoticed again.

`HubFactory.Dispose` becomes:

```csharp
protected override void Dispose(bool disposing)
{
    base.Dispose(disposing);
    TestHubDirectories.Release(DataDir);
}
```

No `try`/`catch` remains in `HubFactory`.

### CLI test directories (Unit B)

`tests/Muthur.Cli.Tests/HubIsolation.cs` creates a fresh `muthur-cli-tests/<guid>` home per run and its comment
defers deletion to T-29. Add a `ProcessExit` handler in the same module initializer that deletes that one
directory, ignoring `IOException` and `UnauthorizedAccessException` — it holds nothing but a possible token
file, one directory per run, and no summary is needed. Update the comment so it no longer points at T-29.

### One-off cleanup (Unit B)

New `scripts/clean-test-temp.ps1`:

- Parameters: `[string]$Root = (Join-Path $env:TEMP 'muthur-tests')`, `[switch]$All`, `[switch]$DryRun`.
- Walks the immediate subdirectories of `$Root`. Deletes a subdirectory when `-All` is given, or when its
  `muthur.log` is absent or contains no line whose second whitespace-separated token is `Error` or `Critical`.
- Prints progress no more than once per 1000 directories (there are ~50,000; a line each is noise) and a final
  summary: examined, deleted, kept, failed.
- `-DryRun` prints the same summary and deletes nothing.
- Also accepts `muthur-cli-tests` as `-Root`, where no `muthur.log` exists and everything is therefore
  deletable.
- Follow the parameter/comment style of `scripts/install.ps1`.

## Units of work

### Unit A — the two handle leaks
- **Files:** `src/Muthur.Server/Infrastructure/Startup.cs`, `src/Muthur.Server/Infrastructure/DatabaseBackup.cs`,
  and a new test file `tests/Muthur.Server.Tests/HubDisposalTests.cs`.
- **Does:** both production changes above, plus a regression test:
  a test that creates a `HubFactory`, forces startup and at least one request through it
  (`await hub.Founder().GetAsync(...)` against any existing route — `HubTestExtensions` shows the idiom),
  disposes it explicitly, and asserts `Directory.Exists(dataDir)` is false. Capture `hub.DataDir` before
  disposing. Use `try`/`finally` or an explicit `hub.Dispose()`, not `using var`, so the assertion runs after
  disposal.
  A second test covers the backup leak: follow `DatabaseBackupTests.cs` for how to get a hub that actually
  writes a backup, then assert the same thing — the directory is gone after disposal.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test tests/Muthur.Server.Tests` green,
  and both new tests fail if either production change is reverted. Check that.

### Unit B — cleanup accounting, CLI directories, and the script
- **Files:** new `tests/Muthur.Server.Tests/TestHubDirectories.cs`, new `scripts/clean-test-temp.ps1`,
  modified `tests/Muthur.Server.Tests/HubFactory.cs`, modified `tests/Muthur.Cli.Tests/HubIsolation.cs`,
  and a new test file `tests/Muthur.Server.Tests/TestHubDirectoriesTests.cs`.
- **Does:** everything under "Test-suite accounting", "CLI test directories" and "One-off cleanup".
- **Depends on:** nothing. It must not edit anything under `src/`.
- **Acceptance:** `dotnet build` clean; `dotnet test` green; `TestHubDirectoriesTests` covers `LoggedAnError`
  over at least: a real `Information` line, a real `Error` line, a real `Critical` line, an empty sequence, a
  blank line, and a line whose *message* contains the word `Error` but whose level is `Information` (must not
  match). Run the script with `-DryRun` against a directory you create with three fixtures (one with an error
  log, one with a clean log, one with no log) and confirm the summary counts 1 kept and 2 deleted.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then the measurement, which is the point of the task — from the task branch, on Windows:

```powershell
$sc = "<a scratch directory>"; Remove-Item -Recurse -Force $sc -EA SilentlyContinue
New-Item -ItemType Directory -Force $sc | Out-Null
$env:TEMP = $sc; $env:TMP = $sc
dotnet test tests/Muthur.Server.Tests 2>&1 | Select-String 'muthur-tests:|Passed!'
(Get-ChildItem (Join-Path $sc 'muthur-tests') -Directory -EA SilentlyContinue).Count
```

Passing looks like: 233 passed; a `muthur-tests:` summary line present in the output with `0 could not be
removed`; and a final count of 0 (or, if some test logged an error, a count equal to the summary's *kept*
number, with each of those directories named on a `kept` line).

The summary line appearing in `dotnet test` output is itself an acceptance criterion — if `Console.Error` at
`ProcessExit` is swallowed by the test host, say so and report rather than inventing another mechanism.

A validator can exercise this without a browser: it is all command line.

## Out of scope / follow-ups

- **Requests in flight when a hub is disposed.** T-41 made the hub answer 503 instead of 500; it did not stop
  the suite from disposing hubs mid-request. One log in this run still recorded an unhandled error. Worth its
  own task.
- **The 49,437 existing directories.** The script does it, but running it is an operator action, not part of
  the build. The orchestrator runs it once as part of landing.
- **`FileLoggerProvider` has no `IAsyncDisposable`** and flushes on every line (`AutoFlush = true`). Fine for a
  hub; worth revisiting if logging volume ever matters.
