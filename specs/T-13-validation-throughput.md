# T-13 — Validation throughput: more than one validator, and a queue you can see

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task a validator role can be served by more than one session at once, two validators cannot
spend themselves on the same task, and the depth of the validation queue is a number the founder and the
conductor can both read.

Today the bottleneck is structural, not a matter of nobody being on call. `RoleHold` is keyed on `RoleKey`
— one row, one holder — and `ConductorService.PlanAsync` skips a task whose required role is held **at
all**, by anybody, for any task. So N tasks sitting in `validating` are checked strictly one after another,
and the conductor staffing five orchestrators' work into one role means four tasks wait on nothing.

## The founder's decision

This task's first question was not the implementer's to answer and was not guessed. Founder request #1,
answered 2026-09-18:

> **Option A.** Validator roles get a concurrency limit set at `role define` (default 1, so nothing changes
> until the founder raises it); many agents may hold such a role at once; a per-(task, role) validation claim
> stops two of them taking the same task; non-validator roles like `comms-oncall` stay strictly one holder,
> because "who is on call" must have one answer.

Everything below follows from that and is not open for reinterpretation. The reasoning, so the shape makes
sense while implementing it: a validator role is a *skill* (a platform, a document set), not a seat. The
exclusivity that matters is "no two validators working the same task for the same role" — which is a claim
on the `(task, role)` pair, exactly like the claim a task already has. One-holder-per-role stays the rule
everywhere it is really a seat.

## Context

Read before writing anything:

- `src/Muthur.Server/Services/RoleService.cs` — `DefineAsync`, `TakeAsync`, `ReleaseAsync`,
  `SweepExpiredHoldsAsync`, `ToDto`. Every one of them is written around `SingleOrDefaultAsync(h => h.RoleKey == key)`.
- `src/Muthur.Data/MuthurDb.cs` around line 82 — `RoleHold` has `e.HasKey(x => x.RoleKey); // one holder per role`
  and a `HasOne<Role>().WithOne()` relationship. Both change.
- `src/Muthur.Server/Services/LifecycleService.cs` — `ImplementedAsync` creates the `TaskValidation` rows,
  `PendingAsync` is what `muthur validate list` reads, `VerdictAsync` enforces role-holding and self-validation.
- `src/Muthur.Server/Services/ConductorService.cs` — `PlanAsync`, and the line
  `if (held.Contains(validation.ValidatorKey)) continue;` that is the bottleneck.
- `src/Muthur.Server/Services/RoleLeases.cs` and `AgentService.HeartbeatAsync` — how a lease is renewed by
  signs of life.
- `src/Muthur.Server/Services/LeaseSweeper.cs` — where lapsed leases are reaped.
- `src/Muthur.Server/Services/TaskService.cs` — `ClaimAsync` and `SweepExpiredClaimsAsync` are the model for
  the validation claim: copy their shape, their conflict code, and their ledger events.
- `tests/Muthur.Server.Tests/RoleTests.cs` — `A_role_has_one_holder_and_the_hold_is_a_lease` is the test
  that encodes today's rule. It must survive, restated for a capacity-1 role.
- `src/Muthur.Server/Components/Panels/HeaderStats.razor`, `BoardPanel.razor` — the dashboard patterns.

Constraints that are not obvious from the code:

- Every state change goes through `Ledger.MutateAsync` and records an event in the same transaction.
- Services throw through `Fail.*` with a stable `code`. Rule violation → 422/exit 2, conflict → 409/exit 3.
- Time comes from the injected `TimeProvider`.
- Tests never sleep to synchronize: wait on a condition you can observe, or step the fake clock.
- Warnings are errors.

## Non-goals

- Changing what a verdict *means*, or how many verdicts a task needs. A project still names required
  validator roles and still needs one `yes` from each.
- Letting a non-validator role have more than one holder. That is the explicit other half of the founder's
  answer, and this task enforces it.
- Any change to `ConductorMaxSessions` or to how a validator session is launched. The conductor gets to
  *plan* more sessions; the budget that caps them is untouched.
- Auto-assigning a task to a validator. A validator claims; nothing claims on its behalf.
- Anything that expires or re-routes a task because validation is slow. A queue that is too deep is a fact
  to show, not a thing to silently resolve.
- The `RECEIPTS`-style "time in each state" analysis. That is T-17; this task provides the waiting-since
  timestamp it will need, and stops there.

## Design

### Contracts — `src/Muthur.Contracts/Roles.cs`

