# T-30 — A validator that reports blocked is heard once, not restaffed forever

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a validator that cannot do its job has somewhere to put that fact: `blocked` is a verdict
recorded on the task, and the task leaves `validating` and goes back to its owner with the reason attached.
And a session that ends without recording any verdict — for that reason or any other — is counted, so the
conductor stops after `ConductorMaxAttempts` instead of restaffing the same impossible task until the
founder's accounts are empty.

The conductor is **off** until this lands. Re-enabling it is the founder's call, not this task's.

## What happened, and why nothing caught it

T-14's frozen spec required verifying a dashboard panel in a live browser. A conductor-started session has
no browser. The validator did exactly what `kit/briefs/validator.md` tells it to:

> If the product cannot be driven unattended, the verdict is **not** pass: message the task's owner, say
> what stopped you, and release the role.

It reported blocked on the bus, released the role, and exited cleanly. The conductor then saw a task in
`validating` whose required role nobody held — which is precisely what it exists to fill — and started
another. **Five sessions in twenty-eight minutes**, each a full harness run on the founder's subscription,
each ending in the same correct refusal. `conductor.failed`: 0. `conductor.stalled`: 0. Verdicts recorded: 0.

Both existing caps missed it, and neither is wrong:

- The **launch-failure** cap needs `ValidatorLaunchException` → `conductor.failed`. The launch succeeded.
- The **attempt** cap in `EscalateExhaustedAsync` counts `validation.failed` events. No verdict was recorded.

A session that runs perfectly and deliberately produces nothing is, to the conductor, indistinguishable
from progress. The validator knew exactly what was wrong and said so five times; the system had nowhere to
put it.

## Context

- `src/Muthur.Server/Services/LifecycleService.cs` — `VerdictAsync(caller, id, request, pass, ct)` is the
  whole of Part 1. Read its existing rules: evidence required on a fail, role must be held, an owner may
  not validate their own work, and a failed verdict returns the task to `InProgress` with a fresh claim.
- `src/Muthur.Server/Api/RoleEndpoints.cs` lines 33–37 — the two verdict routes, both calling `VerdictAsync`
  with a `pass:` bool.
- `src/Muthur.Contracts/Enums.cs` — `Verdict`, its `[JsonStringEnumMemberName]` attributes, and `ToWire`.
- `src/Muthur.Server/Services/ConductorService.cs` — `RunSessionAsync`, the `Stall` record, `_launchFailures`,
  the half-open probe, and `EscalateExhaustedAsync`. Part 2 lives here.
- `src/Muthur.Cli/Commands/RoleCommands.cs` — the private `Verdict(name, description)` factory builds each
  verdict command and posts to `Routes.TaskAction(id, name)`.
- `kit/briefs/validator.md` (line 48) and `kit/core/validate.md` (lines 27–28, 49) — the procedure that
  currently tells a validator to send a message and release. Part 3.

Constraints that are not obvious:

- Every state change goes through `Ledger.MutateAsync` and records a ledger event in the same transaction.
- Services throw `MuthurException` via `Fail.*` with a stable `code`. Rule violation → 422/exit 2, conflict
  → 409/exit 3.
- Time comes from the injected `TimeProvider`.
- `ConductorService` holds its counters in memory, guarded by `lock`. The hub is a local process that is off
  for hours; these counters are deliberately not persisted, and this task does not change that.
- Warnings are errors.

## Non-goals

- **Declaring in advance that a task needs an attended validator.** That is the half of T-30 that belongs to
  the founder; founder request #4 is open on it. Once `blocked` is a verdict the loop is closed either way —
  a blocked task leaves `validating` and is never restaffed — so this task does not wait on that answer, and
  does not pre-empt it.
- Re-enabling the conductor. A founder turns it on.
- Persisting the conductor's counters across a hub restart.
- Changing `ConductorMaxAttempts`, `ConductorMaxSessions`, or the half-open probe interval.
- Any change to what `pass` or `fail` mean.

## Design

### Part 1 — `blocked` is a verdict

**`src/Muthur.Contracts/Enums.cs`:**

