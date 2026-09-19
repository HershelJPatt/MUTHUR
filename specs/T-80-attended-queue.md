# T-80 — An attended task is on the queue of the person it is waiting for

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a task sitting in `validating` with `attended` set appears on Needs You and in the tab badge,
with its reason, its branch, the validator role it is waiting on, and how long it has been waiting. The founder
learns that a task wants them by looking at the badge they already look at, instead of by happening to read the
board and recognising the state.

`attended` is not changed, and nothing new advances a task automatically. The flag takes a task out of the
machine's queue; this puts it into a person's.

## The decisions this task had to make

T-80's body names four things to decide rather than assume. All four are decided here, with what decided them,
so that none of them is re-opened by whoever reads this next.

**Where it surfaces: Needs You, page and badge.** The founder filed this task themselves, in the answer to
request #25: *"a task in a state no mechanism advances, waiting on a human who has no queue telling them it is
waiting … The Needs You page is the obvious home and T-18 already built the ranking for it."* The page is
therefore settled. The **badge** is the part that is not literally in that sentence, and it follows from the
rest of it: a page the founder must think to open is not a queue that tells them anything — it is the board
with extra steps, which is the defect. The number in the top bar is the telling. So an attended task counts
towards `Total`, and towards `OldestWaitingSince` as well, because work is genuinely stopped behind it. (The
existing rule that unread messages count towards `Total` but not towards the age is kept exactly: it is there
because a message blocks nobody, which is the opposite of this case.)

T-31 listed "putting attended tasks into the Needs You count or page" as a non-goal and handed it to T-18;
T-18 did not take it. The founder has now asked for it by name. That is the thread being closed, not crossed.

**What the row carries: the reason, the branch, the wait, the role.** The founder's own list — *"A row that
shows the reason, the branch and how long it has waited is probably the whole feature"* — plus the pending
validator role, which is one word and is the difference between "somebody should look at this" and "I need to
hold `win-validator` to give it a verdict". Nothing else. No evidence, no spec text, no owner: the row links to
the task, and `/tasks/T-n` already shows all of it.

**Expiry and escalation: neither.** The body asks for a week-old wait and an hour-old wait to be tellable apart
"without counting", and that is a rendering question, not a mechanism: every row states its age in words
(`3d`, `2h`), and the list is ordered so the worst is at the top. Nothing ages out, nothing escalates, no
threshold turns a row a different colour at some number of hours. An expiry here would be a decision made by
silence, which is the thing T-18 refused in the same words, and a threshold would be a constant nobody could
defend. The founder reads the age and decides.

**The way back: shown, not clicked.** The panel offers no button — not "clear attended", and above all not
"pass this validation yourself". A verdict is a claim that somebody exercised the work, and a button on a queue
page produces the machinery of one without the substance. The founder's answer to #25 is directly on point:
offered the cheap exit (option C, clear the flag and let a headless validator pass it), they refused it —
*"Releasing both with attended cleared would buy a verdict by deleting the reason the flag exists"* — and did
the browser pass by hand instead. A one-click version of the exit they declined for themselves is not something
this task may add on its own. What the panel does instead is state the two exits in one line of text, so the
queue is not a dead end: take the role and give the verdict, or `muthur task attended T-n --clear`.

If the founder wants either as a button, the Console (T-24) is where founder actions live and it is an additive
change there. That is recorded under *Out of scope / follow-ups* rather than guessed at here.

## Context

- `src/Muthur.Server/Services/FounderAttention.cs` — `FounderAttentionDto` and `ReadAsync`. The single read
  the badge and the page are both built from, "so the two can never disagree". `Total` is computed from
  counts, never from the capped lists.
- `src/Muthur.Server/Services/LifecycleService.cs` — `PendingAsync` (line 70) and `QueueAsync` (line 89) are
  the shape to copy: a `ledger.ReadAsync` over `db.Tasks` and `db.TaskValidations`, projecting through
  `ToDto` / a contract record. This is where the new read goes: it is a question about tasks in `validating`.
