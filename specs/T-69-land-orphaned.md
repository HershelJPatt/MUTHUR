# T-69 — The hub lands validated work whose owner did not survive to land it

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task a validated task whose orchestrator has gone reaches `done` without a human. The loop the
conductor exists to close, closes.

## Context

### The hole, measured

Orchestrator staffing went on at 15:38 on 2026-09-19. In the hundred minutes after it, three tasks landed and
**none of them by a hub-staffed orchestrator**. T-34 is the case: `orchestrator-t-34` claimed it, froze
`specs/T-34-conductor-roster.md`, delegated, integrated, marked it implemented, and a codex validator passed
it. It has sat in `validated` ever since, and nothing will ever move it:

- `TaskService.SweepExpiredClaimsAsync` filters `t.State == TaskState.InProgress`. `validated` is not swept.
- `ConductorService.PlanOrchestratorsAsync` filters `t.State == TaskState.Backlog` (line 613). `validated` is
  not planned.
- The owning agent is stale; its session exited.

T-60 does not help. T-60 works because the sweeper returns abandoned work to the backlog; the sweeper never
looks at `validated`.

This is the normal case rather than an edge. A session is capped at `ConductorSessionMinutes` (45) and must
otherwise survive claim → spec → delegate → review → implemented → **wait for a validator** → land.
Validation took ten to thirty minutes on every task today.

### What already exists and must be reused

`ITaskLander.LandAsync` performs the merge and needs no session. `LifecycleService` already has the land path
the CLI calls, including its `merge_conflict` refusal. `ConductorService.RunPassAsync` already runs on a timer,
already calls `EscalateExhaustedAsync` at line 658, and already knows what it has running via `_running`.

### Files that matter

- `src/Muthur.Server/Services/ConductorService.cs` — `RunPassAsync` (658), `_running`, `EscalateExhaustedAsync`.
- `src/Muthur.Server/Services/LifecycleService.cs` — the land path and its outcomes.
- `src/Muthur.Server/Services/GitLander.cs` — `LandAsync`, `LandResult`, `LandOutcome`.
- `src/Muthur.Server/MuthurOptions.cs` — `AgentStaleSeconds` (180).
- `tests/Muthur.Server.Tests/ConductorTests.cs` — the fake-launcher and fake-lander patterns.

Codebase rules: warnings are errors; every state change through `Ledger.MutateAsync` recording an event in the
same transaction; time from the injected `TimeProvider`; new behaviour comes with tests; tests never sleep.

## Non-goals

- **Auto-landing as policy.** An orchestrator that is alive lands its own work, exactly as the procedure says.
  This is a backstop for the session that did not live to see its verdict, and nothing else.
- Staffing a session to land. A forty-five-minute session to run one hub operation is the wrong shape.
- Changing `ConductorSessionMinutes`. That is a spend decision and does not fix the structural case.
- Pushing after the land — that is T-63.
- Any change to how tasks reach `validated`.

## Design

### Where it lives

In `ConductorService.RunPassAsync`, beside `EscalateExhaustedAsync` and before staffing: one call to
`LandOrphanedAsync(ct)`. The conductor already runs on a timer and is the only thing that knows which tasks
have a live session; a separate sweeper would have to be told.

It runs whenever the conductor is enabled. It does **not** require `orchestrators` to be on: a task validated
before that switch was thrown is in exactly the same position, and this is recovery rather than staffing.

### What it lands

A task is **orphaned-validated**, and the hub lands it, when all of:

1. `State == TaskState.Validated`;
2. it has an owner, and that owner's `LastHeartbeat` is older than `now - AgentStaleSeconds`;
3. `_running` holds no entry for that task under any role — the conductor is not currently running a session
   that might be about to land it itself;
4. the project has a non-empty `RepoPath`.

Condition 3 is the one that keeps this from taking work out from under a living session, and it is stronger
than staleness alone: an orchestrator mid-build is quiet for minutes at a time, and `_running` is what the
conductor actually knows about its own sessions.

Ordered `OrderByDescending(Priority).ThenBy(Id)`, and **all** of them land in a pass — landing is cheap and is
not a session, so the session ceiling does not apply.

