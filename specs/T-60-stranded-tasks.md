# T-60 — A task whose orchestrator dies is stranded in_progress forever

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a task whose owner has died is picked up again instead of sitting in `in_progress` for
ever, and a task whose owner is merely quiet is left alone. This is the difference between a hub that can be
left running and one that must be watched.

## Context

### The hole

`kit/core/orchestrate.md` step 1 selects `--state backlog`, and T-59's `PlanOrchestratorsAsync` selects
`TaskState.Backlog`. Nothing looks at `in_progress`. So a task whose owner is gone is invisible to every
mechanism that assigns work, even though the state machine already permits the recovery:

```csharp
/// <summary>A task can be claimed from backlog, or taken over when its in-progress claim has lapsed.</summary>
public static bool IsClaimable(WorkTask task, DateTimeOffset now)
```

### Renewal is not missing — this is the important correction

`AgentService.HeartbeatAsync` already extends every owned `in_progress` task's claim to
`now + ClaimLease` on **any** heartbeat, and `RoleLeases.RenewAsync` does the same for roles. A working
orchestrator that reports in keeps its task.

The failure is therefore **not** "claims expire too fast". It is that a session which goes quiet for longer
than `ClaimLeaseMinutes` (30) is indistinguishable from one that has died — and a session legitimately goes
quiet for that long while a `dotnet test` run, an AOT publish or a delegated worker is in flight. Both
observed cases today are this:

- **Dead.** `corner` retired at 04:16 with the summary "handing over", leaving T-13 and T-17 in
  `in_progress`. Neither would ever have been picked up; the overseer released them by hand at 04:39.
- **Alive but silent.** The overseer's own claim on T-59 lapsed at 14:00 after 32 minutes of local building
  with no heartbeat. `bottom-left` claimed it at 14:09, correctly, while a subagent was still building
  against the branch. Nothing was lost only because a human saw both sides.

A lapsed claim alone is therefore **not** evidence of death, and staffing on it would manufacture exactly
the T-47 collision this organization has just finished reasoning about.

### Files that matter

- `src/Muthur.Server/Services/ConductorService.cs` — `PlanOrchestratorsAsync` (T-59), `_stalls`, `_running`.
- `src/Muthur.Server/Services/OrchestratorSessionLauncher.cs` — the prompt.
- `src/Muthur.Server/Services/AgentService.cs:70-96` — `HeartbeatAsync`, which already renews.
- `src/Muthur.Core/LeasePolicy.cs` — `IsClaimable`, unchanged by this task.
- `src/Muthur.Server/MuthurOptions.cs` — `AgentStaleSeconds` (180), `ClaimLeaseMinutes` (30).
- `kit/core/orchestrate.md` — the procedure agents follow.
- `tests/Muthur.Server.Tests/ConductorTests.cs` — the fake-launcher pattern.

Codebase rules: warnings are errors; every state change through `Ledger.MutateAsync` with an event; time from
the injected `TimeProvider`; new behaviour comes with tests; tests never sleep to synchronize.

## Non-goals

- Changing `ClaimLeaseMinutes`, or making it a knob. The lease length is not the problem.
- Touching `IsClaimable`, or the `blocked` state — a blocked task waits on a founder and is not abandoned.
- Any change to how validators are planned.

## Design

### Two conditions, not one

`PlanOrchestratorsAsync` gains a second source: a task is **stranded**, and may be taken over, when *all* of

1. `State == TaskState.InProgress`;
2. `ClaimExpires` is null or `<= now` — the claim has lapsed;
3. its owning agent's `LastHeartbeat` is older than `now - AgentStaleSeconds` — the owner is **stale**;
4. the project has a non-empty `RepoPath`;

and the existing `_running` and `_stalls` guards pass, exactly as for a backlog task.

Condition 3 is what separates the two cases above. A live orchestrator running a long build still heartbeats
inside three minutes or it is not live, so a session that is merely busy keeps its work.

### Resuming beats starting

Stranded tasks are planned **before** backlog tasks, each group ordered `OrderByDescending(Priority).ThenBy(Id)`
as now. Work already specced, branched and part-built is closer to done than work not begun, and leaving it
stranded while starting something new is how a board fills up with things nobody is carrying.

### The prompt says it is a takeover

`OrchestratorSessionLauncher` takes an extra `bool Resuming` on `OrchestratorAssignment`. When true, the
prompt keeps every existing rule and replaces its opening with:

```
You are taking over {TaskKey} ("{TaskTitle}") from `{PreviousOwner}`, whose session stopped. This is a
resumption, not a fresh start: the task already has a branch and may already have a frozen spec.

    muthur task claim {TaskKey}
    muthur task show {TaskKey}
    muthur log --task {TaskKey}

Read those three before you write anything. The branch on the task is the work so far; the ledger is what
the last session did and why it stopped. Continue from there rather than starting again — and if the branch
is further along than the ledger suggests, trust the branch and say so in your first heartbeat.
```

`OrchestratorAssignment` therefore becomes:

```csharp
public sealed record OrchestratorAssignment(int TaskId, string TaskKey, string TaskTitle, string Project, bool Resuming = false, string? PreviousOwner = null);
```