- `src/Muthur.Server/Components/Panels/RequestsPanel.razor` — the panel to imitate: `LivePanel`, a `_now`
  taken once per render, `ClockInterval` because ages move without anything being written.
- `src/Muthur.Server/Components/Panels/ValidationQueuePanel.razor` — the smaller example of the same, and the
  `IsRelevant` prefix test to copy.
- `src/Muthur.Server/Components/Pages/NeedsYou.razor` — three panels in a `.stack`.
- `src/Muthur.Contracts/Tasks.cs` — `TaskDto` already carries `Title`, `Branch`, `AttendedReason` and
  `Validations`, and `ValidationDto` carries `Verdict`, `Validator` and `WaitingSince`. **No new contract type
  is needed and none is to be added**; nothing new crosses HTTP, so nothing new is registered in
  `MuthurJsonContext`.
- `src/Muthur.Server/Services/Mapper.cs` — `ToDto`, and `Validations.ForTasksAsync` for the validation rows.
- `tests/Muthur.Server.Tests/TaskAttendedTests.cs` — `ValidatingTaskAsync` is the exact fixture this task's
  tests need: a claimed task, a spec, a branch in a `TestRepo`, marked implemented into `validating`.
- `tests/Muthur.Server.Tests/DashboardNeedsYouTests.cs` — how the page and the badge are asserted from
  prerendered HTML.

Constraints that are not obvious:

- Dashboard components never touch `MuthurDb`; they call the same services the API uses. Live panels inherit
  `LivePanel`.
- Time comes from the injected `TimeProvider`. Panels take one `_now` for a whole render.
- `NeedsYou.razor` does not use `<Virtualize>`, and the new panel must not either: everything it renders has
  to be in the prerendered HTML, which is what makes this task verifiable with no browser.
- Only classes that already exist in `src/Muthur.Server/wwwroot/app.css` are used. **This task adds no CSS**
  — see *Design*.
- Warnings are errors.

## Non-goals

- **Changing what `attended` means, when it is set, or when the conductor skips it.** Settled by founder
  request #4 and #15 and correct. `ConductorService` is not touched by this task at all.
- **Any automatic advance.** No expiry, no escalation, no verdict, no clearing. Said again because a queue
  feature is exactly where one gets added "for convenience".
- **Buttons.** No action of any kind on the new panel. See *The decisions this task had to make*.
- **A new endpoint or CLI command.** `TaskDto.AttendedReason` already crosses HTTP, and
  `muthur task list --state validating` already shows it to any agent that asks. The thing that was missing
  is a human surface, and the dashboard reads the services directly.
- **Surfacing attended tasks in any state but `validating`.** See *Design* for why each other state is
  already covered by something.
- **Touching `TaskCard` or `/tasks/T-n`.** Both already say `needs a human` and the reason (T-31).

## Design

### Which tasks are waiting on a person

`AttendedReason is not null` **and** `State == TaskState.Validating`. That pair, and no other, is a task
nothing will advance. Each remaining state was checked rather than assumed, and each is already somebody's:

| state | what advances an attended task in it |
|---|---|
| `backlog` | the conductor. `PlanOrchestratorsAsync` does **not** filter on `AttendedReason` — only validator staffing does — so an attended backlog task still gets an orchestrator. |
| `in_progress` | its owner. If the owner dies, `SweepExpiredClaimsAsync` returns it to `backlog`, which is the row above (T-60). |
| `validating` | **nothing.** `PlanAsync` skips it, by design, and no other mechanism touches it. This is the task. |
| `blocked` | the founder answering the request that blocked it, which is already the first panel on this page. |
| `validated` | the hub lands it (T-69). |
| `done`, `cancelled` | nothing is waiting. |