### What it does

For each, call the same `LifecycleService` land path the CLI calls, as `Caller.Founder`, and record what came
back:

- **Landed** — the existing `task.landed` event is recorded by that path unchanged. Additionally record
  `conductor.landed` with `{ task, owner }` so the founder can tell a hub land from a human one.
- **Conflict** — the task returns to its owner as it does today, which for a dead owner means it will be
  swept to the backlog and replanned with T-60's `Resuming: true`. The conductor **must** additionally post
  once to the founder, naming the task, the branch and the conflicting files from `LandResult`, and record
  `conductor.land_conflict` with the same. Once per `(task, branch head)` — a conflict that persists must not
  write a message every minute. Follow the `_escalated` pattern `EscalateExhaustedAsync` already uses.
- **Refused** (dirty checkout, push refused, `gh` missing) — leave the task `validated`, record
  `conductor.land_refused` with the code and message, and post to the founder once under the same key. The
  environment is wrong and a human must look; retrying every minute helps nobody.

### The green question, answered

The hub lands on the validator's word and checks nothing further, and this is stated so nobody assumes
otherwise. Validation proved the branch; `ITaskLander` refuses a merge conflict; nothing else is checked.
The hub has no test runner and must not grow one.

This is a real change in what a land means: today a human looks at main before landing, and after this a
validated task can reach main without anyone having done so. The mitigation that already exists is T-38's
work and the suite itself; the honest statement is that the validator's pass is the gate.

Record it plainly in the `conductor.landed` payload comment and in the README beside the conductor settings,
so the next reader meets the decision rather than inferring it.

## Units of work

### Unit A — land what nobody is left to land
- **Files:** `src/Muthur.Server/Services/ConductorService.cs`, `README.md`; tests in
  `tests/Muthur.Server.Tests/ConductorTests.cs`.
- **Does:** the Design above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean, `dotnet test` green, single run.
  - A `validated` task with a **stale** owner and no `_running` entry is landed by a pass; the task reaches
    `done` and both `task.landed` and `conductor.landed` are recorded.
  - The same task with a **live** owner is not landed. Drive with the fake clock.
  - The same task **with** a `_running` entry for it is not landed, even when the owner is stale. This is the
    test that stops the hub racing a session that is about to land its own work.
  - A `validated` task whose project has an empty `RepoPath` is not landed.
  - A lander returning `Conflict` leaves the task where today's land path leaves it, records
    `conductor.land_conflict` with branch and files, and messages the founder **once** across repeated
    passes on the same branch head.
  - A lander returning `Refused` leaves the task `validated`, records `conductor.land_refused`, and messages
    the founder once.
  - Two orphaned-validated tasks in one pass both land — the session ceiling does not gate landing.
  - With the conductor disabled, nothing lands.

## Verification

```
dotnet build        # 0 warnings, 0 errors
dotnet test         # all green, single run
pwsh ./scripts/install.ps1 -Destination ./artifacts/validate
```

End to end against a **scratch** hub, `MUTHUR_HOME` and `MUTHUR_URL` prefixed per command and never exported.
No browser and no GUI. Use a disposable git repository the test creates, never a real project.

```powershell
# a validated task whose owner has gone quiet
muthur project add demo --repo <temp repo> --founder      # ungated: implemented goes straight to validated
muthur agent register --name a1 ... --founder
muthur task add "orphan" --project demo --as-agent a1
muthur task claim T-1 --as-agent a1
muthur task spec T-1 specs/T-1.md --as-agent a1
muthur task implemented T-1 --branch task/T-1-work --as-agent a1   # -> validated, no validators required
# let a1 go stale (no heartbeat for more than Muthur:AgentStaleSeconds), then
muthur conductor on --founder
muthur task show T-1        # done
muthur log --limit 20       # task.landed and conductor.landed
git -C <temp repo> log --oneline main    # the merge commit is really there
```

The negative that matters: repeat with `a1` heartbeating, and confirm the task stays `validated` and no
`conductor.landed` is recorded.

## Out of scope / follow-ups

- Pushing the landed commit (T-63).
- Whether `_lastAction` should distinguish a land from a staffing on the founder's card.
