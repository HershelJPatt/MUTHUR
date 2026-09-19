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

## Amendment 1 — the retry must be bounded, and four things the spec got wrong

### The one change to make

**A refusal must stop being retried every pass.** The implementer kept the spec literally — message the
founder once — and then pointed out that the sentence beside it ("retrying every minute helps nobody") argues
for bounding the *attempt* as well, which the spec never actually said. It is right, and the consequence is
not hypothetical: a refusal leaves the task `validated`, so every pass retries for ever, writing
`task.land_refused` from `LifecycleService` **and** `conductor.land_refused` from the conductor, roughly
fourteen hundred times a day, for a fault only a human can clear. That is the T-45 pathology moved from the
inbox into the ledger, and shipping it into an unattended hub is exactly the thing this task exists to avoid.

The refusal is also reachable rather than exotic: `dirty_checkout` fires whenever the shared main checkout has
uncommitted changes, which happens routinely here, and `branch_missing` fires whenever a branch is cleaned up.

Bound it the way the conductor already bounds a wedged pair: a half-open probe keyed on `(task, branch head)`,
letting one attempt through per `ConductorStallProbeMinutes`. A head that moves clears the cooldown, because a
new commit is new information. Reuse the `_stalls` shape rather than inventing a second one.

### Accepted as built

- **The silent skip on `not_validated`.** If the owner revives between the plan and the land and ships its own
  work, the spec as written would have recorded a refusal and told the founder a land failed for a task that
  had in fact just landed. `catch (MuthurException gone) when (gone.Code == "not_validated") { continue; }` is
  correct, and the honest note that it is the one branch without a test — because covering it means a seam
  through `HubFactory`, outside this unit's files — is the right trade to name rather than paper over.
- **README placement** in `## The conductor` rather than the settings table, which has no row for this.
- **Recording `branch` as its own field and `message` verbatim.** `LandResult` is
  `(Outcome, Commit, PrUrl, Code, Message)` and carries no file list — the conflicting paths exist only inside
  `GitLander.ConflictMessage`'s prose. The spec asked for something that does not exist. Structured files
  would be a `GitLander.cs` change and belong to their own task.
- **Real git instead of a fake lander.** The spec cited "the fake-lander patterns in `ConductorTests.cs`";
  there is no fake `ITaskLander` anywhere in `tests/`. Driving real git through `TestRepo` is better here, and
  it covered a failure mode the spec only asked about in passing: a validated task whose branch no longer
  exists refuses with `branch_missing`, stays `validated`, and tells the founder once.

### Known and accepted, not fixed here

**Landing can hold the pass.** `GitLander` allows two minutes per git invocation and `LandAsync` runs several,
so several orphaned tasks could occupy `_pass` for minutes. `_pass.WaitAsync(0)` drops the overlapping tick
rather than queueing it, so a slow land delays staffing and never doubles it. Bounded, and tolerable.

**Unattended landing now reaches `LandMode.Pr` projects**, where the existing path shells out to `git push`
and `gh`. MUTHUR's own project is `merge`, so nothing is exposed today — but this is an authority change
nobody has signed off, and it is recorded here so the first PR-mode project meets the decision rather than
discovers it.

## Amendment 2 — accepted, including the part I did not ask for

The bounded retry is built on the `_stalls` shape rather than a second mechanism, keyed on `(task, branch
head)`, and a head that moves clears the cooldown outright rather than waiting it out. A conflict arms no
cooldown, correctly: a conflict takes the task out of `validated` and therefore out of the state this plan
looks at, so there is nothing to hold back and cooling it would only have delayed a genuine resubmission.

**`SetEnabledAsync` now clears `_landStalls` alongside `_stalls`, and that was required rather than extra.**
The founder message ends by promising that `muthur conductor off --founder && muthur conductor on --founder`
retries at once. A message that tells the founder an incantation which does not work is worse than no message,
so making it true is part of shipping the sentence. It follows the rule already written beside `_stalls` —
turning the conductor on is the founder saying try again — and it has its own test.

The dropped `Failures` count is right too: a land refused once has told the founder everything a land refused
three times would, so the cooldown arms on the first refusal and there is no `ConductorMaxAttempts` here.

The residual `task.land_refused` from `LifecycleService` is bounded as a consequence — one per probe instead
of one per pass — without touching `LifecycleService.cs` or `GitLander.cs`.

## Amendment 3 — the head must be the branch's head, not the one the ledger remembers

Validation failed T-69 on the one sentence both earlier amendments turned on. Amendment 1 asked for a probe
keyed on `(task, branch head)`; Amendment 2 accepted that "a head that moves clears the cooldown outright
rather than waiting it out, because a new commit is new information". The implementation takes that head from
the most recent `task.implemented` ledger event.

That value does not move when a commit is made. It moves when somebody calls `muthur task implemented` again.
So the recovery the amendment promised — fix the cause, push, and it lands on the next pass — does not
happen: a genuine new commit earns neither an immediate retry nor a fresh notification, and even the timed
probe thirty minutes later records a head that is no longer the branch's.

The validator reproduced it on the installed product with a scratch hub, a disposable repository and a
deleted default branch to force `branch_missing`, rather than reading it off the diff.

**The fix:** the cooldown key reads the branch's actual head. `ITaskLander.BranchHeadAsync(project, branch, ct)`
already exists and already returns null for a branch that is gone — which is the same condition that produced
the refusal in the first place, so a missing branch keeps its cooldown and a restored one clears it.

It costs one `git rev-parse` per orphaned-validated task per pass, and only for tasks already in a cooldown,
which is a cost worth paying for the property the amendment claimed. If it turns out to be measurably worse
than that, say so rather than working around it.

Everything else in Amendment 2 stands: a conflict still arms no cooldown, the count is still absent, the
founder message is still once per head, and `SetEnabledAsync` still clears the land cooldowns.

## Amendment 4 — accepted, and one consequence said out loud

The cooldown now reads the branch's real head through `ITaskLander.BranchHeadAsync`, taken outside the ledger
read — a subprocess does not belong inside a DB read callback — with the whole plan judged against a single
`clock.GetUtcNow()` so staleness and the cooldown agree about when "now" is.

The test that proves it is the one the earlier suite could not: a `dirty_checkout` refusal, then **a real
commit on the branch with the checkout still dirty**, and the very next pass tries again and refuses again.
That second refusal is the evidence it tried at all. The implementer's first attempt at that test cleaned the
checkout without moving the head and expected a land; the cooldown correctly held and the test failed. "Fix
the cause and push" is one act, and the test now says so.

### The consequence, recorded rather than discovered

**The hub can now land a commit no validator saw.** A validator passes head A, somebody commits B, and the
next pass merges B.

This is not new — the timed probe already landed whatever the branch held thirty minutes later, and
`muthur task land` has always merged the current branch rather than a remembered sha. Amendment 3 makes it
happen sooner and more often, which is enough of a change to write down.

It is also not separable from the recovery this amendment exists for: fixing a `dirty_checkout` by committing
is precisely the case, so a hub that refused a head differing from the validated one would refuse the only
route out of the refusal it was built to handle. If that trade is ever worth revisiting — pinning a land to
the exact commit a validator passed — it is a real design decision and its own task, and it interacts with
T-63's push and with how a bounce-back re-validates.

### Also worth keeping

Warnings-as-errors caught a mutation attempt (`CS9113: Parameter 'lander' is unread`) before it could produce
a misleading result. The rule earning its place.
