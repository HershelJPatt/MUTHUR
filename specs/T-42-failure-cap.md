# T-42 — The validation-failure cap counts the current round, not the task's whole history

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, the conductor stops restaffing a task that keeps failing **the same submission**, and
resumes staffing it the moment its owner fixes it and marks it implemented again. A task rejected three
times on three commits — each rejection finding a real defect, each defect fixed — is no longer permanently
excluded from unattended validation.

## The defect, and the live case that proves it

Both cap sites count every `validation.failed` event a task has ever had:

```csharp
.Where(e => e.Type == "validation.failed" && e.TaskId != null && ids.Contains(e.TaskId!.Value))
.GroupBy(e => e.TaskId!.Value).Select(g => new { TaskId = g.Key, Count = g.Count() })
```

The ledger is append-only, so that count can only rise. Nothing resets it when the owner fixes the task and
resubmits. Once a task reaches `ConductorMaxAttempts` failures it can never be conductor-validated again,
however completely it has since been repaired.

**T-13's own ledger is the demonstration**, and it is the task this spec was written alongside:

| Seq | Event | |
|---|---|---|
| 325 | `task.implemented` | |
| 437 | `validation.failed` | conductor identity shared per role |
| 537 | `task.implemented` | fixed |
| 656 | `validation.failed` | truncation collided two long role keys |
| 689 | `task.implemented` | fixed |
| 696 | `validation.failed` | digest collision, brute-forced by the validator |
| 731 | `task.implemented` | fixed |

Three failures, on three different commits, each finding a different real defect, each fixed before the next
attempt. Since the most recent `task.implemented` there have been **zero** failures — and the conductor will
still not staff it, because it counts all three.

That is the validation loop working exactly as designed, punished as though it were a task going nowhere.
And the punishment lands hardest on the tasks that needed the most care: the only way such a task can ever
ship is an attended session, which is the opposite of what this organization is for.

## The decision, and why it is not the founder's

The task filed three candidate fixes and chose none. This spec chooses the first, and records why the other
two lose, because a later reader will want to know:

- **Chosen: count only failures after the task's most recent `task.implemented`.** A resubmission is the
  owner asserting the fault is fixed; the cap's own comment says it exists for "a task that has failed too
  many times", and the thing it is actually trying to detect is *a submission that is not progressing*. This
  restores the stated intent rather than changing policy, which is why it does not need a founder decision.
  It is also a `Seq >` on a query that already exists.
- **Rejected: count consecutive failures on the same branch head.** More precise in principle, and the
  branch head is not recorded per failure, so it would have to be threaded through. More to get wrong for a
  distinction that "since the last resubmission" already draws.
- **Rejected: make this cap half-open like the other two.** The other two caps stall on causes fixed
  *outside* the hub — a missing CLI, a cleared quota — so a timed probe is the only way back. This cap's
  cause is fixed *inside* the hub, by the owner, deliberately, and that act is already an event. A timed
  probe would restaff a genuinely stuck task with nobody having changed anything, which is the behaviour the
  cap exists to prevent.

**The cost implication, stated rather than buried:** after this change a task can consume
`ConductorMaxAttempts` sessions per resubmission, with no ceiling across rounds. That is bounded by an owner
choosing to fix and resubmit each time — not a loop — and every exhausted round still messages the founder,
so the spend stays visible. If that proves too loose, the ceiling belongs in a later task with a number the
founder picks, not in a silently permanent exclusion.

## Context

- `src/Muthur.Server/Services/ConductorService.cs` — two sites, and they must agree:
  - `PlanAsync` (~line 127): the `failures` dictionary, used as `failures.GetValueOrDefault(task.Id) >= options.ConductorMaxAttempts`.
  - `EscalateExhaustedAsync` (~line 317): the same count, used to message the founder once.
- `_escalated` is an in-memory `HashSet<int>` of task ids, so a task is escalated at most once per hub
  process. With per-round counting that is wrong: a second exhausted round must be able to escalate again.
- `LifecycleService.ImplementedAsync` records `task.implemented`; `VerdictAsync` records `validation.failed`
  and returns the task to its owner. A failed verdict always leaves `Validating`, so a task only re-enters
  the conductor's view through a fresh `task.implemented`.
- `LedgerEvent.Seq` is the monotonic ordering already used elsewhere in this file
  (`g.OrderByDescending(e => e.Seq).First()`).

Constraints that are not obvious:

- Reads go through `Ledger.ReadAsync`; `EscalateExhaustedAsync` is inside a `MutateAsync`.
- Time comes from the injected `TimeProvider`.
- The in-memory counters are guarded by `lock`; keep the lock scopes as tight as they are and never hold one
  across an `await`.