Nothing in the implementation encodes that table beyond the one filter. It is here so the filter reads as a
decision rather than as the narrowest thing that happened to pass a test.

### The read

On `LifecycleService`, beside `QueueAsync`:

```csharp
/// <summary>
/// Tasks in 'validating' that a human has said need them, longest-waiting first within a priority. Nothing
/// advances one of these: the conductor skips an attended task on purpose, so unless somebody is told, it
/// waits until somebody happens to read the board.
/// </summary>
public Task<IReadOnlyList<TaskDto>> AttendedAsync(CancellationToken ct = default)
```

Inside `ledger.ReadAsync`:

- Load `db.Tasks.Include(t => t.Project).Include(t => t.Owner)` where
  `t.State == TaskState.Validating && t.AttendedReason != null`.
- Project through `ToDto` with `Validations.ForTasksAsync`, exactly as `PendingAsync` does.
- Order the **DTOs** by, in this order:
  1. `Priority` descending. It is the founder's one lever for "this matters most", and every other place that
     orders work for someone to pick up — `PlanAsync`, `PlanOrchestratorsAsync`, `PendingAsync` — puts it
     first. A second view of the same queue in a different order is how two screens start disagreeing.
  2. `WaitingSince(task)` ascending — the longest wait first among equals.
  3. `Id` ascending, so the order is total and two reads never disagree. Compare the numeric id
     (`int.Parse(dto.Id.AsSpan(2))` is wrong and is not what to write — order the entities' `Id` before
     projecting, or carry the entity alongside the DTO through the sort).

**No cap, and this is deliberate.** T-18 caps the request list at 200 because agents can ask an unbounded
number of questions; this set is bounded by the number of tasks in `validating`, which is bounded by the
work in flight, and nothing an agent does can inflate it. With no cap the count and the list are the same
fact, so the badge and the page cannot disagree — which is the property T-18's cap cost it and had to be
repaired for. **Do not add one for symmetry.**

### How long it has waited

One definition, used by the order, the badge's age and the row:

```csharp
/// <summary>
/// When this task started waiting on a person: when its validation round opened. A task that re-enters
/// 'validating' starts a new round and a new clock, which is right — the wait being reported is the wait
/// for the verdict that is outstanding now, not the age of the task.
/// </summary>
/// <remarks>
/// Falls back to <see cref="TaskDto.UpdatedAt"/> when no verdict is pending. The state machine says a task
/// in 'validating' always has one, so the fallback is unreachable — but it is here rather than a
/// <c>Min()</c> that would throw, because this runs inside a dashboard panel and an empty sequence would
/// take the page down rather than show a row with a wrong age on it.
/// </remarks>
public static DateTimeOffset WaitingSince(TaskDto task) =>
    task.Validations.Where(IsPending).Select(v => v.WaitingSince).DefaultIfEmpty(task.UpdatedAt).Min();
```

`ValidationDto.Verdict` is a wire string — `Validations.ForTasksAsync` writes `v.Verdict.ToWire()`, and
`Verdict.Pending` serialises to `"pending"`. Compare it as `v.Verdict == Verdict.Pending.ToWire()` rather than
against a literal, exact and ordinal, so a rename of the wire word cannot leave this reading a word nothing
produces. `IsPending` is one private static predicate on `LifecycleService`, used by both `WaitingSince` and
the panel's `Pending(task)`; make it `internal` if the panel cannot see it otherwise.

### The badge, and the top bar

`FounderAttentionDto` gains one member, appended last so no positional construction site shifts:

```csharp
/// <param name="Attended">
/// Tasks in 'validating' that a human has said need them. Uncapped by construction, so unlike
/// <see cref="Requests"/> this list is also the count — see LifecycleService.AttendedAsync.
/// </param>
```

- `Total` becomes `RequestsWaiting + OutboundWaiting + Unread + Attended.Count`.
- `OldestWaitingSince` includes the earliest `LifecycleService.WaitingSince` across `Attended`. Unread
  messages stay excluded, for the reason already written on that member; an attended task is included for the
  opposite reason, and the XML doc says so in one sentence.

