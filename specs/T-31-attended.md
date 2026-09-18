# T-31 — muthur task attended: a task can say it needs a human, reversibly and with its reason

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task a task can carry the fact that it needs a human validator — one with a browser, a device, or
their hands — together with the reason. The conductor does not staff it, the board shows it waiting on a
person and says why, and the flag can be lifted the moment the reason stops being true.

T-30 closed the loop: a blocked task leaves `validating`, so it is never restaffed. This is the
optimisation on top of that — it stops the organization paying one session per attended task, every round,
to rediscover something a validator already told it once.

## The founder's decision

Founder request #4, answered 2026-09-18. Quoted so it is not re-litigated:

> Nobody can predict at spec time which tasks need eyes — I did not predict it for T-14, and I wrote the
> brief that made the validator refuse. A is the only option that learns from evidence rather than from
> guessing, and once 'blocked' is a verdict the cost of learning is bounded at exactly one session per task,
> never a loop.
>
> The addition: `muthur task attended T-n` takes a reason and `muthur task attended T-n --clear` removes the
> flag. A task needs a human because of how the product is today, not forever — if the Doctor panel later
> exposes its state through the API or server-rendered HTML, T-14 stops needing a browser, and a flag nobody
> can lift would outlive the fact that set it. Record the reason on the task, and show it on the board where
> the task is waiting on a human, so the next person sees why rather than just that.

Three shapes were rejected with reasons, which stand: a project-level setting (most MUTHUR tasks are
CLI-only); attended-ness as a property of the validator role (T-14 has CLI-testable parts and browser-only
parts, so it means either two validators for one task or the CLI half unchecked); and a founder request per
blocked verdict (Needs You must stay expensive).

## Context

- `src/Muthur.Core/Entities/Entities.cs` — `WorkTask`.
- `src/Muthur.Server/Services/TaskService.cs` — `RequireOwnerOrFounder` (line 112 and its neighbours) is the
  rule to follow; `SetPriorityAsync` and `SetSpecAsync` are the shape to copy for a small mutation.
- `src/Muthur.Server/Services/ConductorService.cs` — `PlanAsync`. As of T-30 it loads tasks in `Validating`
  and filters them; this adds one more filter.
- `src/Muthur.Cli/Commands/TaskCommands.cs` — how a task subcommand is built and wired.
- `src/Muthur.Server/Components/Shared/TaskCard.razor` — the card's pills, including
  `<span class="pill pill-blocked">waiting on founder</span>`, which is the closest existing thing.
- `src/Muthur.Server/Components/Pages/TaskDetail.razor` — the `.kv` definition list at line 27.
- `src/Muthur.Contracts/Tasks.cs` — `TaskDto`; `src/Muthur.Server/Services/Mapper.cs` — `ToDto`.

Constraints that are not obvious:

- Every state change goes through `Ledger.MutateAsync` and records a ledger event in the same transaction.
- Services throw `MuthurException` via `Fail.*` with a stable `code`; rule violation → 422/exit 2.
- Time comes from the injected `TimeProvider`.
- Everything crossing HTTP is registered in `MuthurJsonContext`; the CLI is Native AOT.
- Warnings are errors.
- **`PlanAsync` is being changed on another unlanded branch too** (T-13, `task/T-13-validator-throughput`,
  which makes it plan against role capacity). Keep this change to a single filter in the task loop so the
  two merge cleanly. Do not restructure the method.

## Non-goals

- Anything that sets the flag automatically. A blocked verdict does not set it; a human decides, having read
  the evidence. That is the whole difference between this and the option the founder rejected.
- Putting attended tasks into the Needs You count or page. The founder rejected a founder-request-per-block
  because that queue must stay expensive, and an attended task is not waiting on the founder specifically —
  it is waiting on any human. T-18 may revisit it; this task must not.
- Any change to `blocked`, to validation, or to what the conductor does with tasks it *does* staff.
- Choosing *who* the human is, scheduling them, or notifying anyone.

## Design

### Schema

`WorkTask` gains one nullable field — the flag and its reason are the same thing, so a flag without a
reason cannot exist:

```csharp
/// <summary>Why this task needs a human validator. Null means it does not; the conductor staffs it as usual.</summary>
public string? AttendedReason { get; set; }
```

Migration:

```
dotnet ef migrations add TaskAttendedReason -p src/Muthur.Data -s src/Muthur.Data -o Migrations
```

Existing rows get `NULL`, which is "not attended" — nothing changes for any task already in flight. **Read
what `dotnet ef` scaffolds before committing it**; on this project a scaffolded column default has been
wrong twice.

### Contracts

`TaskDto` gains `string? AttendedReason` **appended after `Validations`**, so no positional construction
site shifts. New request record and route:

```csharp
/// <param name="Reason">Why a human is needed. Null clears the flag.</param>
public sealed record AttendedRequest(string? Reason);
```

Register `AttendedRequest` in `MuthurJsonContext`. The action is `POST /api/v1/tasks/{id}/attended` through
the existing `Routes.TaskAction(id, "attended")`, mapped in `TaskEndpoints.cs` beside the other task actions.

### `TaskService.SetAttendedAsync`

```csharp
public Task<TaskDto> SetAttendedAsync(Caller caller, string id, AttendedRequest request, CancellationToken ct = default)
```

Inside `ledger.MutateAsync`, after `LoadAsync` and `RequireOwnerOrFounder(task, caller)`:

- Setting (`request.Reason` is non-whitespace): trim it. If the trimmed reason is empty or the property is
  absent while the caller meant to set it, that is the CLI's `--reason` being missing — see below.