- Warnings are errors.

## Non-goals

- Changing `ConductorMaxAttempts`, or making it per-project.
- Any change to the launch-failure or no-verdict caps, which are correct and have the right shape.
- Any change to what a failed verdict does to a task.
- A ceiling on total sessions across rounds. Named in the cost paragraph above as a possible later task.
- Purging or rewriting `validation.failed` events. The ledger is append-only and stays that way; this task
  changes how it is *read*.

## Design

### One helper, used by both sites

In `ConductorService`, a single query giving the failures-this-round count per task, so the two sites cannot
drift:

```csharp
/// <summary>
/// Failures since each task's most recent `task.implemented`. The ledger is append-only, so counting a
/// task's whole history means a task rejected `ConductorMaxAttempts` times could never be staffed again,
/// however completely it was then fixed. What the cap is for is a submission that is not progressing.
/// </summary>
private static async Task<Dictionary<int, int>> FailuresThisRoundAsync(MuthurDb db, IReadOnlyList<int> taskIds, CancellationToken ct)
```

It resolves, per task, the greatest `Seq` of a `task.implemented` event, then counts `validation.failed`
events with a greater `Seq`. A task with no `task.implemented` at all counts from zero — it cannot be in
`Validating` without one, but the query must not throw on it.

Two round trips are fine; do not try to make it one at the cost of clarity.

`PlanAsync` and `EscalateExhaustedAsync` both call it and drop their own `failures` queries.

### Escalating again on a later round

`_escalated` becomes `Dictionary<int, long>`: task id → the `Seq` of the `task.implemented` that opened the
round in which it was escalated. A task escalates when its round count reaches the cap **and** the recorded
seq for it is not this round's seq. That way:

- the same exhausted round still messages the founder exactly once;
- a later round that also exhausts messages them again, which is correct — it is new information.

### What the founder is told

The message currently reads *"has failed validation {n} times and the conductor has stopped restaffing it"*.
`{n}` is now a per-round number, so it must say so, otherwise a founder who remembers six failures reads
three and distrusts the number:

> `{T-n} "{title}"` has failed validation `{n}` times since it was last submitted, and the conductor has
> stopped restaffing it. Fix it and mark it implemented again, re-spec it, cancel it, or raise
> `Muthur:ConductorMaxAttempts`.

"Fix it and mark it implemented again" is new and is the point of the task: there is now a way back that
does not involve the founder changing configuration.

The per-verdict lines below it should list **this round's** failures, for the same reason.

## Units of work

### Unit A — the whole task
- **Files:** `src/Muthur.Server/Services/ConductorService.cs`; `tests/Muthur.Server.Tests/ConductorTests.cs`.
- **Does:** everything above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. **The live case.** A task with `ConductorMaxAttempts` failed verdicts, then fixed and marked implemented
     again, **is planned**. Build it through the real lifecycle — implemented, fail, implemented, fail,
     implemented, fail, implemented — rather than by inserting events, so the test would have caught the
     original defect. Mutation-check it: restore the whole-history count and confirm it fails.
  2. A task with `ConductorMaxAttempts` failures **in the current round** is not planned. The cap still works.
  3. The founder is messaged once for an exhausted round, and **again** for a later exhausted round after a
     resubmission — two messages, not one, and the second names the later round's count.
  4. The message says "since it was last submitted" and offers marking it implemented again.
  5. The launch-failure and no-verdict caps still behave exactly as they did; their existing tests pass
     untouched.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub. A stand-in `claude` on PATH
that prints `{"result":"ok","is_error":false}` exercises the launch path without spending anything; the
validator brief documents the technique.

- Drive a task through implemented → fail → implemented → fail → implemented → fail, with
  `Muthur:ConductorMaxAttempts` at 3. Confirm the founder is told once and `conductor status` stops staffing it.
- Mark it implemented a fourth time. **`muthur conductor status` staffs it again** — this is the behaviour
  the whole task is for, and it is the step to see fail on `main`.
- Let that round fail three times too: the founder is told a second time, and the count reads 3, not 6.

## Out of scope / follow-ups

- A ceiling on sessions across rounds, if per-round turns out to be too loose. It needs a number the founder
  picks.
- `_escalated` is in-memory, so a hub restart re-arms escalation for an already-escalated round and the
  founder may be told twice. Pre-existing, unchanged here, and the same is true of the other two caps.
- Nothing records which branch head a verdict was given on, which is why the rejected "same branch head"
  option was expensive. If per-round counting proves too coarse, recording that is the enabling change.
