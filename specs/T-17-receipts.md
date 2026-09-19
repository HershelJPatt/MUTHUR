# T-17 — Receipts: what the organization spent itself on, and where the time went

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, `muthur receipts` and a **Receipts** tab answer, for a window the founder chooses:

- How many sessions this organization started, on which harnesses and accounts, at which tier.
- Which tasks consumed them — so the task that took eleven sessions and failed validation three times is a
  row you can see rather than a thing you remember.
- How long tasks spent in `validating` against how long they spent being worked. That is the number that
  proves or disproves the claim in `docs/PLAN.md` §0 that validation, not implementation, is the bottleneck.
- Which accounts hit their ceiling, and how often.

The reason it matters now rather than later: the conductor spends a subscription while the founder is asleep.
A record they can read in the morning is the difference between trusting it and turning it off.

## What the ledger actually knows

The task says "everything needed is already in the ledger. This is a read, not new instrumentation." That was
checked against the code rather than taken on trust, and it holds — **on one condition**, which is the whole
design: receipts must count what each event actually means, and must not add them together into a single
"sessions" number that no event supports.

There are three session-bearing events. They are exact, and they are not interchangeable.

| Event | What one of them means | Carries |
|---|---|---|
| `agent.registered`, `agent.reregistered` | A session took an identity. | agent name, `harness`, `model`, `tier`, `account` |
| `conductor.staffing` | The conductor started a validator session. | `TaskId`, `role` |
| `worker.finished`, `worker.failed` | A headless `muthur worker run` finished. | `TaskId`, `tier`, `worker` (`harness/model`), `account`, `unit`, `seconds`, `costUsd` |

Why registrations are the honest source for "sessions by harness and account":
`AgentLauncher.RunAsync` calls `identityFor(candidate)` **after** the harness adapter is found and the
executable is resolved on PATH, immediately before `processes.RunAsync`. So a registration event means a
process really started, on that account. `ValidatorSessionLauncher.IdentityFor` registers per candidate and
says so in its own comment: *"Registering again re-issues the token... the previous session is over."* A
fall-through to a second vendor registers twice, which is correct — two accounts were spent.

Its one limit, which the output must state rather than paper over: a founder-started terminal session
registers the first time and then reuses `$MUTHUR_TOKEN`, so hand-started work is counted at its first
session only. Everything the hub launches is counted every time.

**What is genuinely not there.** `conductor.staffing` names the task but not the harness that ran it, and
nothing records how long a validator session took. So "which account validated T-9, and for how long" cannot
be answered, and receipts must not pretend otherwise. That is a follow-up, named below; it is one event at the
point where `ValidatorSessionLauncher` already knows `last.Candidate`, and it is not this task.

### Time in state

No event names the state a task entered — `task.State` is assigned at thirteen sites across four services and
the event type is the only record. The mapping is therefore explicit, and it is exhaustive as of this commit:

| Event | State entered |
|---|---|
| `task.added` | `Backlog` |
| `task.claimed` | `InProgress` |
| `task.released` | `Backlog` |
| `task.claim_expired` | `Backlog` |
| `task.reopened` | `Backlog` |
| `task.blocked` | `Blocked` |
| `task.unblocked` | `InProgress` |
| `task.implemented` | `Validating` |
| `task.validation_failed` | `InProgress` |
| `task.validation_blocked` | `InProgress` |
| `task.validated` | `Validated` |
| `task.land_failed` | `InProgress` |
| `task.landed` | `Done` |
| `task.pr_opened` | `Done` |
| `task.cancelled` | `Cancelled` |

Every other event type — `task.spec_set`, `task.priority_changed`, `task.attended`, `task.attended_cleared`,
`task.land_refused`, and everything outside the `task.` family — leaves the state alone.

Two cases that look like mistakes and are not:

- A project with no required validators gets `task.implemented` and `task.validated` in the **same mutation**,
  so they share an `At` and differ only by `Seq`. Replaying by `Seq` yields a zero-length `Validating`
  interval, which is the truth: it never waited.
- `task.land_refused` records a refusal that changed nothing, so it is absent from the table deliberately.