### Nothing else changes

The takeover is performed by the session itself through the ordinary `muthur task claim`, which
`IsClaimable` already permits — the conductor never reassigns a task, exactly as T-11 required. `_stalls`
is untouched, so a task that kills three sessions in a row stops being restaffed whether those sessions were
fresh or resuming.

### The procedure gains one sentence

`kit/core/orchestrate.md`, in the Identity section beside the existing heartbeat advice:

> Heartbeat before anything that will take more than a few minutes — a full `dotnet test`, an AOT publish, a
> delegated worker — and again when it returns. A heartbeat renews the claim on every task you own; a
> session that is silent for longer than the claim lease looks exactly like one that has died, and the
> conductor may hand your task to someone else.

## Units of work

### Unit A — the stranded source, the prompt, and the procedure
- **Files:** `src/Muthur.Server/Services/ConductorService.cs`,
  `src/Muthur.Server/Services/OrchestratorSessionLauncher.cs`, `kit/core/orchestrate.md`;
  tests in `tests/Muthur.Server.Tests/ConductorTests.cs`.
- **Does:** everything in Design, exactly.
- **Depends on:** T-59's Unit C, which is on this branch already.
- **Acceptance:**
  - `dotnet build` clean; `dotnet test` green, single run.
  - An `in_progress` task with a lapsed claim **and a stale owner** is planned, and the assignment carries
    `Resuming: true` and the previous owner's name.
  - The same task with a **live** owner (heartbeat inside `AgentStaleSeconds`) is **not** planned, even
    though its claim has lapsed. Drive this with the fake clock, never a sleep.
  - The same task whose claim has **not** lapsed is not planned, whatever the owner's state.
  - A stranded task and a backlog task with equal priority: the stranded one is planned first.
  - A `blocked` task with a stale owner is never planned.
  - The resuming prompt names the task, the previous owner, and contains `muthur log --task`.
  - A stranded task that produces three unproductive sessions stalls exactly as a backlog one does.
  - Orchestrators off: no stranded task is planned.

## Verification

```
dotnet build        # 0 warnings, 0 errors
dotnet test         # all green, single run
pwsh ./scripts/install.ps1 -Destination ./artifacts/validate
```

End to end against a **scratch** hub, `MUTHUR_HOME` and `MUTHUR_URL` prefixed per command and never exported,
with a stand-in `claude` on PATH that prints `{"result":"ok","is_error":false}` so no subscription is spent.
No browser and no GUI anywhere.

```powershell
# a task owned by an agent that then goes silent
muthur task add "stranded" --project demo --as-agent a1
muthur task claim T-1 --as-agent a1
# let the claim lapse and the agent go stale, then:
muthur conductor orchestrators on --founder
muthur log --limit 20           # conductor.staffing for T-1, and an agent named orchestrator-t-1
muthur agent list               # the resuming session registered

# and the negative that matters: an owner that keeps reporting in keeps its task
muthur agent heartbeat --as-agent a1
muthur log --limit 20           # no staffing for that task
```

## Out of scope / follow-ups

- Whether the overseer role itself should be a hub-staffed session. Its own task.
- `_lastAction` does not distinguish a resumption from a fresh start on the founder's card.

## Amendment 1 — the premise was false, and the task is smaller and sharper than it looked

The implementer refused to build this and was right. Everything above rests on "a task whose orchestrator
dies sits in `in_progress` for ever". It does not.

`TaskService.SweepExpiredClaimsAsync` returns every lapsed in-progress task to the backlog, unconditionally,
and `LeaseSweeper` runs it every **30 seconds** and once at startup. `ReturnToBacklog` clears State, Owner
and ClaimExpires and **keeps Branch, SpecPath, PrUrl and Priority**. T-59's existing
`PlanOrchestratorsAsync` then staffs it from the backlog with no change at all. Verified in the source, and
by the implementer against a real hub with a fake clock:

```
planned while in_progress: 0; swept: 1; state after sweep: Backlog; owner: (none);
spec kept: specs/T-1.md; planned after sweep: [T-1]
```

The sweeper and the conductor are registered under the same `BackgroundServices` flag, so a hub that is not
sweeping is not staffing either.

**The Context section's evidence was misread, and the misreading was mine.** T-13 and T-17 were released by
hand at 04:39; their claims ran to 05:03. Nothing was stranded — the overseer simply did not wait the
remaining twenty-four minutes. That is evidence about a human's patience, not about the machine.

### What is actually wrong

The task comes back, but it comes back **anonymous**. `PlanOrchestratorsAsync` cannot tell a task that has
never been started from one that already carries a frozen spec and a half-built branch, so the second kind
is handed the fresh-start prompt — *"Claim {TaskKey} … and take it from claim to landing"* — and a session
begins designing work that is already designed. That is the duplication this task exists to prevent, and the
original Design never reached it, because it was watching a state the sweeper had already cleared.

### The design, replaced

1. **`PlanOrchestratorsAsync` keeps its backlog query unchanged.** No new source. The `stranded` source
   described above is **dropped**: the sweeper's 30-second interval beats the conductor's 60-second one, so
   it would be dead code in production, and it is what created the contradiction in Finding 3 below.