`FounderAttention`'s constructor takes `LifecycleService lifecycle` and `ReadAsync` calls `AttendedAsync`.
Check the DI registration order in `Program.cs` compiles; both are already registered services.

`NeedsYouBadge` needs no change — it renders `Total` and the age, both of which now include this.

### The panel

New file `src/Muthur.Server/Components/Panels/AttendedPanel.razor`, `@inherits LivePanel`, injecting
`LifecycleService` and `TimeProvider`.

```razor
<section class="panel">
    <div class="panel-head">
        <span class="panel-title">Waiting on a person</span>
        <span class="panel-sub">@(_attended.Count == 1 ? "1 task" : $"{_attended.Count} tasks")</span>
    </div>
    <div class="panel-body">
        @if (_attended.Count == 0)
        {
            <div class="empty">nothing is waiting on a person</div>
        }
        else
        {
            @* The two exits, stated rather than offered: a verdict is a claim that somebody exercised the
               work, and a button on a queue page makes one without the substance. *@
            <div class="section-note">take the role and give the verdict, or lift the flag: muthur task attended T-n --clear</div>
        }
        @foreach (var task in _attended)
        {
            <div class="request">
                <div class="request-head">
                    <span class="tag tag-open">needs a person</span>
                    <a class="event-task" href="/tasks/@task.Id">@task.Id</a>
                    <span>@task.Title</span>
                    <span class="card-age">@Format.Age(LifecycleService.WaitingSince(task), _now)</span>
                </div>
                <div class="request-question">@task.AttendedReason</div>
                <div class="agent-meta">
                    @if (task.Branch is { } branch)
                    {
                        <span class="pill">@branch</span>
                    }
                    @foreach (var validator in Pending(task))
                    {
                        <span class="pill pill-role">@validator</span>
                    }
                </div>
            </div>
        }
    </div>
</section>
```

- `Pending(task)` is the pending validations' `Validator` values, ordered ordinal. A private static method.
- `_now` is taken once at the top of `LoadAsync`, as `RequestsPanel` does.
- `ClockInterval` is 30 seconds: these rows age without anything being written.
- `IsRelevant` is true for event types starting `task.` or `validation.` — the state changes, the flag and the
  verdicts all move this list, and the same prefix test is what `ValidationQueuePanel` uses.

**No new CSS.** `.request` is the "a person must act on this" card with the amber rail, which is exactly this
meaning, and every other class used here already exists. Do not add a `.attended` class that duplicates
`.request`'s rule.

### The page

`NeedsYou.razor`'s middle stack becomes `RequestsPanel`, `AttendedPanel`, `OutboundPanel`, in that order: a
question has an agent idle on it and is the most expensive thing on the page; a task needing a person has
stopped work; outbound is already the tail. An existing test asserts a request renders before an outbound
draft, and inserting between them keeps that true.

## Units of work

### Unit A — the whole task
- **Files:** `src/Muthur.Server/Services/LifecycleService.cs`, `src/Muthur.Server/Services/FounderAttention.cs`,
  `src/Muthur.Server/Components/Panels/AttendedPanel.razor` (new),
  `src/Muthur.Server/Components/Pages/NeedsYou.razor`, `tests/Muthur.Server.Tests/`.
- **Does:** everything in *Design*.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus the tests below.

Tests, in `tests/Muthur.Server.Tests/DashboardAttendedTests.cs` (new file; `TaskAttendedTests.ValidatingTaskAsync`
is the fixture to copy, including its `TestRepo`):

1. **The queue is what nothing advances.** An attended task in `validating` is returned by `AttendedAsync`;
   an identical task in `validating` that is not attended is not. Assert both in one test, so the flag is
   what differs.