A table like this drifts. The guard is not a list that has to be remembered: it is a test that drives a full
lifecycle through the real API and asserts, after **every** transition, that replaying the ledger lands on the
same state the task row is actually in. A new state-changing event that nobody added here fails that test the
moment any test path exercises it.

## Non-goals

- **Totalling money.** Settled by founder request #13: `costUsd` appears on the worker-run row and is
  never summed, anywhere, by anything.
- Any new ledger event, any change to what any service records, any migration. This is a read.
- Attributing a conductor validator session to a harness or an account, or giving it a duration. Named above
  and in the follow-ups; it needs instrumentation this task does not add.
- Prices, rates, or anything MUTHUR would have to be told by a human. MUTHUR launches the vendors' own CLIs on
  the founder's own subscriptions and never sees a bill.
- Retention, rollups, or a scheduled digest. Receipts reads the ledger on demand; the ledger is append-only
  and small.
- Any change to `muthur log`, the Stream page, or `EventDto`.

## Context

- `src/Muthur.Server/Services/Ledger.cs` — `Mutation.Record` serializes payloads with
  `JsonSerializerDefaults.Web`, so payload properties are **camelCase** on the wire: `harness`, `model`,
  `tier`, `account`, `seconds`, `costUsd`, `role`, `worker`.
- `src/Muthur.Server/Services/AgentService.cs` line ~43 — the registration event and its payload.
- `src/Muthur.Server/Services/HarnessService.cs` `RecordWorkerRunAsync` — the worker event and its payload.
- `src/Muthur.Server/Services/ConductorService.cs` `RunPassAsync` — `conductor.staffing`.
- `src/Muthur.Launch/AgentLauncher.cs` lines 43-62 — why a registration means a process started.
- `src/Muthur.Core/TaskStateMachine.cs` — the shape and placement to copy for the new pure type.
- `src/Muthur.Server/Services/DoctorService.cs` + `src/Muthur.Contracts/Doctor.cs` +
  `src/Muthur.Server/Api/SystemEndpoints.cs` line 29 — the pattern for a read-only service, its DTO and its
  endpoint, end to end. `GET /api/v1/doctor` is unauthenticated; so is this.
- `src/Muthur.Cli/Commands/SystemCommands.cs` — how a read command is wired: one `HubClient.For(parse).GetAsync`
  and `Output.Emit`. The CLI prints the hub's JSON; it does not format.
- `src/Muthur.Server/Components/Panels/DoctorPanel.razor` and `Components/Shared/LivePanel.cs` — the panel
  pattern. `src/Muthur.Server/Components/Layout/MainLayout.razor` — the tab row.
- `src/Muthur.Server/wwwroot/app.css` — `.panel`, `.panel-head`, `.panel-body`, `.panel-sub`, `.section-label`,
  `.role-row`, `.stat`, `.stats`, `.kv`, `.pill`, `.empty`, `.tag` already exist.
- `tests/Muthur.Server.Tests/HubFactory.cs` — isolated temp data dir, fake clock, fake lander, fake harness.

Constraints that are not obvious:

- Every type that crosses HTTP is registered in `MuthurJsonContext`, or the AOT CLI cannot read it.
- Reads go through `Ledger.ReadAsync`. Receipts performs no mutation and records no event.
- Time comes from the injected `TimeProvider`.
- Dashboard components never touch `MuthurDb`; they call the same service the API calls.
- Styling uses only classes from `wwwroot/app.css`; new classes go in that file, never inline.
- Warnings are errors. **Tests never sleep to synchronize.**

## Design

### The window

`GET /api/v1/receipts?hours=24`. `hours` is 1..720 (thirty days); anything outside is clamped rather than
refused — a founder typing `--hours 100000` wants "everything", not an error. `Since` is `now - hours` and is
returned so the reader never has to compute it.

### The pure part: `src/Muthur.Core/TaskStateTimeline.cs`

