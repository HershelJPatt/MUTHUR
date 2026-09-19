# T-54 — `-All` ignores retention, never ownership

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task `scripts/clean-test-temp.ps1` never deletes a hub directory whose `muthur.log` another
process still holds open, whatever the flags say. Today, `-All` together with `-OlderThanMinutes 0` skips the
ownership check along with the retention one, and `Remove-Item -Recurse` then takes `muthur.db` and its
`-wal` before it meets the locked log and throws — half a delete, another agent's database corrupt, reported
to the operator as one `failed` line that reads like "could not delete".

## Context

- `scripts/clean-test-temp.ps1`. The ownership signal already exists and is already explained in the file:
  `FileLoggerProvider` holds `muthur.log` open with `FileShare.ReadWrite`, which refuses our
  `FileShare.Read`, "so a log we cannot read means another process still owns this directory — the one fact
  that should stop us touching it".
- That fact is produced by `Get-LogVerdict`, which is called **inside** `if (-not $All -and ...)`. With
  `-All` it is never asked, so the one fact that should stop us is the one the flag skips.
- `held` is an existing verdict, counter and summary line, meaning exactly "another process owns this".
- T-29's Amendment 3 is the prior art: the age guard was chosen as the defence against this sequence, and it
  holds — a default invocation is safe, which is why the validator reported this as non-blocking.

Constraints that are not obvious:

- The script's contract is that every run ends with its counts and names what it held back or failed on.
- The root has held 49,437 directories. Anything added per directory must be cheap.

## Non-goals

- Changing what `-All` does to **retention**. It still deletes a directory whose log recorded an `Error`.
  That is the flag's whole purpose and it is untouched.
- The age guard, which already works.
- Rewriting the summary. `held` already says the right thing; this makes the script reach it.

## Design

### Ownership is asked first, and always

The log check splits into the two questions it was always answering:

```powershell
if ([IO.File]::Exists($log)) {
    $holder = Get-LogHolder $log          # ownership — asked whatever -All says
    if ($null -ne $holder) { $heldCount++; $heldLines.Add("  held    $dir  ($holder)"); continue }
    if (-not $All) {
        $answer = Get-LogVerdict $log     # retention — skipped by -All, as before
        ...
    }
}
```

`-All` means "ignore what the log recorded", never "ignore that another process is still writing it".

`Get-LogHolder` opens the log with `FileShare.Read` and drops it. It reads nothing: the answer *is* the
open, so it costs one handle per directory rather than a scan, which is what makes it affordable on the
whole root. The `held` branch inside `-not $All` stays — the probe said the log was free a moment ago, and
the owner can have opened it since.

### A handle that was never let go

Found while verifying the above, on `main` and not introduced here: `Get-LogVerdict` iterated
`[IO.File]::ReadLines($LogPath)` and `return`ed out of the loop on the first `Error` line. That enumerator
is lazy and its handle survives the early return until a collection, so **the script held the log itself**.
The next pass in the same process then read that directory as owned by another process — and before this
task's fix, under `-All`, that false ownership signal was not consulted at all and the directory was
deleted while the script's own handle was open, producing the partial delete with nobody else involved.

It reads with an explicit `StreamReader` disposed in a `finally` now. Stopping at the first match is still
the point; letting go is too.

## Units of work

### Unit A — ownership before retention
- **Files:** `scripts/clean-test-temp.ps1`
- **Does:** `Get-LogHolder`; the restructured log check; the header sentence about what `-All` overrides.
- **Depends on:** nothing.

### Unit B — the leaked handle
- **Files:** `scripts/clean-test-temp.ps1`
- **Does:** `Get-LogVerdict` reads through a disposed `StreamReader`.
- **Depends on:** nothing.

## Verification

Every command here runs with no browser, no GUI and no human. Nothing in this task can only be seen by eye,
so it is not marked `attended`. `dotnet build` and `dotnet test` are run to show the solution is untouched.

```
dotnet build
dotnet test
```

Then, against throwaway roots, holding `muthur.log` exactly as `FileLoggerProvider` does
(`FileMode.Create, FileAccess.Write, FileShare.ReadWrite`):

1. A live hub, both guards off → `held 1`, `failed 0`, `muthur.db` and `muthur.db-wal` both still present.
2. The same hub once the holder has released it → `deleted 1`.
3. A log with an `Error` line: default keeps it; `-All` deletes it — retention still overridden.
4. 3 run twice **in one process**: the second run must still delete, not report the directory as held.

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 77 Cli, 340 Server — all green.

### The four shapes, run

```
1. live hub, -All -OlderThanMinutes 0
   examined 1, deleted 0, kept 0, held 1, ... failed 0.
     held    ...\locked  (The process cannot access the file '...\locked\muthur.log' ...)
   muthur.db still present? True      muthur.db-wal still present? True
2. the same hub, holder released
   examined 1, deleted 1, ... failed 0.        hub still there? False
3. a log with an Error line
   default:  examined 1, deleted 0, kept 1, ... hub still there? True
   -All:     examined 1, deleted 1, ...        hub still there? False
4. 3 run twice in one process
   kept 1, then deleted 1.                     hub still there? False
```

### The defect reproduced on the code as it was

```
PRE-FIX: both guards off against a live hub
   examined 1, deleted 0, kept 0, held 0, ... failed 1.
     failed  ...\locked  (The process cannot access the file '...\locked\muthur.log' ...)
   muthur.db still present? False
```

Byte for byte the validator's report: the database gone, the run reported as one `failed` line.

### And the handle leak, isolated before it was fixed

Shape 4 was what found it. On `main`, run twice in one process:

```
   examined 1, deleted 0, kept 1, ... failed 0.
   examined 1, deleted 0, kept 0, held 0, ... failed 1.
     failed  ...\errored  (The process cannot access the file '...\errored\muthur.log' ...)
   hub still there? True
```

`-All` alone in a fresh process deletes it correctly, which is what rules out this task's own change as the
cause: the second run only fails because the *first* run's `ReadLines` enumerator was still holding the log.
That is a defect on `main` and it is fixed here rather than left, because Unit A's whole job is to trust that
signal, and a signal the script can trigger against itself is not one worth trusting.
