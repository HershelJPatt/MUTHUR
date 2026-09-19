# T-43 — A session whose round closed under it is neither credited nor charged

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a conductor session that ends because **another validator already decided the task** is
recorded as exactly that, instead of being counted as a session that produced nothing. The no-verdict cap
stops accumulating strikes against validators that did nothing wrong, and the founder stops being sent to
look for a broken validator when the real answer is "the task failed, as designed".

## The defect

`RunPassAsync` starts one session per **pending validation**, concurrently — so a project with two required
validators gets two sessions for one task. `LifecycleService.VerdictAsync` sets the task to `InProgress` the
moment the **first** validator fails it, which leaves every sibling's `TaskValidation` row `Pending`.

`ReachedAVerdictAsync` then asks only about that pair's row:

```csharp
!await db.TaskValidations.AnyAsync(v =>
    v.TaskId == assignment.TaskId && v.ValidatorKey == assignment.RoleKey && v.Verdict == Verdict.Pending, ...)
```

It finds `Pending`, so `RunSessionAsync` falls to `UnproductiveAsync(NoVerdict)` — whose comment says *"It
started, it exited cleanly, and it recorded nothing. That is not progress."* For the sibling that is untrue:
the round ended correctly and there was nothing left to record.

After `ConductorMaxAttempts` such rounds the sibling's pair stalls half-open, and the founder is told:

> The conductor started 3 validators for T-1 (web-validator) and none of them reached a verdict. They ran and
> exited cleanly, so something is stopping them from validating at all rather than failing.

That message is an **active misdiagnosis**. T-30 built it precisely so a founder would be sent to the right
place; here it sends them to the wrong one.

In production it is worse than a probe shows: the sibling's validator does real work, tries to post its
verdict, gets 422 `not_validating`, and is still counted as having recorded nothing.

**Latent, not live.** The only project on this hub requires one validator, so no founder has been
misdiagnosed. It becomes live the moment a second required validator exists — which is exactly what T-13
makes worth having, and T-13 is in validation now.

## The decision inside the fix

The obvious fix is to treat "the task is no longer `Validating`" as the round having ended. What that leaves
open — and what the implementer who found this flagged rather than assumed — is whether such a session
should **clear** the pair's strikes.

**It must not.** Neither credited nor charged:

- **Not charged**, because the session did nothing wrong and the cap exists to catch sessions that did.
- **Not credited**, because clearing on a closed round would let a genuinely broken validator have its
  strikes wiped by a sibling's failure. A pair that only ever runs in rounds closed by others would be
  laundered clean without ever recording a verdict — which is precisely the thing the no-verdict cap is for.

So a closed round leaves the counter exactly where it was. A pair with two strikes still has two; it clears
only by actually reaching a verdict. That is the honest reading of "no evidence either way".

## Context

- `src/Muthur.Server/Services/ConductorService.cs`:
  - `RunSessionAsync` (~line 205) — the three-branch outcome after `launcher.StartAsync` returns.
  - `ReachedAVerdictAsync` (~line 242) — the query that must learn about the task's state.
  - `UnproductiveAsync`, `Unproductive` (~line 54), `Stall` — the counting, unchanged by this task.
  - `PlanAsync`'s inner `foreach (var validation in pending.Where(v => v.TaskId == task.Id))` is what starts
    one session per validator; it does not change.
- `src/Muthur.Server/Services/LifecycleService.cs` — `VerdictAsync` sets `InProgress` on a failure and on a
  blocked verdict, in the same mutation that records the verdict. `TaskState.Validating` is set only by
  `ImplementedAsync`.
- `tests/Muthur.Server.Tests/HubFactory.cs` — `FakeValidatorSessions` drives a session without a process,
  and its `Delegate` hook lets a test make a session do something real before it returns.

Constraints that are not obvious:

- `RunSessionAsync` is fire-and-forget and uses `CancellationToken.None` throughout.
- Reads go through `Ledger.ReadAsync`; the counters are guarded by `lock`, and no lock may be held across
  an `await`.
- **Tests never sleep to synchronize.**
- Warnings are errors.

## Non-goals

- Cancelling sibling sessions when a task leaves `Validating`. That would genuinely save quota and is the
  better end state, but it means reaching into `AgentLauncher`'s process handles. Named in the follow-ups.
- Any change to when `PlanAsync` starts sessions, or to how many.
- Any change to the launch-failure or no-verdict caps themselves, or to `ConductorMaxAttempts`.
- Any change to what a failed or blocked verdict does to a task.
- A founder message for a closed round. It is normal and not actionable; the ledger records it and that is
  enough.

## Design

### Three outcomes, not two

Replace `ReachedAVerdictAsync` with a classifier, in one read:

```csharp
/// <summary>What a session that exited cleanly actually left behind.</summary>
private enum RoundEnd
{
    /// <summary>This pair recorded a verdict. The pair is working.</summary>
    Verdict,
    /// <summary>Another validator decided the task first, so there was nothing left for this pair to record.</summary>
    Closed,
    /// <summary>The round is still open and this pair recorded nothing. That is the defect the cap catches.</summary>
    Nothing,
}

private Task<RoundEnd> RoundEndAsync(ConductorAssignment assignment)
```

It reads the task's `State` and this pair's `TaskValidation.Verdict` together, and decides:

| Condition | Result |
|---|---|
| the pair's row is not `Pending` | `Verdict` |
| the row is `Pending` **and** the task is not `Validating` | `Closed` |
| the row is `Pending` and the task is `Validating` | `Nothing` |