2. **`Resuming` is set from the work already attached**, not from the state: `true` for any planned task
   whose `SpecPath` or `Branch` is non-null. A task in `backlog` carrying either has been worked before —
   `task spec` requires an owner, so there is no other way to acquire one.
3. **`PreviousOwner` comes from the ledger**, because the sweeper cleared the column. The newest
   `task.claim_expired` for the task carries `{ agent }`; one grouped query in the same shape as the existing
   `built` and `CapStateAsync` reads.
4. **Order:** tasks carrying prior work before untouched ones, both `OrderByDescending(Priority).ThenBy(Id)`.
   That is the original "resuming beats starting" intent, expressed against the state that actually occurs.
5. **The takeover prompt ships exactly as written above.** Only its trigger changes.
6. **The `kit/core/orchestrate.md` heartbeat sentence stays.** It is true and useful on its own: a session
   silent longer than the claim lease loses its task to the sweeper, which is precisely what happened to the
   overseer on T-59 at 14:00.

### Finding 3 dissolves rather than needing a fix

The implementer found that Design and Acceptance contradicted each other: `RunOrchestratorSessionAsync`
judges a session productive via `StillInBacklogAsync`, so a resuming session whose task is `InProgress`
would always be credited as productive and never stall. With the stranded source dropped, every planned task
is in `backlog` when planned and leaves it when claimed, so the existing check is correct as written and
needs no change. Removing the source removes the contradiction.

### Condition 3 was not doing the work the spec claimed

`HeartbeatAsync` sets `LastHeartbeat` **and** renews the claim in one mutation, so at the default 30-minute
lease "the claim lapsed" already implies "silent for 30 minutes", which implies stale at 180 seconds. The
staleness test only does independent work for a claim taken with an explicit short lease
(`muthur task claim T-n --lease 1`) or inside the plan-to-launch race. Neither is the case the spec argued
from. With the stranded source gone the question is moot, and it is recorded here so nobody reinstates the
argument later.

### Left for the founder, not decided here

Whether `SweepExpiredClaimsAsync` should stop returning tasks to the backlog and leave them `in_progress`
until someone takes them over — keeping the owner visible and giving `LeasePolicy.IsClaimable`'s takeover
path something to do. That is arguably the better world and it is a founder-level change: the board, `muthur
task list --state backlog`, and `Sweep_returns_lapsed_tasks_to_the_backlog` in `TaskLeaseTests.cs:95` all
rest on today's behaviour. Its own task if it is ever wanted; T-60 does not need it.

## Amendment 2 — priority is the founder's lever, and prior work is only the tiebreaker

The implementer built the ordering Amendment 1 specified and then flagged the line it did not like, which
is the correct order of operations. The line was wrong and it was mine.

"Tasks carrying prior work before untouched ones, each group ordered by priority" means an abandoned
priority-1 task is staffed ahead of an untouched priority-9 one. Priority is the only lever a founder has to
say *this one matters most*, and a rule that silently outranks it takes that lever away. The founder raised
`validator` capacity today precisely to stop urgent work waiting; a queue that answers by starting an old
priority-0 task first is not one they can steer.

The intent — "resuming beats starting" — is real, but it is a tiebreaker, not a trump. Order becomes:

```csharp
.OrderByDescending(t => t.Priority)
.ThenByDescending(CarriesWork)     // among equals, finish what is begun
.ThenBy(t => t.Id)
```

So prior work still wins wherever it is genuinely a choice, and never inverts an explicit priority. The test
pinning the old behaviour changes with it: three tasks `begun(p1)`, `urgent(p9)`, `ordinary(p5)` must plan
as `[urgent, ordinary, begun]`, and a fourth case — two tasks at equal priority, one carrying a branch —
pins that the begun one still goes first.

### Also, the prompt should not claim a branch it may not have

The takeover opening says "the task already has a branch and may already have a frozen spec", while the
trigger is spec **or** branch. A spec-only resumption is therefore told about a branch that does not exist.
It becomes "the task already has work on it: a frozen spec, a branch, or both."

### Accepted as built

- **`PreviousOwner` reads `task.claimed` as well as `task.claim_expired`**, newest by `Seq`. Amendment 1
  named only `claim_expired`, which exists only when the sweeper lapsed the claim — so a task released by
  hand, which is the 04:39 case the original Context was written about, would have had no name at all. The
  deliberate exclusion of `task.released` is the sharper half: it records the *caller*, so a founder release
  would have put "founder" in front of the next session as the previous owner.
- **"from an earlier session that stopped"** when the ledger names nobody. Falling back to the fresh-start
  prompt would hand a resumption the exact wording this task exists to remove.
- **The kit sentence's last clause** now describes the sweeper returning the task to the backlog rather than
  the conductor handing it on. The original described the mechanism Amendment 1 deleted.

### Still a follow-up, deliberately

A resumption is invisible to the founder: `conductor.staffing` records only `{ role }` and `_lastAction`
says "staffed an orchestrator for T-1" either way. Its own task rather than a wider diff here.