2. **Every other state stays out.** One attended task taken to `backlog` (claimed then released, or added and
   flagged before claiming) and one attended task in `in_progress`: neither is returned. Do not chase
   `validated`/`done` through a real land — the filter is one clause and these two cover it either side.
3. **The order.** Three attended tasks: a low-priority one that has waited longest, and two of equal higher
   priority implemented a fake-clock hour apart. Assert priority wins, and that the older of the two equals
   comes first. Step `_hub.Clock`, never sleep.
4. **The wait is the round's, not the task's.** A task marked implemented, then blocked back to
   `in_progress` and implemented again an hour later, reports the second round's `WaitingSince`.
5. **The badge counts it and ages with it.** `FounderAttention.ReadAsync().Total` includes an attended task;
   `OldestWaitingSince` is that task's wait when it is the oldest thing waiting; clearing the flag with
   `POST /api/v1/tasks/T-n/attended` carrying a null reason takes it out of both.
6. **The page says it.** `GET /needs-you` contains the task id, the title, the reason **in full**, the branch,
   the pending validator role and `needs a person`; and `1 task` in the panel head. A hub with none contains
   `nothing is waiting on a person`.
7. **Nothing on that panel acts.** `GET /needs-you` for an attended task contains the words of the exit line,
   and the rendered panel contains no `<button`. Assert it against the panel's own HTML — render
   `AttendedPanel` through `HtmlRenderer` as `TaskAttendedTests.RenderCardAsync` renders `TaskCard` — because
   the whole page has buttons from the other two panels.
8. **Mutation check.** With `&& t.AttendedReason != null` removed from the read, test 1 fails. With the
   `Priority` key removed from the order, test 3 fails. Report both in *Proof*; do not commit either.

## Verification

```
dotnet build
dotnet test
```

Both clean: 0 warnings, 0 errors, every test green.

End to end, against an installed build and a scratch home, never the live hub. No browser is required and none
should be used: `NeedsYou.razor` has no `<Virtualize>`, so a `GET` of the page carries everything it shows —
which is the same reason `DashboardNeedsYouTests` can assert the existing panels over HTTP.

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t80
$env:MUTHUR_HOME = "$PWD/artifacts/t80-home"; $env:MUTHUR_URL = "http://127.0.0.1:7461"
./artifacts/t80/muthur.exe up
```

With a project requiring a validator, a task implemented and sitting in `validating`:

```
./artifacts/t80/muthur.exe task attended T-n --reason "the panel is only visible in a browser"
(Invoke-WebRequest "$env:MUTHUR_URL/needs-you" -UseBasicParsing).Content |
    Select-String -Pattern 'Waiting on a person', 'needs a person', 'only visible in a browser', 'task/T-n-'
(Invoke-WebRequest "$env:MUTHUR_URL/" -UseBasicParsing).Content | Select-String -Pattern 'class="badge"'
```

Passing is: all four patterns present on Needs You, the badge on `/` one higher than it was before the flag
was set, and after `./artifacts/t80/muthur.exe task attended T-n --clear` both the row and that extra count
are gone again.

This task adds nothing that can only be seen by eye, so it is not marked attended — which is the point of it
being worth doing at all.

## Out of scope / follow-ups

- **A button.** Whether Needs You (or the Console) should offer "clear attended" or "record my verdict" as a
  click is a decision for the founder, set out in *The decisions this task had to make* and deliberately not
  taken here. If they want it, the Console is where founder actions live and it is additive.
- **Anyone but the founder.** An attended task waits on any human; the only human surface this organization
  has is the founder's dashboard. If a second person ever holds a browser-capable validator role, "who is this
  waiting on" becomes a real question and this panel is where it would be answered.
- **Reaching the founder off the screen.** This makes the queue visible to somebody looking at the dashboard.
  A push — mail, chat — for a task that has waited a long time is the next thing after this, and it is exactly
  the escalation this task declined to invent on its own.
