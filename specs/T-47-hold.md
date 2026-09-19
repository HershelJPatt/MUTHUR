# T-47 — A hold, recorded and visible

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task an orchestrator can say "do not land this yet, and here is why" in a way the next lander
sees. `muthur task hold T-n --reason "…"` records the hold with who placed it and when; the board shows it
and its age; `muthur task land` **warns and proceeds**, naming the holder, the age and the reason verbatim;
and the override is a ledger event, so the question this task was filed to answer becomes countable.

The measurement already on this branch is why: 31 lands, zero observable disagreements, and the incident this
task was filed about indistinguishable from the thirty that were fine — because holding was a silent act.
Founder request #16 chose this shape over doing nothing precisely so the next round of the question can be
answered with a number instead of a sketch.

## Context

- `src/Muthur.Core/Entities/Entities.cs` — `WorkTask`; `AttendedReason` (line 56) is the nearest existing
  field, and `TaskService.SetAttendedAsync` (line 173) is the shape to copy: owner-or-founder on an owned
  task, anyone identified on an unowned one, one event to set and a different one to clear.
- `src/Muthur.Server/Services/LifecycleService.cs` — `LandAsync`, the `Landed` arm at line 347.
- `src/Muthur.Contracts/Tasks.cs` — `TaskDto`, ending at `AttendedReason`.
- `src/Muthur.Server/Components/Shared/TaskCard.razor` — `pill-blocked` is the existing "waiting on a person"
  pill; `Format.Age` is how every other age on the board is rendered.
- `src/Muthur.Server/MuthurOptions.cs` — `ClaimLeaseMinutes = 30` and `RoleLeaseMinutes = 30` are the lease
  idiom and the naming style.
- `src/Muthur.Data/Migrations` — `dotnet ef migrations add <Name> -p src/Muthur.Data -s src/Muthur.Data -o Migrations`.
  T-53's lesson applies: a migration that rebuilds a table is written out rather than scaffolded. Three added
  columns need no rebuild.

Constraints that are not obvious:

- Every state change goes through `Ledger.MutateAsync` and records an event in the same transaction.
- Time comes from the injected `TimeProvider`.
- Everything crossing HTTP is registered in `MuthurJsonContext`; the CLI is Native AOT.
- The board card is not inside a `<Virtualize>`, so the pill is in the prerendered HTML.
- Warnings are errors.

## Non-goals

- **A lock.** `land` warns and proceeds, never refuses. That is the founder's T-16 answer applied to
  intentions, restated in #16: "the last thing this organization needs is another gate that can hold work
  still."
- **Announcing a pending land on the bus** (sketch B) — rejected on this branch's own numbers and by the
  founder: latency on all 31 lands to catch a case that has happened once.
- Holding anything other than a task — no hold on a role, a project or a branch.
- Deciding *between* two orchestrators. A hold is information, not an adjudication.

## Design

### The hold

Three fields on `WorkTask`, and three on `TaskDto` after `AttendedReason`:

```csharp
public string? HoldReason { get; set; }
public string? HoldBy { get; set; }             // the agent name, captured when it was placed
public DateTimeOffset? HoldExpires { get; set; }
```

`POST /tasks/{id}/hold` with `HoldRequest(string? Reason)`, and `TaskService.SetHoldAsync` built like
`SetAttendedAsync`:

- **Any identified agent may hold any task**, owned or not — unlike `attended`, and deliberately: the whole
  point is that somebody *other than the owner* has an intention about it. The founder may always.
- A null or blank reason is the clear, the same wire convention `attended` uses, and the CLI refuses a
  missing `--reason` before sending anything so "I forgot the flag" cannot read as "lift it".
- Setting records `task.held` with `{ reason, by, expires }`; clearing records `task.hold_cleared` with
  `{ was, by }`.

### It expires, and its age is on the board

`HoldExpires` is `now + HoldMinutes`, default **120**. Two hours spans several thirty-minute orchestrator
rounds — long enough that a hold placed in one iteration is still there for the next — and clears itself
inside the working session that placed it. A permanent note nobody cleared is text the next lander learns to
skip, which pays for the mechanism and keeps none of the signal.

A hold is **live** when `HoldExpires > now`. An expired hold is not shown, not warned about and not counted.
It is left on the row rather than swept, so the record and the ledger agree about what was placed and when.

`TaskCard` renders, while live:

```razor
<span class="pill pill-blocked">held by @task.HoldBy · @Format.Age(placed, now)</span>
```

where `placed` is `HoldExpires - HoldMinutes`. No new CSS.

### Land warns, names, and records

In `LandAsync`'s `Landed` arm, read the live hold before the state change. When there is one:

- The `task.landed` (or `task.pr_opened`) payload gains `overrodeHold = { by, reason, placedAt }`.
- A second event, `task.hold_overridden`, carries the same. This is the event #16 asked to be countable, and
  a field inside another event is harder to count than a row of its own.
- The founder and the holder are each told, in the words #16 specified — who, when, and the reason verbatim:

  `top-right landed T-33, which bottom-left held 20 minutes ago: "it collides with T-23's migration".`

`land` does not refuse, does not pause, and does not clear the hold. The hold stands until it expires or
somebody clears it, because the next lander should see that this task has already been landed over once.

## Units of work

### Unit A — the hold
- **Files:** `src/Muthur.Core/Entities/Entities.cs`, `src/Muthur.Data/Migrations/…`,
  `src/Muthur.Contracts/Tasks.cs`, `src/Muthur.Contracts/MuthurJsonContext.cs`,
  `src/Muthur.Server/Services/TaskService.cs`, `src/Muthur.Server/Services/Mapper.cs`,
  `src/Muthur.Server/Api/TaskEndpoints.cs`, `src/Muthur.Server/MuthurOptions.cs`
- **Does:** the three fields, `HoldMinutes`, `SetHoldAsync`, the route, the DTO and the migration.
- **Depends on:** nothing.

### Unit B — land warns
- **Files:** `src/Muthur.Server/Services/LifecycleService.cs`
- **Does:** the two events and the two messages, per Design. Never refuses.
- **Depends on:** Unit A.

### Unit C — the CLI and the board
- **Files:** `src/Muthur.Cli/Commands/TaskCommands.cs`, `src/Muthur.Server/Components/Shared/TaskCard.razor`
- **Does:** `muthur task hold <id> --reason … | --clear`, refusing a missing reason before sending; the pill.
- **Depends on:** Unit A.

### Unit D — the tests
- **Files:** `tests/Muthur.Server.Tests/`
- **Does:**
  - A hold by a non-owner is accepted; the DTO carries reason, holder and expiry; `task.held` is recorded.
  - `--clear` lifts it and records `task.hold_cleared` naming what it was.
  - A live hold does not stop a land: the task reaches `done`, and `task.hold_overridden` is recorded with
    the holder, the reason verbatim and when it was placed.
  - The founder and the holder are each told, and the message contains the reason verbatim.
  - An **expired** hold does not warn, is not counted and is not shown.
  - The board shows the pill with the holder and the age while live, and does not once it has expired.
- **Depends on:** Units A–C.
- **Acceptance:** each test fails with its unit reverted.

## Verification

Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor, and the board card is in the prerendered HTML. Nothing here can only be seen by eye, so it is
not marked `attended`.

```
dotnet build
dotnet test
```

And on an installed build against a scratch hub — never the live one:

```
muthur task hold T-1 --reason "collides with T-2's migration" --as-agent other
muthur task land T-1 --as-agent owner      # exit 0
muthur log --since 0 | Select-String 'hold_overridden'
```
