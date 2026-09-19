# T-35 — The hub stops asking for a Windows Event Log it never wanted

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the hub and its test suite register only the logging providers MUTHUR actually uses, so a
session with ordinary user rights — or any sandbox that denies the Windows Event Log — can run `dotnet test`
and `muthur up` without a workaround nobody told it about. And `muthur doctor` reports whether the log file
the hub is configured to write is actually writable, so the next person meets that fact as one line rather
than as a wall of failures.

## The defect, and why it is worth a task

Two independent agents hit this hours apart, on different tasks:

- A `codex` worker building T-5 could not run `dotnet test` until it set
  `Logging__EventLog__LogLevel__Default=None`, and reported it as a sandbox workaround.
- `conductor-validator` reported T-15 **blocked**: "sandbox server tests hit Windows Event Log access denied
  (103 failures)".

**Nothing in `src/` asks for it.** `Startup.AddMuthur` line 23 adds exactly one provider —
`FileLoggerProvider`, writing into the data directory. The Event Log provider arrives from
`WebApplication.CreateBuilder`'s Windows defaults, which register `EventLog` whether or not the process can
write to it. `AddProvider` *adds to* those defaults rather than replacing them.

A grep of `src/` and `tests/` finds no reference to `EventLog` outside transitive package entries in
generated `deps.json`. Nothing in this repository reads it, and the hub's own record is `muthur.log` plus
the ledger.

The reason this is worth fixing rather than documenting: it is a **silent prerequisite**. The kit tells a
validator to run `dotnet test` and bring up a scratch hub. Neither says "and you will need Event Log
access". An unattended validator that hits it cannot tell a genuine failure from an environment it was never
told about — the T-15 verdict is exactly that, 103 failures that say nothing about the task under test.

## The decision, and why it is not the founder's

The task asks whether to remove the provider outright or only on sandboxed paths. **Outright.** Conditional
logging would mean the hub behaves differently depending on how it was started, which is a worse property
than the one being fixed, and there is no condition to test against — a sandbox does not announce itself.
Nothing reads the Event Log, so removing it loses no signal anyone consumes. That is an engineering call
against an established fact, not a policy choice.

## Context

- `src/Muthur.Server/Infrastructure/Startup.cs` line 23 — the single `AddProvider` call, and the whole of
  the change.
- `WebApplication.CreateBuilder` registers Console, Debug, EventSource and (on Windows) EventLog before
  `AddMuthur` runs.
- `tests/Muthur.Server.Tests/HubFactory.cs` brings up a real `WebApplicationFactory<Program>`, so the test
  host runs the same `AddMuthur` and inherits the same providers. **One fix covers both**; that is where the
  103 failures came from.
- `src/Muthur.Server/Services/DoctorRepoCheck.cs` is the shape to copy for the new doctor check:
  an `IDoctorCheck` returning `CheckDto`s, registered in `Infrastructure/Startup.cs` in `AddMuthur`.
- `MuthurEnvironment.LogFile` is `muthur.log`; the hub writes it at `{DataDir}/muthur.log`.
- T-14's file-probe lesson applies to the new check: proving a parent directory exists is not proving the
  destination is writable. `FileChannel.ProbeAsync` in `OutboundChannels.cs` shows what that looks like done
  properly.

Constraints that are not obvious:

- Doctor performs no mutation and records no ledger event; it reads through `Ledger.ReadAsync` where it
  needs the database, and this check needs only `MuthurOptions`.
- Time comes from the injected `TimeProvider`.
- Warnings are errors.

## Non-goals

- Any change to `FileLoggerProvider` itself, or to what the hub logs.
- Adding a configuration switch for logging providers. If a founder ever wants the Event Log back, that is a
  task with a reason behind it; adding the knob now is inventing a requirement.
- `muthur down` returning 0 while the process is still alive. The same validator reported it; it is a
  different defect and belongs in its own task if it reproduces.
- Changing what the kit tells validators. Once the prerequisite is gone there is nothing to document.

## Design

### The providers

`Startup.AddMuthur`, replacing line 23:

```csharp
// CreateBuilder registers Console, Debug, EventSource and — on Windows — EventLog. The Event Log is the
// problem: the hub never reads it, nothing in this repository does, and a session with ordinary user rights
// or a sandbox that denies it fails at a layer that has nothing to do with what MUTHUR does. Start from
// nothing and add back the two that have a reader.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDir, MuthurEnvironment.LogFile)));
```

Console stays because a founder running the server in the foreground reads it. The file provider stays
because it is the hub's record, and `muthur doctor` and every validator's evidence point at it. Debug and
EventSource go: nothing in this repository or its tooling consumes either.

### The doctor check

