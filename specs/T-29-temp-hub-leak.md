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

## Amendment 1 — Unit A's acceptance sentence was wrong, and the leak was already closed by Unit A alone (2026-09-19)

Two corrections, both raised by the Unit A implementer, both accepted.

**1. "Both new tests fail if either production change is reverted" cannot be true, and should not be.**

Reverting the logger change fails both tests, because every hub writes `muthur.log`. Reverting the backup
change fails only `A_disposed_hub_releases_the_backup_it_wrote`, because the log-only test never writes a
backup and so has nothing to detect. That is what a targeted regression test is supposed to do. The sentence is
replaced by:

> Each production change has at least one test that fails when it alone is reverted, and no test is broadened
> to make a tidier sentence true.

Verified that way: reverting `Startup.cs` alone failed 2 of 2; reverting `DatabaseBackup.cs` alone failed 1 of
2, naming the surviving directory.

**2. Unit A alone already takes the run to zero leaked directories.**

With both production fixes and `HubFactory.Dispose` still on its original `try { Directory.Delete } catch`, a
full `dotnet test tests/Muthur.Server.Tests` left **0** directories behind (235 tests). The handles were the
whole leak.

This does not make Unit B redundant, and it is worth being explicit about why, because the tempting reading is
that the cleanup accounting is now decoration:

- The silent `catch` is the defect the task named third and independently. It is what let a two-day, 49,437-
  directory leak go unseen; removing the leak without removing the silence leaves the next leak just as quiet.
- Zero is only observable if something reports it. "0 could not be removed" printed once per run is the whole
  point — an absence nobody prints is indistinguishable from an absence nobody checked.
- The retention rule is what keeps T-41-style evidence after the directories stop accumulating by accident.

So Unit B's expected output on an integrated branch is a summary line reporting 0 failures, and that is a pass,
not a no-op.

## Amendment 2 — two defects only the integrated branch could show (2026-09-19)

Units A and B each verified green alone. Merging them produced
`183 removed, 2 kept (logged an error), 1 could not be removed` — worse than Unit A alone, which left zero.
Both causes are recorded here because both are traps the next person will fall into.

**1. The delete had a retry, and it was invisible.**

`WebApplicationFactory.Dispose()` reaches `Dispose(bool)` **twice**: once directly, and once through the
`DisposeAsync()` it starts. The second arrival happens after the host has finished shutting down, so a handle
still open on the first arrival is released by the second. The original swallowing code attempted the delete on
both arrivals, which meant it silently retried and usually won.

Unit B first guarded the double arrival with a "first call wins" set, to stop double-counting. That is correct
about the counting and wrong about the work: it removed the retry. The rule is therefore:

> `Release` is idempotent in what it **counts**, not in what it **attempts**. Every arrival does the work; the
> outcome recorded for a path is the latest one, so a path that fails and then succeeds is reported once, as
> removed.

The spec's frozen `Release` description did not mention the second arrival because the orchestrator did not
know about it. It is now the reason the implementation keys outcomes by path.

**2. Some tests log errors on purpose, and retention was keeping their directories forever.**

The two kept directories were not accidents:

- `SystemTests.A_disposed_object_on_a_hub_that_is_not_stopping_is_still_an_internal_error` — logs
  `ErrorMiddleware: Unhandled error on PUT /api/v1/outbound-targets`, which is the assertion.
- a test asserting EF's failure handling — two `Microsoft.EntityFrameworkCore.Database.Command: Failed
  executing DbCommand` lines.

Retaining those rebuilds the original bug at 2 directories per run instead of 185. Slower is still unbounded.

**Retention means *unexpected*.** `HubFactory` gains `public bool ExpectsLoggedErrors { get; init; }`, passed to
`Release`; when set, the evidence check is skipped and the directory is removed like any other. The flag is set
by the test that intends the error, because that test is the only thing that knows the error is intended. A
category blocklist (ignore `Microsoft.EntityFrameworkCore.*`, say) was rejected: it would silently swallow a
real EF error in a test that did not expect one.

A test that forgets the flag gets a retained directory and a `kept` line naming it. That is the intended
behavior — visible, not silent — and no fallback may hide it.

## Amendment 3 — the cleanup script needed an age guard (2026-09-19)

The script as frozen would delete a hub directory another agent's test run was using at that moment. This
machine runs a dozen agents against one shared `%TEMP%\muthur-tests`, and `Remove-Item -Recurse` deletes files
as it walks: against a live hub it takes `muthur.db` and `muthur.db-wal` and only then hits the still-locked
`muthur.log` and throws. The script would count one `failed` and move on, leaving the other agent a corrupted
database under a running test. That is a worse failure than the leak, and silent on our side.

`clean-test-temp.ps1` therefore takes `-OlderThanMinutes` (default 60) and skips any subdirectory whose own
`LastWriteTimeUtc` is newer than the cutoff. Skipped directories are counted and reported separately from kept
ones — they are not evidence, they are simply not ours to touch yet, and a later pass takes them.
`-All` overrides the retention rule, not the age guard.

## Proof (2026-09-19)

Integrated branch, `TEMP`/`TMP` pointed at a fresh scratch directory:

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test -v n
  Muthur.Launch.Tests    23/23      Test Run Successful.
  Muthur.Core.Tests      3708/3708  Test Run Successful.
  Muthur.Cli.Tests       55/55      Test Run Successful.
  Muthur.Server.Tests    240/240    Test Run Successful.

muthur-tests: 187 removed, 0 kept (logged an error), 0 could not be removed. Root: ...\final2\muthur-tests
```