Order matters: check the verdict first. A task that reached `Validated` because every validator passed is
not `Validating` either, and that pair must read as `Verdict`, not `Closed`.

A row that no longer exists — the next round wiped it, because `ImplementedAsync` removes the previous
round's rows — reads as `Verdict`. The pair is not pending, so it is not the cap's business.

### What `RunSessionAsync` does with them

```csharp
else switch (await RoundEndAsync(assignment))
{
    case RoundEnd.Verdict:
        lock (_stalls) _stalls.Remove(key);   // including a probe: the pair is working again
        break;
    case RoundEnd.Closed:
        // Another validator decided the task while this session was running, so there was nothing left to
        // record. Not charged, because the session did nothing wrong. Not cleared either: a pair that only
        // ever runs in rounds closed by others must not be laundered clean without reaching a verdict.
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("conductor.round_closed", assignment.TaskId, new { role = assignment.RoleKey });
            return Task.CompletedTask;
        }, CancellationToken.None);
        break;
    default:
        await UnproductiveAsync(assignment, key, Unproductive.NoVerdict, null);
        break;
}
```

The event is recorded rather than swallowed because the session **spent a slot**. A founder asking what
their subscription went on — which is T-17 — needs the round-closed sessions in the ledger, not missing
from it. It carries no founder message: it is normal, and not something to act on.

## Units of work

### Unit A — the whole task
- **Files:** `src/Muthur.Server/Services/ConductorService.cs`;
  `tests/Muthur.Server.Tests/ConductorTests.cs`.
- **Does:** everything above.
- **Depends on:** nothing. It is independent of T-13, which only makes the defect reachable in practice.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. **The defect.** A project requiring two validator roles, a task in `validating`, both sessions started.
     One records a failing verdict; the other's session returns having recorded nothing. Assert the sibling
     gets **no** `conductor.no_verdict`, **one** `conductor.round_closed` naming its role, and that three
     such rounds produce **no** `conductor.stalled` and no founder message. Mutation-check it: restore the
     two-way `ReachedAVerdictAsync` and confirm it fails with the stall and the misdiagnosing message.
  2. **The cap still bites.** A task still in `validating` whose session records nothing is charged
     `conductor.no_verdict` exactly as before, and stalls after `ConductorMaxAttempts`.
  3. **Neither credited.** A pair with strikes short of the cap, whose next round is closed by a sibling,
     still has those strikes: one further genuine no-verdict round takes it to the cap and stalls it. This
     is the decision above, and it is the one a future reader is most likely to reverse by accident.
  4. **A verdict still clears.** A pair with strikes that then records a verdict is cleared, as today.
  5. **All-passed reads as a verdict, not a closed round.** Two validators, both pass, task reaches
     `validated`: neither session records `conductor.round_closed`.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub. A stand-in `claude` on PATH
that prints `{"result":"ok","is_error":false}` exercises the launch path without spending anything.

With a project requiring **two** validator roles and one task marked implemented:

- Turn the conductor on. Two `conductor.staffing` events appear, one per role.
- Have a real agent holding one of the roles fail the task. The other session ends.
- `muthur log` shows one `conductor.round_closed` for the second role and **no** `conductor.no_verdict`.
- Repeat three times. There is no `conductor.stalled`, and `muthur msg inbox --founder` carries no message
  about validators that "ran and exited cleanly" — which is the misdiagnosis this task removes.

## What the end-to-end actually showed

Run on an installed build (`artifacts/t43`) against a scratch home on port 7461, never the live hub, with a
project requiring **two** validator roles and one task marked implemented.

It was not the scripted run this section describes, and the difference is worth recording. A stand-in
`claude` was placed first on PATH, but `avoidHarness: claude` moved claude to the back of the tier and the
conductor staffed **codex** for both roles — so two real validator sessions ran, and they produced the exact
production case the defect section predicts rather than a simulation of it:

```
12  conductor.staffing        {"role":"web-validator","avoidHarness":"claude"}
14  conductor.staffing        {"role":"win-validator","avoidHarness":"claude"}
18  validation.blocked        win-validator  (real evidence, real worktree)
19  task.validation_blocked   -> T-1 leaves Validating
25  message.sent  conductor-web-validator -> builder:
      "Correction: the web-validator blocked verdict command was rejected with not_validating
       because T-1 had concurrently returned to in_progress."
27  conductor.round_closed    {"role":"web-validator"}
```

The sibling did its work, tried to post, was refused 422 `not_validating`, and said so itself on the bus.
Across the whole ledger: `conductor.round_closed` 1, `conductor.no_verdict` **0**, `conductor.stalled` **0**,
and no founder message containing "ran and exited cleanly". On main that session would have taken a strike.

One practical note for whoever next reaches for the stand-in-harness technique: it only bites if the harness
you shimmed is the one the tier actually selects, and `avoidHarness` can send the selection elsewhere.

## Out of scope / follow-ups

- **Cancelling siblings when the round closes.** Today a sibling keeps running, does its work, and is
  refused with 422 `not_validating` when it posts. Recording the closed round makes the waste visible;
  stopping it needs the launcher to hold and kill process handles, which is a larger change and its own task.
- `muthur doctor` could report roles whose recent rounds are mostly closed rather than decided — a sign that
  a project's validators are ordered such that one always decides first. Speculative; only worth it if the
  ledger shows it happening.
- T-17 (receipts) is where `conductor.round_closed` earns its keep: it is a session that cost a slot and
  produced nothing, and it should appear as such rather than as an absence.