```csharp
/// <summary>
/// Where a task's time went, reconstructed from the ledger. No event names the state a task entered, so the
/// event type is the record: this is that mapping, in one place, and the replay that turns it into intervals.
/// </summary>
public static class TaskStateTimeline
{
    /// <summary>One continuous stay in one state. <paramref name="Until"/> is null while it is still there.</summary>
    public readonly record struct Interval(TaskState State, DateTimeOffset From, DateTimeOffset? Until);

    /// <summary>The state an event type moves a task into, or null for the events that move nothing.</summary>
    public static TaskState? Enters(string eventType);

    /// <summary>
    /// Events for one task, in <c>Seq</c> order, oldest first. Consecutive events entering the same state
    /// extend the stay rather than starting a new one.
    /// </summary>
    public static IReadOnlyList<Interval> Replay(IEnumerable<(string Type, DateTimeOffset At)> events);

    /// <summary>How long the intervals overlap [<paramref name="from"/>, <paramref name="to"/>], per state.</summary>
    public static IReadOnlyDictionary<TaskState, TimeSpan> Within(
        IReadOnlyList<Interval> intervals, DateTimeOffset from, DateTimeOffset to);
}
```

Rules `Replay` follows, each of which has a test:

- Events that enter no state are skipped entirely; they never close an interval.
- An event entering the state the task is already in extends the current interval. (`task.validation_failed`
  on a task already back `InProgress` cannot happen today, but two `task.released` in a row can.)
- The last interval has `Until = null`. `Within` closes it at `to`.
- An empty sequence gives no intervals. A sequence whose first state-entering event is not `task.added` still
  replays from that event — the ledger may have been read from a window that starts mid-life.
- `Within` clips both ends: an interval that starts before `from` contributes only the part after it.

`Enters` is a `switch` over the table above, returning `null` in the default arm.

### The read: `src/Muthur.Server/Services/ReceiptsService.cs`

One `Ledger.ReadAsync`. It loads, in this order:

1. `Events` in the window whose `Type` is one of the session-bearing five, plus `account.limited`, plus
   `validation.failed`.
2. The distinct `TaskId`s named by any of those, and those tasks' rows.
3. **All** events for those tasks — not only the ones in the window — so the replay knows what state each was
   in when the window opened. A task's whole history is a handful of rows.
4. `AccountLimits` still in force at `now`.

Payloads are read with `JsonDocument`; a payload that does not parse, or that lacks the property, contributes
`null` for that dimension rather than throwing. A receipts view must never be the thing that fails.

### `src/Muthur.Contracts/Receipts.cs`

```csharp
/// <param name="Count">Sessions that took this identity in the window.</param>
public sealed record SessionGroupDto(string? Tier, string Harness, string Model, string? Account, int Count);

/// <param name="ConductorSessions">Validator sessions the conductor started for this task.</param>
/// <param name="WorkerRuns">Headless worker runs reported against it, and how many failed.</param>
/// <param name="ValidationFailures">Verdicts of "no" in the window: the resubmission count.</param>
public sealed record TaskReceiptDto(
    string Task, string Title, TaskState State,
    int ConductorSessions, int WorkerRuns, int WorkerRunsFailed, int ValidationFailures,
    double ValidatingSeconds, double InProgressSeconds, bool Landed);

/// <param name="Longest">The single longest stay any one task had in this state, and which task.</param>
public sealed record StateTimeDto(TaskState State, double Seconds, int Tasks, double Longest, string? LongestTask);

/// <param name="Times">How many times this account was reported out of quota in the window.</param>
public sealed record AccountReceiptDto(string Account, int Times, DateTimeOffset? LimitedUntil);

public sealed record ReceiptsDto(
    DateTimeOffset Since,
    DateTimeOffset At,
    int Sessions,
    int ConductorSessions,
    int WorkerRuns,
    double WorkerSeconds,
    IReadOnlyList<SessionGroupDto> ByAccount,
    IReadOnlyList<TaskReceiptDto> Tasks,
    IReadOnlyList<StateTimeDto> StateTime,
    IReadOnlyList<AccountReceiptDto> Accounts,
    IReadOnlyList<WorkerRunDto> Runs);
```

- `Sessions` is the count of `agent.registered` + `agent.reregistered` in the window: identities taken.
  `ByAccount` is those same events grouped by `(tier, harness, model, account)`, ordered by `Count`
  descending then `Harness`. A registration whose payload names no account groups under `Account = null`.