Leftovers afterwards: `muthur-tests` **0**, `muthur-cli-tests` **0**.

The one-off cleanup, run against the real root at 50,670 directories:

```
C:\Users\hersh\AppData\Local\Temp\muthur-tests: examined 50670, deleted 48845, kept 474, skipped (too recent) 1351, failed 0.
```

474 directories kept because their logs recorded an error, 1,351 skipped because other agents were writing to
them — the guard doing exactly the job it was added for.

### Open question, not blocking

`muthur ask` #14 is with the founder: the summary line is visible at `dotnet test -v n` but **not** at plain
`dotnet test`, because VSTest's console logger at minimal verbosity prints only the result line and failing
tests. The options offered were (A) a cleanup failure fails the run, (B) leave it at `-v n` and document that,
(C) `VSTestVerbosity=normal` for the server test project. The branch implements B's behavior, which is the
status quo of the three and the only one that needs no further change. A and C are each a small follow-up.

## Amendment 4 — the validator was right: the script reproduces this task's own defect (2026-09-19)

`conductor-validator` failed T-29 and the verdict is accepted in full. The leak is fixed and was measured at
zero four times, both revert checks behave as Amendment 1 says, and the retention rule was driven end to end
through a real hub. The task fails on `scripts/clean-test-temp.ps1`.

**The defect.** `Test-LoggedAnError` calls `[IO.File]::ReadLines` at line 35, outside any `try`/`catch`, under
`$ErrorActionPreference = 'Stop'` (line 24). A `muthur.log` it cannot read therefore **terminates the whole
script**: no summary line, the directories after it never examined, exit 1. Eleven of twelve runs against a
live test root aborted that way.

That is this task's own defect class, rebuilt in the fix for it. T-29 was opened because a cleanup failure was
swallowed; the script now loses the entire count instead. The spec wrote the right rule for the C# half —
*"Reading `muthur.log` must not throw: if the read fails for any reason, treat it as 'logged an error' and
keep the directory"* — and `TestHubDirectories.Evidence` implements it. The script half never got it. The
asymmetry was an oversight, not a decision.

### The measurement that reframes the fix

An unreadable log is not an error condition to route around. It is the signal.

`FileLoggerProvider` opens `muthur.log` with `FileShare.ReadWrite`, but `[IO.File]::ReadLines` requests
`FileShare.Read` — which conflicts with the live writer's own write access. Measured:

```
readable while a writer holds it: False
  "The process cannot access the file '...\muthur.log' because it is being used by another process."
```

So a log that cannot be read means **another process owns this directory**. The C# half reads its own hub's
log after disposal and needs `FileShare.ReadWrite` to do it; the script reads *other processes'* logs, where an
open handle is exactly the thing that should stop it. **The script must not copy the C# half's share mode.**
It must catch the failure and keep the directory.

### The age guard's stated reason is false, and it matters

The comment claims "a live hub writes its log, and writing a file touches the directory that holds it".
Measured — 200 appends to an existing `muthur.log`:

```
dir LastWriteTimeUtc before : 2026-09-19T06:40:06.1562385Z
dir LastWriteTimeUtc after  : 2026-09-19T06:40:06.1562385Z
unchanged                   : True
log LastWriteTimeUtc        : 2026-09-19T06:40:08.1743490Z
```

NTFS updates a directory's write time when an entry is created, renamed or removed — not when a file inside it
is appended to. The guard works today only because a live hub churns entries. It does not cover a hub that has
been quiet longer than the cutoff while still holding its log, which is the case Amendment 3 exists to
prevent.

The unreadable-log rule above covers that case for a default run. It does **not** cover `-All`, which skips the
log check entirely — so `-All` against a quiet live hub is the corruption Amendment 3 was written to stop. The
guard has to be true on its own.

### What the fix is

1. **`Test-LoggedAnError` must not throw.** Wrap the read. A read that fails for any reason returns "keep
   this", and the caller records it as *held*, not *kept* — an operator reads those numbers and "someone has
   it open" is a different fact from "it recorded an error".
2. **The age guard reads the newest write inside the directory,** not the directory's own timestamp: the
   maximum of the directory's `LastWriteTimeUtc` and that of every file beneath it
   (`[IO.Directory]::EnumerateFiles($dir, '*', 'AllDirectories')` with `[IO.File]::GetLastWriteTimeUtc`).
   A hub directory holds a handful of files; this is one cheap stat each, not the per-file walk the original
   spec warned off. Replace the false sentence in the comment with what was measured.