`RoleDto` loses its single holder. Replace the existing records with:

```csharp
public sealed record DefineRoleRequest(string Key, string? Brief = null, bool? IsValidator = null, int? Holders = null);

/// <param name="Agent">The agent holding it. One row per live hold.</param>
public sealed record RoleHolderDto(string Agent, DateTimeOffset HeldSince, DateTimeOffset LeaseExpires);

/// <param name="Capacity">How many agents may hold this role at once. Always 1 for a non-validator role.</param>
public sealed record RoleDto(
    string Key,
    bool IsValidator,
    int Capacity,
    IReadOnlyList<RoleHolderDto> Holders,
    bool HasBrief,
    DateTimeOffset UpdatedAt);

public sealed record ClaimValidationRequest(string Validator);
```

`ClaimValidationRequest` belongs to **Unit B**, which is the unit that consumes it; Unit A adds the other
three records and leaves it alone.

`ValidationDto` (in `src/Muthur.Contracts/Tasks.cs`, wherever it is declared today) gains three members,
appended so existing positional uses keep their meaning:

```csharp
    DateTimeOffset WaitingSince,
    string? ClaimedBy,
    DateTimeOffset? ClaimExpires
```

New:

```csharp
/// <param name="Waiting">Tasks in 'validating' with a pending verdict for this role.</param>
/// <param name="Claimed">How many of those a validator has taken.</param>
public sealed record ValidationQueueDto(
    string Role,
    int Capacity,
    int Holders,
    int Waiting,
    int Claimed,
    DateTimeOffset? OldestWaitingSince);
```

Register `RoleHolderDto`, `IReadOnlyList<RoleHolderDto>`, `ClaimValidationRequest`, `ValidationQueueDto`
and `IReadOnlyList<ValidationQueueDto>` in `MuthurJsonContext`.

Add to `src/Muthur.Contracts/Routes.cs`:

```csharp
public const string ValidationQueue = Validations + "/queue";
```

The claim and its release are task actions, reusing `Routes.TaskAction`:
`POST /api/v1/tasks/{id}/validate-claim` and `POST /api/v1/tasks/{id}/validate-release`, both taking a
`ClaimValidationRequest` body.

### Schema

`src/Muthur.Core/Entities/Entities.cs`:

```csharp
// on Role
/// <summary>How many agents may hold this role at once. Only a validator role may exceed 1: "who is on call" has one answer.</summary>
public int Holders { get; set; } = 1;
```

```csharp
// on RoleHold — the class comment changes from "One holder per role" to:
/// <summary>One agent's hold on a role. A role may have up to <see cref="Role.Holders"/> of these; the hold is a lease.</summary>
```

```csharp
// on TaskValidation
/// <summary>When this row started waiting — set when the round opens, never moved by a verdict.</summary>
public DateTimeOffset WaitingSince { get; set; }
/// <summary>The validator who has taken this (task, role) pair. Nobody else may spend a session on it.</summary>
public Guid? ClaimedByAgentId { get; set; }
public Agent? ClaimedBy { get; set; }
public DateTimeOffset? ClaimExpires { get; set; }
```

`src/Muthur.Data/MuthurDb.cs`:

```csharp
modelBuilder.Entity<RoleHold>(e =>
{
    e.ToTable("role_holds");
    e.HasKey(x => new { x.RoleKey, x.AgentId });   // a role may have several holders; an agent holds it once
    e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleKey).OnDelete(DeleteBehavior.Cascade);
    e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
});
```

`TaskValidation` gains `e.HasOne(x => x.ClaimedBy).WithMany().HasForeignKey(x => x.ClaimedByAgentId).OnDelete(DeleteBehavior.SetNull);`
beside the existing `Agent` relationship.

One migration for all of it:

```
dotnet ef migrations add ValidatorConcurrency -p src/Muthur.Data -s src/Muthur.Data -o Migrations
```

`dotnet ef` needs `dotnet tool restore` and a build first in a fresh worktree.

Existing rows: `Role.Holders` defaults to 1, so every role defined before this change keeps exactly today's
behaviour. `TaskValidation.WaitingSince` needs a value for rows that already exist — the migration sets it
to the row's `At` where `At` is not null, and otherwise to the moment the migration runs. Say so in a
comment in the migration; a `validating` task from before this change reporting its wait as "since the
upgrade" is honest, and there are at most a handful.

### Roles with capacity — `RoleService`

**`DefineAsync`** takes `request.Holders`:

- `null` → leave the current value (1 for a role being created).
- `< 1` → `Fail.Rule("invalid_holders", "A role needs at least one holder.")`
- `> 1` on a role that is not (and is not becoming) a validator →
  `Fail.Rule("single_holder", "Only a validator role may have more than one holder: '<key>' is a standing post, and who holds it has to have one answer.")`
  Evaluate this against the role's `IsValidator` **after** `request.IsValidator` has been applied, so
  `role define x --validator true --holders 3` in one call works.
- Lowering `Holders` below the number of live holds is allowed and removes nobody. Existing holds stand
  until they lapse or are released; the new ceiling applies to the next `take`. The `role.updated` payload
  carries `holders` so the ledger shows when it changed.

**`TakeAsync`** replaces the single-hold lookup:

- Load the role. Load *all* unexpired holds for it.
- If one of them is mine → renew it, no ledger event (today's "taking a role you already hold" rule).
- Else if `unexpiredHolds.Count >= role.Holders` → `Fail.Conflict("role_held", …)`:
  - **exactly one live holder**: `"Role '<key>' is held by '<holder>' until <lease:O>."` — **unchanged from
    today**, so the existing message and test survive.
  - **more than one live holder**: `"Role '<key>' is full: <n> of <capacity> held by <comma-separated holders>."`

  Branch on the number of live holders, **not** on `role.Holders`. The spec allows a founder to lower a
  capacity below the number of standing holds, and a capacity-1 role with two holders left over from a
  capacity-3 era would otherwise print the single-holder message and name only the oldest of them — telling
  an agent to wait for one lease when two stand in its way.
- Else add a hold and record `role.taken` with `{ role, agent, holders = <live count after>, capacity }`.
  The `tookOverFrom` field disappears — with capacity, taking a role never displaces anyone.

**`ReleaseAsync`**: releases *the caller's* hold. Not held by the caller → `Fail.Rule("not_holder", …)`
naming who does hold it. The founder releasing a role removes **every** hold on it, and records one
`role.released` per hold. Nothing is held at all → `Fail.Rule("role_not_held", "Role '<key>' is not held by anyone.")`,
as today.

**`SweepExpiredHoldsAsync`** already operates on a list; it needs no change beyond compiling against the
new key.

**`ListAsync` / `ToDto`** return every live hold, ordered by `AcquiredAt` then agent name, plus `Capacity`.

**`RoleLeases`**: `HoldsAsync` and `HeldByAgentAsync` already query by `AgentId` and need no change.
Add `public static Task<int> LiveHolderCountAsync(MuthurDb db, string roleKey, DateTimeOffset now, CancellationToken ct)`
for the callers that only need the number.

### The validation claim — `LifecycleService`

**`ImplementedAsync`**: each `TaskValidation` it creates gets `WaitingSince = m.Now` and no claim.

**New `ClaimValidationAsync(Caller caller, string id, ClaimValidationRequest request, CancellationToken ct)`:**

- `caller.RequireIdentified()`; normalize the validator key the way `VerdictAsync` does.
- Task not `Validating` → `Fail.Rule("not_validating", …)` with `VerdictAsync`'s existing wording.
- No `TaskValidation` row for that key → `Fail.Rule("validator_not_required", …)`, as `VerdictAsync` says it.
- Row's `Verdict` is not `Pending` → `Fail.Rule("already_decided", "'<validator>' has already given a verdict on <T-n>.")`
- Caller does not hold the role → `Fail.Rule("role_not_held", "You do not hold the '<validator>' role. Take it first: muthur role take <validator>")`
- Caller owns the task → `Fail.Rule("self_validation", "You own this task. Validation must come from someone who did not build it.")`
  — the same rule `VerdictAsync` enforces, moved earlier so a validator finds out before it spends a session.
- Claimed by someone else and not yet lapsed → `Fail.Conflict("validation_claimed", "<T-n> is being validated for '<validator>' by '<agent>' until <expires:O>.")`
- Otherwise set `ClaimedByAgentId` and `ClaimExpires = m.Now + leases.ClaimLease`, and record
  `validation.claimed` with `{ validator, by }`. Claiming one you already hold just renews it, with no event.

**New `ReleaseValidationAsync`**: the claimer or the founder clears the claim and records
`validation.released` with `{ validator, by }`. Not claimed by the caller and the caller is not the founder
→ `Fail.Rule("not_claimer", …)`.

**`VerdictAsync`** gains one rule, placed with the other checks on the row: if the row is claimed by a live
claim belonging to someone else →
`Fail.Conflict("validation_claimed", "<T-n> is being validated for '<validator>' by '<agent>'.")`.
An unclaimed row is claimed implicitly by the verdict — a single validator needs no extra command — so set
`ClaimedByAgentId` to the caller before writing the verdict. After a verdict is written, clear the claim
(`ClaimedByAgentId = null; ClaimExpires = null`) on that row: it is decided and nobody is working it.

When a failed verdict sends the task back to `InProgress`, clear the claim on **every** row of that task —
the round is over.

**`PendingAsync(string? validatorKey, bool includeClaimed, CancellationToken ct)`**: with `includeClaimed`
false (the default), a task whose row for that role carries a live claim held by someone else is left out —
`validate list` shows a validator what it can actually take. Passing no `--role` means no claim filtering
can be meaningful, so `includeClaimed` is ignored when `validatorKey` is null.

**New `QueueAsync`**: one `ValidationQueueDto` per role that is `IsValidator`, ordered by `Waiting`
descending then `Role` ordinal. `Waiting` counts `Pending` rows on tasks in `Validating`; `Claimed` counts
those with a live claim; `Holders` counts live holds; `Capacity` is `Role.Holders`; `OldestWaitingSince` is
the smallest `WaitingSince` among the waiting rows, or null when there are none. A role with no live holds
and nothing waiting still appears, with zeros — a queue view that hides idle roles cannot show you that the
role nobody holds is also the role nothing is waiting for.

**Claim renewal and expiry**: wherever `RoleLeases.RenewAsync` is called today (`AgentService.HeartbeatAsync`),
also extend every live validation claim held by that agent to `m.Now + leases.ClaimLease`, the same way the
task claims immediately above it are extended. Add `LifecycleService.SweepExpiredValidationClaimsAsync`,
modelled on `TaskService.SweepExpiredClaimsAsync`: clear claims whose `ClaimExpires <= now`, record
`validation.claim_expired` with `{ validator, agent }` per row, return the count. Call it from
`LeaseSweeper` beside the two sweeps already there, logging
`"Released {Count} validation claim(s) whose holders went quiet."`.

### The conductor — `ConductorService.PlanAsync`

Replace the role-is-held test with capacity and claims. Inside the read, alongside what it already loads:

- live hold counts per role key,
- the set of `(TaskId, ValidatorKey)` pairs with a live claim.

Then, per pending validation:

- skip when the pair has a live claim — someone is already on it;
- skip when `liveHolders[role] >= role.Holders` — the role is full, and a session that cannot take the role
  is a session that does nothing;
- otherwise plan it, and **count it against that role for the rest of this pass**, so one pass does not plan
  three sessions into two free slots.

Everything else in `PlanAsync` — priority order, the failure ceiling, `_running`, the stall cooldown,
`ConductorMaxSessions` — is unchanged.

The validator prompt in `ValidatorSessionLauncher` gains one line after the `role take` lines:

```
    muthur validate claim {assignment.TaskKey} --as {assignment.RoleKey}
```

and one sentence in the rules: `- Claim the task before you start. If the claim is refused, another validator has it: release the role and stop.`

### CLI — `src/Muthur.Cli/Commands/RoleCommands.cs`

- `role define` gains `--holders <n>`:
  `"How many agents may hold this role at once (default 1). Only a validator role may exceed 1."`
- `validate list` gains `--all`: `"Include tasks another validator has already claimed."` It adds
  `all=true` to the query. Build the query the way `TaskCommands.list` and `InboundCommands.list` already do
  — a list of `key=value` joined with `&` and prefixed with `?` only when non-empty — so `--all` alone
  yields `?all=true` and not a query beginning with `&`. `--role` is escaped with `Uri.EscapeDataString`,
  as it is today.
- New `validate claim <id> --as <role>`:
  `"Take a task for validation so no other validator spends a session on it. Exit 3 if someone already has it."`
- New `validate release <id> --as <role>`: `"Give back a task you claimed but will not validate."`
- New `validate queue`: `"How deep the validation queue is, per validator role."` `GET Routes.ValidationQueue`.

All five follow the existing `Output.Emit` pattern exactly; none of them interprets the body.

### Dashboard

**New `src/Muthur.Server/Components/Panels/ValidationQueuePanel.razor`** — `@inherits LivePanel`,
`@inject LifecycleService Lifecycle`, `@inject TimeProvider Clock`.

- `LoadAsync` calls `Lifecycle.QueueAsync()`.
- `ClockInterval => TimeSpan.FromSeconds(30)` — the oldest wait ages on its own.
- `IsRelevant`: types starting (ordinal) with `validation.`, `task.` or `role.`.
- Head: title `Validation queue`; sub `<total waiting> waiting`, or `clear` when nothing is.
- Body: one `.role-row` per role — a `.tag` reading `<holders>/<capacity>` (class `tag-held` when
  `Holders > 0`, `tag-open` when it is 0 and `Waiting > 0`, plain `tag` otherwise), the role key, and in a
  `.role-holder` either `<waiting> waiting · oldest <age>` using `Format` the way `HarnessPanel` uses
  `Format.Until`, or `clear`. `.empty` reading `No validator roles defined.` when there are none.
- Place it in the right-hand aside of `src/Muthur.Server/Components/Pages/Board.razor`, above whatever is
  there. If that page's asides are already full, put it at the top of the right aside anyway — the queue
  depth is the number this task exists to make visible.

**`HeaderStats.razor`**: the existing `@_validating validating` stat gains the wait behind it. Load
`Lifecycle.QueueAsync()`, take the smallest non-null `OldestWaitingSince`, and render
`<b>@_validating</b> validating` followed by ` · oldest @Format.Age(oldest, _now)` when there is one.
Keep `stat-accent` on the same condition as today. `Format.Age(then, now)` in `Components/Shared/Format.cs`
is already the "how long ago" helper — it is what `AgentsPanel` and `CommsPanel` use — so use it and add
nothing. Do not format inline.

No new CSS classes. Everything above uses classes that already exist in `wwwroot/app.css`.

## Units of work

### Unit A — capacity on roles
- **Files:** `src/Muthur.Contracts/Roles.cs`, `MuthurJsonContext.cs`; `src/Muthur.Core/Entities/Entities.cs`
  (`Role.Holders`, the `RoleHold` comment); `src/Muthur.Data/MuthurDb.cs` (`RoleHold` key and relationship);
  a migration; `src/Muthur.Server/Services/RoleService.cs`, `RoleLeases.cs`;
  `src/Muthur.Server/Components/Panels/CommsPanel.razor` if it fails to compile;
  `tests/Muthur.Server.Tests/RoleTests.cs`.
- **Does:** `Holders` on `Role`, the multi-holder `RoleHold`, `DefineAsync` / `TakeAsync` / `ReleaseAsync` /
  `ListAsync` as specified, the new `RoleDto` shape, and `LiveHolderCountAsync`.
- **Depends on:** nothing. Do this one first: B and C compile against `RoleDto` and `Role.Holders`.
- **Acceptance:** `dotnet build` and `dotnet test` clean. `RoleTests.A_role_has_one_holder_and_the_hold_is_a_lease`
  still passes, restated against `Holders`/`Capacity`. New tests: a validator role at `--holders 2` admits
  two agents and refuses the third with `role_held` and exit 3; `--holders 2` on a non-validator role is
  refused with `single_holder`; `--holders 0` is refused with `invalid_holders`; lowering the capacity
  evicts nobody; each holder releases only their own hold, and the founder's release clears all of them.

### Unit B — the validation claim
- **Files:** `src/Muthur.Core/Entities/Entities.cs` (`TaskValidation`); `src/Muthur.Data/MuthurDb.cs`;
  the same migration as Unit A if A has not run yet, otherwise a second one;
  `src/Muthur.Server/Services/LifecycleService.cs`, `Validations.cs`, `AgentService.cs` (renewal),
  `LeaseSweeper.cs`; `src/Muthur.Server/Api/RoleEndpoints.cs` and/or `TaskEndpoints.cs` for the two routes;
  `src/Muthur.Contracts/Tasks.cs` (`ValidationDto`); `tests/Muthur.Server.Tests/`.
- **Does:** `WaitingSince`, the claim columns, `ClaimValidationAsync`, `ReleaseValidationAsync`, the new rule
  and the claim-clearing in `VerdictAsync`, `PendingAsync`'s filter, renewal, and the sweep.
- **Depends on:** Unit A (for `RoleLeases.LiveHolderCountAsync` and the role-holding check).
- **Acceptance:** `dotnet build` and `dotnet test` clean. Tests: two agents holding the same capacity-2 role,
  the first claims a task, the second's claim is refused with `validation_claimed` and 409; the second's
  *verdict* on that task is refused the same way; `validate list --role x` hides the claimed task for the
  second agent and shows it with `--all`; a claim lapses on the fake clock and the sweep frees it, with a
  `validation.claim_expired` event; an unclaimed verdict still works in one step; a failed verdict clears
  every claim on the task; claiming a task you own is refused with `self_validation`.

### Unit C — the conductor plans against capacity
- **Files:** `src/Muthur.Server/Services/ConductorService.cs`, `ValidatorSessionLauncher.cs`;
  `tests/Muthur.Server.Tests/` (the conductor tests).
- **Does:** the `PlanAsync` change and the two lines in the validator prompt.
- **Depends on:** Units A and B.
- **Acceptance:** `dotnet build` and `dotnet test` clean. Tests: with a capacity-2 role and three tasks in
  `validating`, one pass plans exactly two assignments, on the two highest-priority tasks; with capacity 1 it
  plans exactly one, as today; a task whose pair is already claimed is not planned; a role whose holds are
  all live at capacity is not planned; `ConductorMaxSessions` still caps the total below capacity.

### Unit D — CLI
- **Files:** `src/Muthur.Cli/Commands/RoleCommands.cs`.
- **Does:** `--holders`, `validate claim`, `validate release`, `validate queue`, `validate list --all`.
- **Depends on:** Units A and B for the contracts; it can be written against them as soon as those compile.
- **Acceptance:** `dotnet build` and `dotnet test` clean; verified by hand per **Verification**.

### Unit E — the queue on the dashboard
- **Files:** new `src/Muthur.Server/Components/Panels/ValidationQueuePanel.razor`; modified
  `src/Muthur.Server/Components/Pages/Board.razor`, `Panels/HeaderStats.razor`, and
  `Components/Shared/Format.cs` if it needs a "how long ago" helper.
- **Does:** the panel and the header stat.
- **Depends on:** Unit B (`QueueAsync`).
- **Acceptance:** `dotnet build` and `dotnet test` clean; verified by eye per **Verification**.

## Verification

```
dotnet build
dotnet test
```

Both clean — warnings are errors.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t13
$env:MUTHUR_HOME = "$PWD/artifacts/t13-home"; $env:MUTHUR_URL = "http://127.0.0.1:7432"
./artifacts/t13/muthur.exe up
```

Then, as the founder and two registered agents:

- `role define win-validator --validator true --holders 2 --founder` is accepted; the same with
  `comms-oncall` is refused with `single_holder` and exit 2.
- Two agents both `role take win-validator` and both succeed. A third is refused with `role_held`, exit 3,
  and the message names both holders and the capacity.
- With two tasks in `validating`, each validator claims a different one and both work at once — the thing
  that was impossible before this task.
- The second validator claiming the first's task is refused with `validation_claimed` and exit 3, and so is
  a verdict on it.
- `muthur validate queue` shows `win-validator` with `holders 2`, `capacity 2`, the waiting count, and an
  `oldestWaitingSince` that matches when those tasks were marked implemented.
- `muthur validate list --role win-validator` as the second validator does not list the task the first
  claimed; `--all` lists it.
- On the dashboard, the Board page's validation-queue panel shows the same numbers live, and the header's
  `validating` stat carries the oldest wait beside it.
- Turn the conductor on with two tasks waiting and capacity 2: `muthur conductor status` shows two sessions
  running rather than one, and `lastAction` names the second task.

## Out of scope / follow-ups

- **Capacity the conductor sets itself.** A role whose queue is consistently deep could have its capacity
  raised automatically. It should not, until a founder has watched it a while; file it if it proves out.
- **Time-in-state analysis** — how long tasks sit in `validating` versus being worked, which is the number
  that proves or disproves PLAN.md's claim that validation is the bottleneck. `WaitingSince` is what that
  needs; the analysis is T-17.
- **T-16** (two tasks in flight on the same files) becomes more likely the moment several validators land
  work in parallel. Nothing here makes it worse than the conductor already does, and nothing here addresses it.
- `HeaderStats` has no `IsRelevant` override, so it reloads on every ledger event, and this task gives it a
  second query to run each time. Harmless at present scale, and the cheap fix is an `IsRelevant` override on
  that component — worth doing if the board ever feels slow, not worth doing on suspicion.
- `muthur role list` output changed shape (`holder` → `holders[]`, plus `capacity`). Any brief in `kit/` or
  prose in `docs/` that quotes the old shape should be swept — check `kit/briefs/validator.md` and
  `kit/core/`. If a sweep is needed beyond one or two lines, file it rather than widening this task.