- `ConductorSessions` counts `conductor.staffing`. It is reported beside `Sessions`, never folded into it:
  a staffing and the registration it causes are the same session counted from two ends, and adding them
  would double it.
- `WorkerRuns` counts `worker.finished` + `worker.failed`; `WorkerSeconds` sums their `seconds`.
- `Tasks` is ordered by `ConductorSessions + WorkerRuns` descending, then `ValidationFailures` descending,
  then task id. Tasks with no sessions and no failures in the window are omitted — receipts is a record of
  what was spent, not a second board.
- `StateTime` covers `InProgress`, `Validating`, `Blocked`, `Backlog`, in that order, and omits a state no
  task was in. `Validated` is omitted: it is the gap between a pass and a land, and T-26 already surfaces it.
- Register every one of these, and `IReadOnlyList<>` of each that crosses on its own, in `MuthurJsonContext`.

`Routes.Receipts = Api + "/receipts"`. `SystemEndpoints` maps it beside `Doctor`, unauthenticated: it names
accounts and task titles, both of which `/harness/tiers` and `/tasks` already serve without a token.

### The CLI

```
muthur receipts [--hours 24]
```

in `SystemCommands`, beside `doctor`: one `HubClient.For(parse).GetAsync(Routes.Receipts + query)` and
`Output.Emit`. Exit 0 whatever the numbers say — receipts reports, it does not judge. No `--founder`: the
endpoint needs no token, and adding one would imply an authority that is not there.

### The page

New `src/Muthur.Server/Components/Pages/Receipts.razor` at `/receipts`, and a
`Components/Panels/ReceiptsPanel.razor` inheriting `LivePanel`, laid out like `Operations.razor`.

The tab goes in `MainLayout.razor` after **Operations** and before **Projects**. **No badge.** The reference
hub shows `RECEIPTS 16`, but a badge in this dashboard means something needs the founder — that is what
`NeedsYouBadge` is for. Receipts never needs anyone; a count there would cry wolf every time a session ran.

The panel:

- A window selector of three buttons — 24h, 7d, 30d — defaulting to 24h, `.btn` and `.btn-row`.
- `.stats`/`.stat` for the four headline numbers: sessions, conductor sessions, worker runs, worker time.
- `.section-label` "where the time went", then one `.role-row` per `StateTimeDto`: state, total, `Tasks`
  tasks, and the longest stay with its task id.
- `.section-label` "by account", then one `.role-row` per `SessionGroupDto`.
- `.section-label` "tasks", then one row per `TaskReceiptDto` — task id, title, a `.pill` for its state, and
  the counts. The row that took eleven sessions must be visibly the first one.
- `.section-label` "accounts", then one `.role-row` per `AccountReceiptDto`, marked when still limited.
- `.empty` "nothing spent in this window" when every list is empty.

`IsRelevant` returns true for `agent.`, `conductor.`, `worker.`, `task.`, `validation.` and `account.`
prefixes. `ClockInterval` is 60s: the window's edge moves with time alone.

Any class this needs that `app.css` does not have is **added to `app.css`**, with a token, in the style of the
rows already there. Nothing inline.

### Money — settled by founder request #13

**Show `costUsd` on the worker-run row where it is exact, and nowhere else. Never total it.**

The founder's reasoning, because it decides more than one field: a total would be incomplete *in a biased
direction nobody can correct for*. The sessions with no cost are not a random sample — they are every claude
session and every conductor-started validator, and validators are the heaviest spend now that the
organization runs itself. "A figure that omits the largest category is not a partial answer to 'what did this
cost', it is a confident answer to a different question." A caveat beside the number does not save it: the
number is read and the caveat is skimmed, and the error moves with the harness rotation.

Two consequences that are requirements, not decoration:

- **The row names its harness.** A reader seeing a blank cost must be able to see that the blank is claude
  rather than a bug.
- **Where a column would naturally total, say what is not counted.** The panel prints
  "conductor validator sessions report no cost" rather than leaving an empty cell. That absence is a fact
  about the data, and making the organization's spending legible is what T-17 is for.

This adds one list to the read, in Unit B, and one section to the panel, in Unit D. Money is a field on the
rows that have it; it is not what the page is about.