New `src/Muthur.Server/Services/DoctorLoggingCheck.cs`, `IDoctorCheck`, category **`logging`**, subject
`muthur.log`. One check, no database access — it takes `MuthurOptions`.

It proves what writing a log line actually needs, the way T-14's file probe does, and leaves nothing behind:

| Condition | Status | Detail |
|---|---|---|
| the path is an existing directory | `Fail` | `{path} is a directory; the hub cannot write its log there.` |
| opening it for append throws `IOException`, `UnauthorizedAccessException` or `NotSupportedException` | `Fail` | `The hub cannot write {path}: {message}. Its own log is the record every validator reads.` |
| otherwise | `Ok` | `Writing to {path}.` |

Open with `new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)` and dispose without
writing a byte. **Do not delete the file afterwards** — the hub is writing to it, and T-14 learned that a
probe which deletes what it created can discard what a concurrent writer appended.

**This check ignores `DoctorContext.Probe` and always runs.** `Probe` gates network calls — a `gh` invocation,
an HTTP request to Discord — because those are slow, cost someone's quota, and should not fire on the
dashboard's timer. Opening a local file for append and closing it is none of those things, and the panel
calls `RunAsync(probe: false)`, so gating it would mean the one check that says whether the hub can keep a
record is the one check the dashboard never runs. Deliberate, not an oversight.

The path is `Path.Combine(options.DataDir, MuthurEnvironment.LogFile)`, and it may appear in the detail:
a log path is not a credential, unlike an outbound target's address.

Register it in `Infrastructure/Startup.cs` beside the other `IDoctorCheck`s. `DoctorService` orders by a
fixed category list; add `"logging"` to that list, after `"secret"` and before `"ingest"` — a hub that
cannot write its own log should be read before anything that depends on reading it.

### One consequence to expect

This is the first doctor check that emits a line on **every** hub, including one with no projects, no roles
and no targets. Two existing tests assert an empty report — `DoctorTests`' empty-report test and
`DashboardOperationsTests`' panel empty state. Both must be updated, not loosened: keep what they were
actually for (an unauthenticated read, the injected clock's `At`, the panel's empty-state markup) and reach
the empty state by removing the registered checks rather than by asserting there are none.

## Units of work

### Unit A — the whole task
- **Files:** `src/Muthur.Server/Infrastructure/Startup.cs`; new
  `src/Muthur.Server/Services/DoctorLoggingCheck.cs`; `src/Muthur.Server/Services/DoctorService.cs` (the
  category order); `tests/Muthur.Server.Tests/`.
- **Does:** everything above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. **The defect itself.** Resolve `ILoggerFactory`'s providers from a running `HubFactory` hub and assert
     none is an Event Log provider — and that the file provider is present, so the test cannot pass by the
     hub having no logging at all. Match on the provider's type name rather than referencing the
     `Microsoft.Extensions.Logging.EventLog` package, which this project does not depend on directly.
     Mutation-check it: restore the plain `AddProvider` line and confirm it fails **on Windows**.
  2. A healthy hub reports `logging` `ok` naming its log path.
  3. A hub whose log path is an existing directory reports `fail` — drive `DoctorLoggingCheck` directly
     with a `MuthurOptions` pointing at a temp directory containing a directory named `muthur.log`.
  4. The probe leaves the existing log's contents untouched: write a line, run the check, assert the bytes
     are unchanged and the file still exists.
  5. `GET /api/v1/doctor` includes the `logging` check, ordered before `ingest`.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t35
$env:MUTHUR_HOME = "$PWD/artifacts/t35-home"; $env:MUTHUR_URL = "http://127.0.0.1:7450"
./artifacts/t35/muthur.exe up
./artifacts/t35/muthur.exe doctor --pretty
```

- The hub starts and `doctor` shows a `logging` check at `ok` naming `{MUTHUR_HOME}/muthur.log`.
- `{MUTHUR_HOME}/muthur.log` still contains the hub's startup lines after `doctor` has run — the probe
  appended nothing and truncated nothing.
- **The point of the task, for a validator to confirm rather than take on trust:** on a machine or sandbox
  where the Windows Event Log is denied, `dotnet test` and `muthur up` both work with no
  `Logging__EventLog__LogLevel__Default=None` and no other workaround. If the validator's environment
  happens to permit the Event Log, say so — that means this step was not exercised, not that it passed.

## Out of scope / follow-ups

- `muthur down` returning 0 while the hub's process is still alive, reported by the same validator on T-15.
- Nothing warns when the log file grows without bound. The hub is a local process that is off for hours and
  the file is small, so this is a note rather than a plan.
- If a founder ever wants the Event Log back, it returns as one line plus a reason — not as a configuration
  switch added on suspicion.
