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

## The founder's decision

Request #11, answered 2026-09-19. Quoted, because the addition is theirs and the reasoning matters:

> **A, with one addition that gives it the ceiling you are right to want.**
>
> A is right because the cap's purpose was "stop spending on a pair that is not progressing", and a
> resubmission is the owner asserting progress. T-13 is the proof: three failures on three different commits,
> each finding a different defect, each fixed before the next attempt. That is the loop working, and the cap
> punished it.
>
> Your objection to A - no ceiling on total spend - is the real one, and here is the addition that answers it
> without giving up convergence. **Record the branch head at each `task implemented`, and only let a
> resubmission buy a new round if the head actually moved.** An owner who resubmits the identical commit is
> the pathology the cap was built for, and it is cheap to detect ... Converging tasks are unaffected; a task
> that spins on one commit stalls immediately rather than after three more sessions.

They rejected C on the two costs the implementer found (an owner lifting its own stop is not a stop, and
overloading the attended badge), and B because losing the sentence *"the conductor has stopped restaffing
it"* costs them the thing that tells them whether to expect more sessions.

They also recorded that the implementer's correction changed how they read their own task: they wrote T-42
believing three failures could accumulate inside one round. They cannot.

### What the number means now, which is the check that was missing twice

`failures` is still "times this task has ever been failed". The rule reads: **a task failed at least
`ConductorMaxAttempts` times is retried only when its code actually changed.** Two consequences worth
stating rather than discovering:

- **A converging task past the cap generates no founder message at all.** The stall is the notification, and
  the stall now requires an unmoved head. That is a deliberate change from the wording of option A as it was
  put to the founder ("every failure at or past the cap tells you again"), and it follows from their
  "converging tasks are unaffected". If they want a per-failure signal as well, that is a separate ask.
- **The ceiling is real but conditional.** One task can still consume unlimited sessions across rounds - as
  long as someone keeps changing the code between them. What it can no longer do is consume them while
  standing still.

## Context

- `src/Muthur.Server/Services/ConductorService.cs` — two sites, and they must agree:
  - `PlanAsync` (~line 127): the `failures` dictionary, used as `failures.GetValueOrDefault(task.Id) >= options.ConductorMaxAttempts`.
  - `EscalateExhaustedAsync` (~line 317): the same count, used to message the founder once.
- `_escalated` is an in-memory `HashSet<int>` of task ids, so a task is escalated at most once per hub
  process. With per-round counting that is wrong: a second exhausted round must be able to escalate again.
- `LifecycleService.ImplementedAsync` records `task.implemented` and calls `lander.BranchExistsAsync` first;
  `VerdictAsync` records `validation.failed` and returns the task to its owner. A failed verdict always
  leaves `Validating`, so a task only re-enters the conductor's view through a fresh `task.implemented`.
  **Follow that through before designing anything here:** it means a round can hold at most one failure, so
  "three failures" has always meant three resubmissions. An earlier draft of this spec counted failures
  *since* the last `task.implemented` and would have produced a constant zero, turning the cap into dead
  code. The implementer proved it and refused to build it.
- `src/Muthur.Server/Services/GitLander.cs` — `ITaskLander`, and `GitAsync`, which is how this codebase
  shells out to git. `BranchExistsAsync` already runs the `rev-parse` this task needs and throws the answer
  away.
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
- Any change to the launch-failure or no-verdict caps. The launch-failure cap is correct. The no-verdict cap
  is **not** correct on a multi-validator project — when one validator fails a task, its siblings' rounds end
  with their rows still `Pending`, so they are charged for a round that ended correctly and the founder is
  told the wrong cause. That is a separate, pre-existing defect with its own task; it is out of scope here,
  and the earlier claim in this spec that both caps "have the right shape" was wrong.
- Any change to what a failed verdict does to a task.
- A ceiling on total sessions across rounds. Named in the cost paragraph above as a possible later task.
- Purging or rewriting `validation.failed` events. The ledger is append-only and stays that way; this task
  changes how it is *read*.

## Design

### Recording the head