#### `WorkerRunDto`

```csharp
/// <param name="Worker">"harness/model", exactly as the event records it — the row must name its harness.</param>
/// <param name="CostUsd">What the harness reported, or null when it reports none. Never summed.</param>
public sealed record WorkerRunDto(
    string? Task, string Tier, string Worker, string? Account, string? Unit,
    int Seconds, decimal? CostUsd, bool Success, DateTimeOffset At);
```

`ReceiptsDto` gains one property, `Runs`: one entry per `worker.finished`/`worker.failed` event in the
window, newest first. The existing `WorkerRuns` keeps its name and stays the count of them. There is **no**
cost field anywhere else in `ReceiptsDto`, and nothing sums `CostUsd`.

## Units of work

### Unit A — the timeline
- **Files:** new `src/Muthur.Core/TaskStateTimeline.cs`; new `tests/Muthur.Core.Tests/TaskStateTimelineTests.cs`.
- **Does:** `Enters`, `Replay`, `Within`. Pure; no EF, no HTTP, no clock.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. `Enters` returns the state in the table for each of the fifteen rows, and `null` for `task.spec_set`,
     `task.land_refused`, `task.priority_changed`, `conductor.staffing` and `""`.
  2. A full lifecycle replays to the right intervals: added → claimed → blocked → unblocked → implemented →
     validation_failed → implemented → validated → landed. Nine events, and the `Validating` stays are the
     two the founder would count.
  3. Events entering no state are skipped without closing the interval they fall inside: inserting
     `task.spec_set` and `task.priority_changed` into the sequence above changes no interval.
  4. Repeating a state extends rather than splits: two `task.released` in a row give one `Backlog` interval.
  5. The last interval is open, and `Within` closes it at `to`.
  6. `Within` clips: an interval spanning the window's start contributes only its part inside.
  7. Zero-length is preserved: `task.implemented` and `task.validated` at the same instant give a
     `Validating` interval of `TimeSpan.Zero`, not a missing one.

### Unit B — the read and the endpoint
- **Files:** new `src/Muthur.Contracts/Receipts.cs`; `src/Muthur.Contracts/Routes.cs`;
  `src/Muthur.Contracts/MuthurJsonContext.cs`; new `src/Muthur.Server/Services/ReceiptsService.cs`;
  `src/Muthur.Server/Api/SystemEndpoints.cs`; `src/Muthur.Server/Infrastructure/Startup.cs` (registration);
  new `tests/Muthur.Server.Tests/ReceiptsTests.cs`.
- **Does:** everything under "The read", the contracts, and the endpoint.
- **Depends on:** Unit A.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. **The drift guard.** Drive a task through the real API: add, claim, ask (blocked), answer (unblocked),
     spec, implemented, fail it, implemented again, pass it, land it. After **every** step, replay that task's
     ledger with `TaskStateTimeline` and assert the final interval's state equals the task row's `State`.
     Mutation-check it: remove one row from `Enters` and confirm this test fails naming that state.
  2. **Sessions by account.** Register two agents on different harnesses and accounts, re-register one of
     them, and assert `Sessions` is 3, `ByAccount` has two groups, and the re-registered one has `Count` 2.
  3. **Conductor sessions are not folded in.** With `HubFactory`'s fake session launcher, run a conductor pass
     that staffs one task; assert `ConductorSessions` is 1 and that it is not added to `Sessions`.
  4. **The interesting row.** A task with several conductor sessions and two failed verdicts sorts above a
     task with one of each, and its `ValidationFailures` is 2.
  5. **The bottleneck number.** A task marked implemented, left validating while the fake clock advances an
     hour, then failed, then worked for ten minutes: `StateTime` reports `Validating` 3600s and `InProgress`
     600s for that stretch. Step `HubFactory.Clock`; never sleep.
  6. **The window bites.** The same fixture read with `hours=1` after advancing the clock two hours reports no
     tasks and no sessions, and `Since` is `now - 1h`.
  7. **`hours` is clamped**, not refused: `0`, `-5` and `100000` all answer 200, with `Since` at the 1-hour
     and 720-hour bounds.
  8. **A malformed payload does not break it.** An event whose `PayloadJson` is `"not json"` — write one
     directly through `Ledger.MutateAsync` — is counted, contributes `null` dimensions, and the endpoint
     still answers 200.
  9. `GET /api/v1/receipts` answers without a token, and round-trips through `MuthurJsonContext` — assert by
     deserializing the response body with the generated context, not with reflection.
  10. **Money is on the row and nowhere else.** Report two worker runs through `POST /api/v1/workers/runs`,
     one with a `costUsd` and one without: both appear in `Runs`, newest first, each naming its `worker` as
     `harness/model`; the one that reported nothing has `CostUsd` null; and no other property of
     `ReceiptsDto` carries a cost. Assert the last part by reflecting over `ReceiptsDto`'s own properties in
     the test — a future hand adding a total should fail here rather than in review.

