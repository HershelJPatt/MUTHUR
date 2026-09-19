# T-29 — The server test suite leaks temp hub directories, silently

> **THIS SPEC IS NOT FROZEN. Do not build from it.**
> It is a handover: analysis that was done, a measurement that was not, and the decision that is still open.
> Whoever picks T-29 up owns freezing it. See **State at handover** at the end for exactly where the line is.

## What the task asks

T-29 as filed asks three things, and the first one gates the other two:

1. What actually holds the handle at `Dispose` time? **Measure it, do not assume.**
2. Should `Dispose` retry briefly, should the suite delete `%TEMP%\muthur-tests` wholesale at assembly
   teardown, or should hubs live somewhere self-cleaning?
3. Whatever the answer: a cleanup that fails must say so, at least once per run, instead of being caught and
   dropped.

Point 3 is not in doubt and is the part I would keep whatever the measurement says. The two bare `catch`
clauses are why nobody noticed for two days, and they are why the count in the task's own title is out by a
factor of three.

## The code

`tests/Muthur.Server.Tests/HubFactory.cs`:

```csharp
protected override void Dispose(bool disposing)
{
    base.Dispose(disposing);
    try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
```

`DataDir` is `Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"))`, so every hub
gets its own directory and there is no collision between parallel hubs. The connection string carries
`Pooling=False` (T-22), so connection pooling is not holding anything.

## Two leads, neither measured

Both predict the same failure and are fixed by the same change, which is what makes the measurement cheap.

### Lead 1 — every hub is disposed synchronously

`grep` for `new HubFactory` across `tests/Muthur.Server.Tests`: every one is `using var hub = new HubFactory()`
or an xUnit `IDisposable` field. **Not one is `await using`.**

`WebApplicationFactory` implements `IAsyncDisposable`, and the async path is the one that properly awaits host
shutdown. The synchronous path disposes a container whose services — `SqliteConnection` among them — are
async-disposable. `Directory.Delete` then runs on the very next line, against a file whose handle may not have
been released yet.

### Lead 2 — requests are still in flight when the hub is disposed

**This one is not a theory; it is the evidence that solved T-41.**

Each leaked directory keeps the hub's own `muthur.log`. Searching them for
`ErrorMiddleware: Unhandled error on` finds 32 hits across twelve endpoints — every one an
`ObjectDisposedException` on `SQLitePCL.sqlite3` or `IServiceProvider`, every one immediately followed by
`Application is shutting down...`. A request inside `Ledger.ReadAsync` or `MutateAsync` when the host disposes
is holding an open `SqliteConnection` on the file `Directory.Delete` is about to remove. That is an
`IOException`, and the bare catch drops it.

T-41 fixed what the *hub* says in that situation (503 `hub_stopping` rather than 500). It did **not** touch
why the suite has requests in flight at disposal, and it did not touch the leak.

### The experiment that would settle it

Cheaper than `handle.exe`, and it tests both leads at once:

1. Count `%TEMP%\muthur-tests` subdirectories.
2. Run `dotnet test tests/Muthur.Server.Tests` once. Count again. That is the per-run leak.
3. Change the disposal to the async path — `await DisposeAsync()` before the delete, which means
   `IAsyncLifetime` on the test classes or an explicit `DisposeAsync` on `HubFactory` — and repeat.

If the count collapses, the handle was the host's own and lead 1 is the answer. If it does not, the WAL and
`-shm` sidecars are next and handle enumeration earns its keep.

## The decision this task must not get wrong

**The leaked directories are evidence, and one of the three options in the task body would destroy it.**

Deleting `%TEMP%\muthur-tests` wholesale at assembly teardown is the tidy-looking answer. It would also have
made T-41 unsolvable: the hub writes `muthur.log` into each data directory, nothing else in this organization
records unhandled server errors, and those 32 log lines are the entire reason T-41's filed hypothesis
(`FileChannel.Validate` leaking an `IOException`) was caught being wrong. The real cause — a request in flight
at shutdown — was read off disk, not reasoned out.

So retention is a decision with a reason behind it, not housekeeping. Some options, none chosen:

- keep the directory only for runs that logged an error, delete the rest;
- keep the last N runs;
- write the hub's log somewhere that outlives its data directory, and then the directories are free to go.

Whoever freezes this spec should say which, and why. **I did not settle it, and it is the reason this spec is
not frozen** — the fix and the retention policy are one decision, because a cleanup that works is also a
cleanup that destroys the logs.

## Numbers, measured rather than guessed

| When | Directories in `%TEMP%\muthur-tests` |
|---|---|
| T-29 filed (2026-09-18 18:02) | 15,691 |
| 2026-09-19 ~02:30 | 45,435 |
| 2026-09-19 ~03:10 | 45,749 |
| 2026-09-19 ~04:00 | 48,509 |

Roughly three thousand in ninety minutes of ordinary work by two or three agents. The task's title says
"100-300 per run"; that is per *suite*, and the organization runs the suite dozens of times an hour.

## State at handover

**Nothing is built. This branch carries this file and nothing else.** `dotnet build` and `dotnet test` were
not run for T-29 because there is no change to run them against.

**Done:**
- The code read: `HubFactory.Dispose`, the `DataDir` scheme, the `Pooling=False` connection string, and the
  fact that every hub in the suite is disposed synchronously (`grep` for `new HubFactory`, no `await using`).
- Both leads above, and the link to T-41, which is real and load-bearing: T-41's root cause (requests in
  flight at host disposal) is very likely this task's root cause too.
- The counts in the table, taken directly.
- The retention argument, which I think is the most valuable thing here and is not in the task body.

**Not done:**
- **The measurement.** The task says "measure it, do not assume", and I did not. I had the numbers and the
  code read but never ran the before/after experiment.
- Any change to `HubFactory` or the test classes.
- The retention decision.
- One-off cleanup of the 48,509 existing directories. **Do not do this before reading the retention section**
  — and if you do it, keep the `muthur.log` files of any run that logged an error.

**What I tried that did not work:**
- Nothing technical failed. What stopped this task was that for a stretch of the session every `dotnet` and
  `git` invocation outside the primary working directory was refused by the sandbox, so the experiment could
  not be run when I had the context for it. That is an environment condition, not a property of the task; the
  next session should simply run step 2 above and see.

**What I would do next, in order:**
1. Run the three-step experiment. It is twenty minutes and it decides the shape of the whole task.
2. Whatever it says, make the cleanup failure *audible* — one line per run naming how many directories could
   not be removed. That is point 3 of the task, it is independent of the measurement, and it is the change
   that stops this recurring.
3. Settle retention explicitly, in the spec, with the T-41 story as the reason. Only then write the cleanup.
4. Ask the founder only if the retention options come out genuinely balanced. They may not: "keep the logs of
   runs that failed" costs almost nothing and preserves the whole benefit.

## Related

- **T-41** (landed) — same root cause, different symptom. Its spec, `specs/T-41-hub-stopping.md`, contains the
  full evidence table of the 32 unhandled errors and is worth reading before starting here.
- **T-22** (landed) — the connection-pool flakiness. Measured across that change and unaffected by it, so this
  predates T-22 and T-22 neither caused nor fixed it.