3. **A directory that vanished mid-walk is `gone`, not `failed`.** `EnumerateDirectories` yields a path the
   owning test run then removes; `GetLastWriteTimeUtc` returns 1601-01-01, which no cutoff skips, and
   `Remove-Item` raises `ItemNotFoundException`. `failed` is the number an operator acts on and must not
   carry the routine case. Mirror the C# half, which returns without recording when the directory is gone.
4. **Name the paths.** The summary prints one line per *held* and per *failed* directory, as
   `TestHubDirectories` does. A count nobody can investigate is halfway back to a silent catch.
5. **The summary always prints.** Whatever happens to an individual directory, the run ends with its counts:
   `examined N, deleted N, kept N, held N, skipped (too recent) N, gone N, failed N.`

### Also fixed: three behaviors of `Release` that nothing asserts

The validator proved each with a temporary probe and reported the gap rather than resting the verdict on it.
`TestHubDirectoriesTests` covers `LoggedAnError`, the already-gone path and the failed-then-succeeded path, but
nothing pins that `Release` **keeps** a directory whose log recorded an error, that `ExpectsLoggedErrors: true`
removes it anyway, or that an unreadable log keeps it. All three work; a regression in any would be silent,
which is the failure mode this task exists to end. They get tests.

## Amendment 5 — what the race measurement added to Amendment 4 (2026-09-19)

Amendment 4's fix is implemented and the validator's reproduction is closed: exit 0, the summary printed,
`b-locked` named as held, the other three deleted. Two things the amendment did not anticipate, both found by
measuring rather than reasoning.

**1. `gone` has to absorb a not-found raised *during* our own delete, not only one raised before it.**

Amendment 4 said to treat a directory that was already missing as `gone`. Implemented literally, two cleaners
racing over one root of 3,000 directories still reported **66 and 62 `failed`**, in two message shapes —
`Could not find file '<path>\muthur.log'` and `Access to the path '<path>' is denied` — with **0 survivors**.
Zero survivors is the whole argument: every one of those directories was in fact removed, by the other
process, mid-delete. `failed` was lying about half of them.

Classifying `FileNotFoundException` and `DirectoryNotFoundException` alongside `ItemNotFoundException` as
`gone` removed the first shape entirely. Final race: `gone 1288 / failed 33` and `gone 1432 / failed 45`.

`UnauthorizedAccessException` is deliberately **not** absorbed. A delete-pending file, a read-only file and a
genuine ACL problem are indistinguishable from there, and that is a number an operator must see. The residual
"Access is denied" shape only appears when two *cleaners* fight over one root, which is not how the script is
run; against the real deployment — one cleaner, live test runs — it means a genuinely locked file, and staying
`failed` and named is the conservative answer.

**2. A root containing `[` or `]` was a wildcard to `Remove-Item`, which deletes.**

Found by the implementer, outside the amendment. `Test-Path` and `Remove-Item` treat the path as a pattern, so
a bracketed root could have matched sibling directories. Now `[IO.File]::Exists` and `Remove-Item -LiteralPath`.

**Also:** `Test-LoggedAnError` became `Get-LogVerdict`. Amendment 4 gave the function three outcomes (error /
held / clean) while keeping a name whose `Test-` verb promises a boolean. The name was inherited from a
two-outcome version and should have changed with the shape.

### The summary as it now prints

```
<Root>: examined 4, deleted 3, kept 0, held 1, skipped (too recent) 0, gone 0, failed 0.
  held    <full path>  (The process cannot access the file '<path>\muthur.log' because it is being used by another process.)
  failed  <full path>  (<exception message>)
```

`held` is a new number an operator reads beside `kept`, and on a shared root during a busy hour it will not be
zero. That is correct: every one is a directory another process still owns.

## Proof of Amendments 4 and 5 (2026-09-19)

Integrated branch, verified by the orchestrator, `TEMP`/`TMP` on a fresh scratch root:

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test -v n
  Muthur.Launch.Tests    23/23      Test Run Successful.
  Muthur.Core.Tests      3708/3708  Test Run Successful.
  Muthur.Cli.Tests       55/55      Test Run Successful.
  Muthur.Server.Tests    243/243    Test Run Successful.

muthur-tests: 190 removed, 0 kept (logged an error), 0 could not be removed.
```

Leftovers: `muthur-tests` **0**, `muthur-cli-tests` **0**. 243 is the validated 240 plus Unit C's three
`Release` tests.

The validator's own reproduction, run verbatim against the integrated branch:

```
<root>: examined 4, deleted 3, kept 0, held 1, skipped (too recent) 0, gone 0, failed 0.
  held    <root>\b-locked  (The process cannot access the file '<root>\b-locked\muthur.log' because it is being used by another process.)
child exit code: 0
SURVIVORS: b-locked
```

Against the verdict's "exit 1, no summary at all, `c-clean` and `d-clean` never examined": the run now
completes, prints its counts, names the one directory it could not take, deletes the other three, and leaves
the held one standing.
