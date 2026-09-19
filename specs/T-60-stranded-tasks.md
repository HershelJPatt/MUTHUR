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