`LifecycleService.ImplementedAsync` already calls `lander.BranchExistsAsync` before it records anything.
Replace that with a call that returns the head, so one git call does both jobs:

```csharp
// ITaskLander
/// <summary>The commit at the tip of <paramref name="branch"/>, or null when the branch does not exist.</summary>
Task<string?> BranchHeadAsync(Project project, string branch, CancellationToken ct = default);
```

`GitLander` implements it with `rev-parse --verify --quiet refs/heads/{branch}`, which is exactly what
`BranchExistsAsync` already runs - it only discarded the output. Keep `BranchExistsAsync` if anything else
uses it; if nothing does, delete it rather than leave two ways to ask the same question.

`ImplementedAsync`'s `branch_missing` rule becomes "head is null". The event gains the head:

```csharp
m.Record("task.implemented", task.Id, new { branch, head, spec = specPath, validators = required });
```

Nothing else stores it. The ledger is the right home: this is per-round history, and a column on the task
would hold only the latest and lose the comparison the rule needs.

### Reading it back

Both cap sites need, per task: the total `validation.failed` count, and the heads of the two most recent
`task.implemented` events. One helper so they cannot drift:

```csharp
/// <param name="Failures">Times this task has ever been failed - the ledger is append-only, so this only rises.</param>
/// <param name="RoundSeq">Seq of the most recent `task.implemented`: the round this task is in now.</param>
/// <param name="HeadMoved">Whether that round's branch head differs from the round before it.</param>
private sealed record CapState(int Failures, long RoundSeq, bool HeadMoved);

private static async Task<Dictionary<int, CapState>> CapStateAsync(MuthurDb db, IReadOnlyList<int> taskIds, CancellationToken ct)
```

It loads the `task.implemented` events for those tasks, takes the newest two per task, and reads `head` out
of `PayloadJson` with `JsonDocument` **in memory** - do not try to query into the JSON. The validating set is
small, and `PlanAsync` already pulls `task.implemented` events and picks the newest per task this way.

`HeadMoved` is **true** when either head is missing or unreadable. Events recorded before this change carry
no `head`, and a task must never be stalled on the absence of evidence - T-13's own history is exactly that
case, and it is the task this spec was written alongside.

### The rule

A task is skipped, and its round escalated, when:

```
state.Failures >= options.ConductorMaxAttempts && !state.HeadMoved
```

`PlanAsync` skips it. `EscalateExhaustedAsync` messages the founder once per round: `_escalated` becomes
`Dictionary<int, long>` mapping task id to the `RoundSeq` already announced, so a later unmoved resubmission
announces again and the same round does not.

### What the founder is told

The message must name the actual cause, which is no longer "it failed a lot" but "it came back unchanged":

> `{T-n} "{title}"` has failed validation `{n}` times and came back on the same commit, so the conductor has
> stopped restaffing it. Change the branch and mark it implemented again, re-spec it, cancel it, or raise
> `Muthur:ConductorMaxAttempts`.

Keep the per-verdict lines below it as they are.

## Units of work

### Unit A — the whole task
- **Files:** `src/Muthur.Server/Services/ConductorService.cs`; `tests/Muthur.Server.Tests/ConductorTests.cs`.
- **Does:** everything above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. **The live case.** A task failed `ConductorMaxAttempts` times, then fixed and marked implemented again
     **on a moved branch head**, is planned. Build it through the real lifecycle - implemented, fail,
     implemented, fail, implemented, fail, implemented - with a real commit on the branch between rounds, not
     by inserting events. Mutation-check it: restore the unconditional whole-history skip and confirm it fails.
  2. **The ceiling.** The same task resubmitted on the **same** head is not planned, and the founder is told,
     once, with the "came back on the same commit" wording.
  3. A second unmoved resubmission announces again; the same round does not announce twice.
  4. A task whose `task.implemented` events carry no `head` - recorded before this change - is planned, not
     stalled. Absence of evidence is not evidence of standing still.
  5. `ImplementedAsync` still refuses a branch that does not exist, with `branch_missing`.
  6. The launch-failure and no-verdict caps behave as they did; their existing tests pass untouched.

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