### Unit C — the CLI
- **Files:** `src/Muthur.Cli/Commands/SystemCommands.cs`; `tests/Muthur.Server.Tests/SystemTests.cs`.
- **Does:** `muthur receipts [--hours n]`.
- **Depends on:** Unit B.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus the command appears in `muthur --help`, exits 0
  against a hub with nothing in it, and passes `--hours` through to the query string.

### Unit D — the page
- **Files:** new `src/Muthur.Server/Components/Pages/Receipts.razor`; new
  `src/Muthur.Server/Components/Panels/ReceiptsPanel.razor`;
  `src/Muthur.Server/Components/Layout/MainLayout.razor`; `src/Muthur.Server/wwwroot/app.css`;
  new `tests/Muthur.Server.Tests/DashboardReceiptsTests.cs`.
- **Does:** the tab, the page, the panel.
- **Depends on:** Unit B. Founder request #13 is answered; the decision is in "Money" above.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. `GET /receipts` renders, and the tab is present on every page.
  2. A hub with nothing in it renders the empty state, not an exception.
  3. A hub with the Unit B fixture renders the task rows in spend order, the state-time rows, and the account
     groups; assert on the rendered markup the way `DashboardOperationsTests` does.
  4. Switching the window to 7d re-reads and changes the numbers.
  5. Every class the panel uses exists in `app.css` — assert by reading the file, as no test can catch a
     missing class at runtime.
  6. **Money reads as the founder asked.** The worker-run section renders one row per run, each naming its
     harness; a run that reported no cost renders an em dash rather than a zero; the section carries the line
     "conductor validator sessions report no cost"; and no total appears anywhere on the page. Assert the
     last by searching the rendered markup for a second currency-formatted figure.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t17
$env:MUTHUR_HOME = "$PWD/artifacts/t17-home"; $env:MUTHUR_URL = "http://127.0.0.1:7455"
./artifacts/t17/muthur.exe up
```

- `muthur receipts --pretty` on an empty hub: zeros, an empty `tasks`, and a `since` one hour short of `at`
  when `--hours 1`.
- Register two agents by hand on different harnesses, take a task through claim → implemented → fail →
  implemented → pass → land, then `muthur receipts --pretty`: the task appears with `validationFailures` 1,
  and `stateTime` shows time in both `validating` and `in_progress`.
- Open `http://127.0.0.1:7455/receipts`: the Receipts tab is there, the task is in the list, and the window
  buttons change the numbers.
- **The question the task exists to answer**, said out loud in the report: over the window, is total
  `validating` time larger than total `in_progress` time on this hub? Either answer is a result; the point is
  that it is now a number rather than an opinion.

## Out of scope / follow-ups

- **A conductor session that knows its own harness and its own length.** `ValidatorSessionLauncher` holds
  `last.Candidate` and `AgentLauncher` already times every attempt; one event at the end of `StartAsync` would
  let receipts answer "which account validated T-9, and for how long". That is instrumentation, so it is its
  own task — and the most valuable single follow-up here.
- Sessions a founder started in a terminal are counted at their first registration only. Making them exact
  needs a session concept the hub does not have.
- A daily digest through `muthur out`, so the morning record arrives rather than being fetched.
- `muthur doctor` could warn when one account carries every session — a subscription about to run out is
  visible in these numbers before it bites.
