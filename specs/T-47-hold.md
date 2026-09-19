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
- `BoardPanel` **does** render its cards inside a `<Virtualize>`, which emits nothing to a fetch. The task
  detail page does not, and is where a lander deciding whether to override is reading anyway.
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
  - The task page shows the holder, the age and the reason while live, and shows nothing once it has expired.
- **Depends on:** Units A–C.
- **Acceptance:** each test fails with its unit reverted.

## Verification

Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor. Nothing here can only be seen by eye, so it is not marked `attended`, and there is no
`needs:` line.

The hold is asserted on **`/tasks/T-n`**, not on the board. `BoardPanel` renders its cards inside a
`<Virtualize>`, which emits nothing during a prerender — settled on T-16, and true again here. The card
still carries a pill for a human reading the board; the detail page is what a test and a lander both read.

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

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 159 Cli, 388 Server — all green.

The migration adds three columns and rebuilds nothing on the way up, so it produces no
`PRAGMA foreign_keys` warning — T-13's lesson, checked rather than assumed.

### One claim in this spec was wrong, and the test found it

The Context section first said the board card "is not inside a `<Virtualize>`, so the pill is in the
prerendered HTML". It is, and it isn't. `The_board_says_who_holds_it_and_for_how_long` failed on exactly
that, which is the same limit T-16 recorded and I restated from memory instead of checking.

The fix is not to weaken the assertion. The hold now also renders on `/tasks/T-n`, which is not virtualized
and is the page a lander is actually reading when they decide whether to override — so the test asserts the
thing a human would read, and the card keeps its pill for the board. Both sections above are corrected.

### What is countable now

```
task.held              { reason, by, expires }
task.hold_cleared      { was, by }
task.hold_overridden   { by, reason, placedAt, landedBy }
task.landed            { …, overrodeHold: { by, reason, placedAt } }
```

`task.hold_overridden` is the row request #16 asked for: "the event you actually wanted counted — a hold
that a land later overrode — exists in the ledger, and the next time someone asks this question they can
answer it with a number instead of a sketch."

## Amendment after the first validation round (2026-09-19, top-right)

`conductor-validator-t-47` failed this with two findings, and the first is the founder's own condition
unmet:

> installed CLI land gives no explicit hold warning/age, and self-holder receives no override notification
> (founder only).

Request #16 said "the warning on land must name who held it, when, and the reason verbatim". I put that in
the ledger and in two messages and called it done — but the person doing the overriding gets back a JSON
task and no sentence, and messages arrive in an inbox they are not reading at that moment. A warning nobody
is shown is not a warning.

**`muthur task land` now says it on stderr**, where `role define --brief-file` already puts its provenance
note, so stdout stays the JSON an agent parses:

```
Warning: bottom-left held T-1 until 14:32 — "it collides with T-23's migration" — and you landed it anyway.
Clear it when it stops being true: muthur task hold T-1 --clear
```

Derived from the response the land already returns, needing no contract change: a land deliberately does not
clear the hold it overrode, so a task that comes back `done` still carrying an unexpired hold is one that was
landed over.

**That closes the second finding too.** The hub still does not message you about your own act — a message
telling you what you just did is noise in an inbox. But the CLI line is printed whether or not the lander is
the holder, so somebody who holds a task and then lands it now sees that the two halves of what they did were
in tension. That was previously silent in every channel, which is the part that was wrong.

### Tests

`TaskHoldWarningTests` — the warning names the holder, the reason verbatim and the way to clear it; an
expired hold, an unheld task, a response that is not a completed land (a PR-mode project leaves it
`validated`), a refusal, unparseable JSON and an empty body all say nothing rather than throwing.

### Not exercised, and not worked around

The validator could not test landing at the exact moment of expiry: "approval review rejected checkout of the
disposable default branch under the user prohibition; no workaround attempted." That was the right call —
the boundary is covered by `An_expired_hold_is_not_a_hold` at the service and by
`A_hold_that_has_expired_is_not_warned_about` in the CLI, both on a fake clock, which is where a boundary
belongs.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 180 Cli, 389 Server — all green.