```csharp
public enum Verdict
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("yes")] Yes,
    [JsonStringEnumMemberName("no")] No,
    /// <summary>The validator could not do the job at all: it is not a judgement on the work.</summary>
    [JsonStringEnumMemberName("blocked")] Blocked,
}
```

and `ToWire` gains `Verdict.Blocked => "blocked"`.

**`LifecycleService.VerdictAsync`** changes signature from `bool pass` to the outcome itself:

```csharp
public Task<TaskDto> VerdictAsync(Caller caller, string id, VerdictRequest request, Verdict outcome, CancellationToken ct = default)
```

`outcome` is only ever `Verdict.Yes`, `Verdict.No` or `Verdict.Blocked`; anything else is a programming
error and may be an `ArgumentOutOfRangeException`.

Rules, in the order the method already checks things:

- Evidence is required for `No` **and** for `Blocked`. The existing `evidence_required` message stays for
  `No`; for `Blocked` it reads
  `"A blocked validation needs evidence: what you tried, what stopped you, and what would let the next validator get further."`
- Everything else — `not_validating`, `validator_not_required`, `role_not_held`, `self_validation` — applies
  unchanged to all three outcomes.
- `row.Verdict = outcome`, with evidence, agent and time set exactly as today.
- The ledger event is `validation.passed`, `validation.failed`, or **`validation.blocked`** with payload
  `new { validator, by = caller.Name, evidence = request.Evidence }`.

Then, after `SaveChangesAsync`:

- **`Blocked`** behaves like a failure as far as the task is concerned, and unlike one in what it says:
  - `task.State = TaskState.InProgress`, `task.ClaimExpires = m.Now + leases.ClaimLease` — the same
    return-to-owner the failure path uses, and the reason this closes the loop: the conductor only staffs
    `Validating`, so a blocked task is never restaffed.
  - Record `task.validation_blocked` with `new { validator, owner = task.Owner?.Name }`.
  - Message the **owner**:
    `"{T-n} could not be validated by {validator}: it is back in progress with you. This is not a verdict on the work. Why: muthur task show {T-n}"`
  - Message the **founder** as well, via `MessageService.PostFromHub(m, Recipient.Founder, null, …)`:
    `"{T-n} \"{title}\" needs something a validator could not supply: {first line of evidence}. It is back with {owner}. Full evidence: muthur task show {T-n}"`
    Use `ConductorService.FirstLineOfEvidence`-style truncation — one line, capped at 140 characters. It is
    already `internal static` and takes a payload JSON string; if reusing it means moving it somewhere both
    services can see, move it to a small `internal static class Evidence` in `Muthur.Server/Services/` and
    update `ConductorService` to call it there. Do not duplicate it.

    The founder is told because "this task needs something the organization cannot supply unattended" is a
    fact only they can act on, and the observed failure is precisely that this information reached one
    agent's inbox and nowhere else.
- `Yes` and `No` behave exactly as they do today, including the all-yes → `Validated` transition. A
  `Blocked` row must **not** count as a yes: the `rows.All(v => v.Verdict == Verdict.Yes)` test already
  handles this correctly and needs no change — confirm it rather than editing it.

**`EscalateExhaustedAsync`** keeps counting `validation.failed` only. A blocked task has left `validating`,
so it is not a candidate, and conflating "three validators judged this bad" with "one validator could not
run" would tell the founder the wrong thing.

**`src/Muthur.Server/Api/RoleEndpoints.cs`:** the two existing routes pass `Verdict.Yes` / `Verdict.No`, and
a third is added for `Routes.TaskAction(id, "blocked")` passing `Verdict.Blocked`.

**CLI**, in `RoleCommands.AddValidate`, through the existing `Verdict(name, description)` factory:

```csharp
validate.Subcommands.Add(Verdict("blocked", "You could not validate it at all — say what stopped you. The task returns to its owner; this is not a verdict on the work."));
```

The factory already posts to `Routes.TaskAction(id, name)`, so the route name `blocked` is all it needs.

### Part 2 — a cap for sessions that end without a verdict

All in `ConductorService`.

A third unproductive outcome joins the two that exist. Rename `_launchFailures` to `_stalls` and give
`Stall` a reason, so one counter covers all three and the founder's message can still say which:

```csharp
private enum Unproductive { NeverStarted, RanAndFailed, NoVerdict }
private sealed record Stall(int Failures, DateTimeOffset? StalledAt, bool Announced, Unproductive Last);
```

The counting, the `ConductorMaxAttempts` ceiling, the half-open `ConductorStallProbeMinutes` cooldown, and
the "clear on success" rule all stay exactly as they are. What changes is what counts as success.

In `RunSessionAsync`, after `await launcher.StartAsync(...)` returns **without throwing**, ask whether the
session actually produced a verdict for that pair:

```csharp
var produced = await ledger.ReadAsync(async (db, _) =>
    !await db.TaskValidations.AnyAsync(v =>
        v.TaskId == assignment.TaskId && v.ValidatorKey == assignment.RoleKey && v.Verdict == Verdict.Pending, ct),
    CancellationToken.None);
```

- **Produced a verdict** → clear the pair from `_stalls`, exactly as today's success path does.
- **Did not** → record `conductor.no_verdict` with
  `new { role = assignment.RoleKey, attempt = stall.Failures }`, and count it as unproductive with
  `Unproductive.NoVerdict`, through the same code path the two exception cases use. Do not throw to get
  there; factor the counting into one private method that all three call.

When the count reaches `ConductorMaxAttempts`, the pair stalls half-open and the founder is told once, with
wording that names which of the three it was:

| Last | Message |
|---|---|
| `NeverStarted` | *(today's wording, unchanged)* `"The conductor could not start a validator for {task} ({role}) {n} times and has stopped trying: {error}"` |
| `RanAndFailed` | *(today's wording, unchanged)* `"The conductor started a validator for {task} ({role}) {n} times and none of them finished: {error}"` |
| `NoVerdict` | `"The conductor started {n} validators for {task} ({role}) and none of them reached a verdict. They ran and exited cleanly, so something is stopping them from validating at all rather than failing. Look at the bus for what they said, then re-spec the task, validate it yourself, or raise Muthur:ConductorMaxAttempts."` |

All three keep the existing second paragraph about the pair retrying by itself within
`ConductorStallProbeMinutes`.

`_lastAction` for the new case reads `"gave up on {role} for {task}: {n} sessions, no verdict"`.

**Ordering note that matters:** with Part 1 in place a blocked verdict moves the task out of `validating`,
so `PlanAsync` will not select the pair again and the no-verdict counter will rarely be reached. That is
intended. Part 2 is for the cases Part 1 cannot see — a session that dies silently, a harness that exits 0
having done nothing, a validator that forgets to record anything. Part 2 without Part 1 is a sticking
plaster: the founder is told the pair is stuck without being told why. Part 1 without Part 2 leaves the
loop open for every other reason a session can end empty.

### Part 3 — the procedure says to record it

`kit/briefs/validator.md` (around line 48) and `kit/core/validate.md` (lines 27–28 and 49) currently tell a
validator to send a message and release the role. They must tell it to record the verdict:

- `kit/core/validate.md`, beside the `pass` and `fail` lines:
  `- muthur validate blocked T-n --as <validator-role> --evidence <file>` — you could not validate it at
  all. The task returns to its owner with your evidence. This is not a verdict on the work, and it is the
  right answer when the product cannot be driven from the session you are in.
- The "if the product cannot be driven unattended" instruction in both files changes from *message the
  owner and release the role* to *record `muthur validate blocked`, then release the role*. Keep the
  message to the owner as a suggestion, not the mechanism — the verdict is the mechanism now.
- `kit/briefs/validator.md` line 64 says failing is cheap and normal. Add that blocking is too, and that a
  validator that cannot run must never pass.

Do not restructure either document. These are edits of a few lines each, in the voice already there.

**And one line of CSS.** `TaskCard.razor` and `TaskDetail.razor` render `class="pill pill-verdict-@v.Verdict"`,
and `wwwroot/app.css` defines `pill-verdict-yes`, `-no` and `-pending` but would have no `-blocked` — so the
new verdict would render as an unstyled grey pill, on a feature whose entire point is that a blocked task is
visible. Add it beside the others, amber, matching the existing `.pill-blocked` token:

```css
.pill-verdict-blocked { color: var(--amber); border-color: rgba(226, 166, 68, .4); background: var(--amber-bg); }
```

## Units of work

### Unit A — `blocked` is a verdict
- **Files:** `src/Muthur.Contracts/Enums.cs`; `src/Muthur.Server/Services/LifecycleService.cs`;
  `src/Muthur.Server/Api/RoleEndpoints.cs`; `src/Muthur.Cli/Commands/RoleCommands.cs`; possibly a new
  `src/Muthur.Server/Services/Evidence.cs` if `FirstLineOfEvidence` moves; `tests/Muthur.Server.Tests/`.
- **Does:** Part 1.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean. Tests: a blocked verdict with evidence returns the
  task to `InProgress` with a fresh claim and records `validation.blocked`; a blocked verdict without
  evidence is refused 422 `evidence_required`; a blocked task is **not** `Validated` even when every other
  required validator said yes; the owner and the founder each receive a message, and the founder's names the
  first line of the evidence; a validator that does not hold the role is refused; the owner of the task is
  refused with `self_validation`; and `pass`/`fail` behave exactly as before.

### Unit B — the no-verdict cap
- **Files:** `src/Muthur.Server/Services/ConductorService.cs`; `tests/Muthur.Server.Tests/ConductorTests.cs`.
- **Does:** Part 2.
- **Depends on:** nothing in code. It can be built beside Unit A.
- **Acceptance:** `dotnet build` and `dotnet test` clean. Tests, using `FakeValidatorSessions`: a session
  that returns without recording a verdict is counted and records `conductor.no_verdict`; after
  `ConductorMaxAttempts` such sessions the pair stalls, records `conductor.stalled`, and the founder gets
  exactly one message naming the count and saying they reached no verdict; a later pass inside the cooldown
  does not restaff the pair; a session that *does* record a verdict clears the count, so a pair that
  recovers is not left one strike from a stall; and the two existing caps (never started, ran and failed)
  still behave and still say what they said before.

### Unit C — the validator procedure
- **Files:** `kit/core/validate.md`, `kit/briefs/validator.md`, `src/Muthur.Server/wwwroot/app.css`.
- **Does:** Part 3.
- **Depends on:** nothing, but its wording must match the CLI Unit A builds.
- **Acceptance:** `dotnet build` and `dotnet test` clean (neither file is compiled; the check is that
  nothing else was touched). Every command named in the changed lines exists in `muthur validate --help`.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub. No browser is required:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t30
$env:MUTHUR_HOME = "$PWD/artifacts/t30-home"; $env:MUTHUR_URL = "http://127.0.0.1:7434"
./artifacts/t30/muthur.exe up
```

With a project requiring one validator role, a task marked implemented, and two agents (an owner and a
validator holding the role):

- `muthur validate blocked T-n --as <role> --note "no browser in this session"` is accepted; the task is
  `in_progress` and owned by its owner again; `muthur task show T-n` carries the blocked verdict and the
  evidence.
- The same command without `--evidence` or `--note` is refused with `evidence_required`, exit 2.
- `muthur msg inbox` as the owner carries the hub's message; the founder's Needs You page and
  `muthur msg inbox --founder` carry theirs, naming the first line of the evidence.
- Turn the conductor on with `ConductorMaxAttempts` low and a `FakeValidatorSessions`-equivalent absent —
  this part is covered by Unit B's tests rather than by hand, because reproducing it live means starting
  real harness sessions, which is the cost this task exists to stop.
- `muthur validate pass` and `muthur validate fail` still do what they did.

## Out of scope / follow-ups

- **Founder request #4 is open**: how the organization declares in advance that a task needs an attended
  validator. Whatever is chosen becomes its own task; this one closes the loop without it.
- The conductor's counters live in memory and reset when the hub restarts. A hub restarted in a loop would
  restart the counting too. Worth a task if the hub ever stops being a thing that runs for hours at a time.
- `muthur validate list` does not distinguish a task that has just arrived from one that a validator blocked
  and an owner re-submitted unchanged. A validator walking into the same wall twice would be worth catching.