- Clearing (`request.Reason` is null or whitespace): `task.AttendedReason = null`.
- Record only when the value actually changes: `task.attended` with
  `new { reason = task.AttendedReason }`, or `task.attended_cleared` with `new { was }`. Setting the same
  reason twice, or clearing an already-clear task, succeeds and records nothing — it is an assertion about
  the world, not a toggle, and repeating it is not an error.
- `task.UpdatedAt = m.Now` whenever the value changes.
- Return `task.ToDto(...)` the way the neighbouring methods do.

There is no state rule: a task may be marked attended in any state. A backlog task whose owner already knows
it will need a browser is exactly the case worth recording early.

### The conductor does not staff it

In `ConductorService.PlanAsync`, inside the loop over tasks, beside the existing failure-ceiling check:

```csharp
// A human has said this one needs them. Staffing it spends a session to be told what the task already says.
if (task.AttendedReason is not null) continue;
```

One line, one comment, nothing else in the method moves.

### CLI

In `TaskCommands`, beside `priority`:

```
muthur task attended <id> --reason "<why>"
muthur task attended <id> --clear
```

- Command description: `"Say that this task needs a human validator, and why. --clear lifts it when the reason stops being true."`
- `--reason`: `"Why a human is needed: a browser, a device, your hands. Required unless --clear."`
- `--clear`: `"Lift the flag. The conductor staffs the task again."`
- Neither given → `Output.Error("reason_required", "Say why this task needs a human: --reason \"<why>\", or --clear to lift it.", ExitCodes.RuleViolation)` without calling the hub. Both given → the same error, wording
  `"Pass --reason or --clear, not both."`
- `--clear` sends `new AttendedRequest(null)`; `--reason x` sends `new AttendedRequest(x)`.

### The board says so, and says why

`TaskCard.razor`, beside the existing `Blocked` pill:

```razor
@if (Task.AttendedReason is not null)
{
    <span class="pill pill-blocked">needs a human</span>
}
```

`TaskDetail.razor`: in the `.kv` list, a `needs a human` row whose value is the reason, rendered only when
`AttendedReason is not null`. The card says *that*; the detail page says *why*, which is what the founder
asked for — a card has no room for a sentence and a truncated reason is worse than a link to it.

No new CSS. `pill-blocked` is the amber "a human must act" token and this is the same meaning.

## Units of work

### Unit A — the whole task
- **Files:** `src/Muthur.Core/Entities/Entities.cs`; a migration under `src/Muthur.Data/Migrations/`;
  `src/Muthur.Contracts/Tasks.cs`, `MuthurJsonContext.cs`; `src/Muthur.Server/Services/TaskService.cs`,
  `Mapper.cs`, `ConductorService.cs`; `src/Muthur.Server/Api/TaskEndpoints.cs`;
  `src/Muthur.Cli/Commands/TaskCommands.cs`; `src/Muthur.Server/Components/Shared/TaskCard.razor`,
  `Components/Pages/TaskDetail.razor`; `tests/Muthur.Server.Tests/`.
- **Does:** everything above.
- **Depends on:** nothing. T-30 is landed on `main`.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. Setting the flag with a reason records `task.attended`, and `task show` carries the reason.
  2. Setting it with no reason is refused 422 `reason_required`; the task is unchanged.
  3. `--clear` lifts it, records `task.attended_cleared`, and the conductor plans it again.
  4. **The conductor does not plan an attended task**, and does plan an otherwise identical one that is not
     attended — assert both in one test so the flag is what differs. Mutation-check it: with the `continue`
     removed the test must fail.
  5. Only the owner or the founder may set or clear it; another agent is refused.
  6. Setting the same reason twice records one event, not two; clearing an already-clear task is not an error.
  7. The board card shows `needs a human` for an attended task and the detail page shows the reason.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub. **No browser is required and
none should be used** — the dashboard prerenders, so `GET` of a page carries what it shows:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t31
$env:MUTHUR_HOME = "$PWD/artifacts/t31-home"; $env:MUTHUR_URL = "http://127.0.0.1:7439"
./artifacts/t31/muthur.exe up
```

With a project requiring a validator, a task implemented and waiting in `validating`:

- `muthur task attended T-n --reason "the spec needs a live browser"` is accepted; `muthur task show T-n`
  carries the reason; `GET /` contains `needs a human` on that task's card; `GET /tasks/T-n` contains the
  reason in full.
- `muthur task attended T-n` with neither flag → `reason_required`, exit 2, and the hub was not called.
- Turn the conductor on with only that task waiting: `muthur conductor status` shows it staffing nothing,
  and no `conductor.staffing` event appears for it. Then `muthur task attended T-n --clear` and confirm the
  next pass does staff it. (Use a stand-in harness on PATH if you do not want a real session spent — the
  validator brief documents how.)
- `muthur task attended T-n --reason "x" --clear` → refused, exit 2, hub not called.

## Out of scope / follow-ups

- Whether an attended task belongs in Needs You, and how it is surfaced when a queue is two hundred long, is
  **T-18**.
- Nothing tells a human that an attended task is waiting for them. Today they see it on the board. A
  standing "these need a person" view, or a nudge, is worth its own task once there is more than one.
- `blocked` and `attended` are related but independent: a validator blocks, a human reads the evidence and
  decides whether the task is attended-forever or attended-for-now. If that handoff turns out to be missed
  in practice, a prompt at blocked-verdict time is the obvious next step — and it is the one the founder
  rejected as automatic, so it would have to be a suggestion, not an action.
