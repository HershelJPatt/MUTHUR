# T-27 — `muthur doctor` answers within a budget, or says which check did not

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task a probing doctor run is bounded: whatever the checks do, the hub answers within
`DoctorBudgetSeconds`, and a check that did not answer in time appears in the report as a `warn` saying so
rather than holding the request open. The dashboard's `Re-check` button inherits that bound, because the
request it makes now has one.

Today nothing bounds what the hub spends. T-14 gave each probe its own timeout — 60s for a `gh` call, 30s
for git — and gave the CLI a 180-second client timeout, but the checks run one after another with no ceiling
on the total. A hub with several sources can hold one request open for minutes, and a founder watching the
dashboard cannot tell a slow doctor from a hung one. That is the whole point: a doctor that hangs is worse
than one that says it could not tell you.

## Context

- `src/Muthur.Server/Services/DoctorService.cs` — `RunAsync` loops over the injected `IDoctorCheck`s,
  awaiting each in turn, and already has the shape this needs: a `try` that turns a check's own exception
  into a `CheckDto("doctor", <type name>, Fail, …)` rather than taking the report down.
- `src/Muthur.Server/Services/CollisionService.cs` — the pattern for a budget in this codebase:
  `new CancellationTokenSource(Budget, clock)` on the **injected** `TimeProvider`, linked to the caller's
  token, with whatever was computed when it runs out being what gets shown.
- `src/Muthur.Server/MuthurOptions.cs` — where a tunable lives; `ConductorIntervalSeconds` and its neighbours
  are the naming and documentation style.
- `src/Muthur.Cli/Commands/SystemCommands.cs` line 81 — `DoctorTimeout` is 180 seconds. The hub's budget must
  stay under it, or the CLI gives up before the report it was promised arrives.
- `src/Muthur.Server/Components/Panels/DoctorPanel.razor` — `RecheckAsync` calls `Doctor.RunAsync(probe: true)`
  directly; there is nothing to change there once the service is bounded.
- `src/Muthur.Server/Infrastructure/Startup.cs` lines 76–83 — `DoctorService` is a singleton and the six
  checks are registered in report order.

Constraints that are not obvious:

- Time comes from the injected `TimeProvider`, never `DateTimeOffset.UtcNow` — which is what lets a test step
  the budget instead of sleeping.
- Tests never sleep to synchronize; anything a test waits for is a condition it can observe or a step of the
  fake clock.
- Warnings are errors.

## Non-goals

- **Changing the `IDoctorCheck` contract.** T-27 offers this as one of two shapes and it is rejected: it
  would touch all six checks, and a check that forgot to honour its deadline could still hang the hub. The
  budget belongs where it can be enforced regardless of what a check does.
- **Per-check timeouts.** T-14 already gave the probes their own, and they stay exactly as they are. This is
  the ceiling on the whole run, which is the thing that is missing.
- **Cancelling a run from the dashboard.** With the request bounded there is nothing left to cancel; a
  `Re-check` that always returns is the fix T-27 asks for.
- Changing what any check reports when it *does* answer.

## Design

### One budget for the run, not one per check

`DoctorService` takes `MuthurOptions`. `RunAsync` opens a budget on the injected clock and links it to the
caller's token:

```csharp
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(options.DoctorBudgetSeconds), clock);
using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
```

A **whole-run** budget, not one per check: six checks each allowed a minute is six minutes, which is the
complaint. The ceiling has to be on the thing the founder is waiting for.

Each check is then raced against the budget rather than merely handed its token, so a check that ignores
cancellation cannot hold the hub either:

```csharp
var expired = Task.Delay(Timeout.InfiniteTimeSpan, clock, budget.Token);   // created once, before the loop
…
var running = check.RunAsync(context, bounded.Token);
if (await Task.WhenAny(running, expired) == running) found.AddRange(await running);
else found.Add(TimedOut(check));
```

A check that loses the race is left running detached. Its eventual result is discarded and its eventual
fault is observed and dropped, so a slow check cannot surface later as an unobserved task exception.

### What an unanswered check says

Two `warn` rows, both `CheckDto("doctor", <check type name>, CheckStatus.Warn, …)` — the same category and
subject the existing self-failure path uses, which already sorts last:

- Raced and lost: `"This check did not answer within the {n}s doctor budget."`
- Never started, because the budget was already spent:
  `"This check was not reached: the {n}s doctor budget was spent before it ran."`

Both are `warn`, not `fail`. A check that did not answer has not told us the hub is broken, and reporting
`fail` would make an overloaded machine look like a misconfigured one.

The caller's own cancellation is still rethrown, unchanged: `OperationCanceledException` propagates when
`ct` is the reason, and is a timeout only when the budget is.

### The number

```csharp
/// <summary>The ceiling on one doctor run. Under the CLI's 180s client timeout, so a bounded hub still answers it.</summary>
public int DoctorBudgetSeconds { get; set; } = 120;
```

120 rather than 60: one `gh` probe is allowed 60 seconds by T-14, and a budget that cannot fit two of them
would report `warn` on an ordinary two-source hub that is merely slow. 120 leaves 60 seconds of headroom
under the CLI's 180.

## Units of work

### Unit A — the budget
- **Files:** `src/Muthur.Server/Services/DoctorService.cs`, `src/Muthur.Server/MuthurOptions.cs`
- **Does:** everything in Design.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean, 0 warnings; Unit B's tests pass.

### Unit B — the tests
- **Files:** `tests/Muthur.Server.Tests/`
- **Does:** over a `DoctorService` built directly from a list of fake checks and the fake clock —
  - A check that never returns is reported `warn` naming the budget once the clock passes it, and the run
    completes. Nothing sleeps: the test advances the clock and waits on the run it can observe.
  - Checks after the one that ate the budget are reported `warn` as not reached, and are never invoked.
  - Checks that answer within the budget are reported exactly as they are today, and a run where nothing is
    slow spends no budget and reports no `warn` of either kind.
  - A check that throws still reports `fail` as it does today — the race must not swallow that path.
  - The caller cancelling still throws, rather than being reported as a timeout.
- **Depends on:** Unit A.
- **Acceptance:** each test fails with Unit A reverted.

## Verification

Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor. Nothing in this task can only be seen by eye, so it is not marked `attended`.

```
dotnet build
dotnet test
```

Passing is 0 warnings, 0 errors, every test green. `DoctorService` is reachable from the test suite directly,
and `GET /doctor` is driven through `HubFactory` like any other route, so there is nothing here a
conductor-started session cannot check.

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3708 Core, 23 Launch, 60 Cli, 280 Server — all green.

With the budget reverted, three of the five new tests fail, and they fail by **hanging**: 30 seconds each,
stopped only by the test's own hang-detector. That is the defect exactly — nothing ended the run — and it is
why those three are worth having. The suite's rule holds: real time is only ever a hang-detector budget here,
and the budget itself is a timer on the injected clock that the test steps.

### One thing the tests found

The first cut raced each check against the budget's own token. `The_caller_giving_up_is_not_a_timeout`
failed with a `TimeoutException`: a caller who walked away was still left waiting, because the check ignored
its token and only the budget could end the race. The race is now against the **linked** token, and which of
the two ended it is decided afterwards — `ct.ThrowIfCancellationRequested()` before the row is written, so a
cancelled request throws and a spent budget reports. Without that, a hub could have honestly reported "this
check did not answer in 120s" to a caller who had given up ten seconds in.
