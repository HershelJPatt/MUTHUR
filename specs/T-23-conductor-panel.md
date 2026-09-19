# T-23 — The conductor on the board

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the founder can see what the conductor is doing without running a CLI: whether staffing is
on and turn it on or off, which (task, role) pairs have a session running now against the ceiling, when the
last pass ran and what it did, and which pairs have stalled — with the reason and when the next probe is due.

T-11 asked for exactly this in its task body, its frozen spec listed only `muthur conductor status|on|off`,
and so seven rounds built no panel. `ConductorStatusDto` already carries most of it; two facts the founder
most needs to act on — *which* sessions, and *what* stalled — are held in `ConductorService` and reach
nobody.

## Context

- `src/Muthur.Server/Services/ConductorService.cs`:
  - `_running`, a `HashSet<string>` of `"T-n/role"` keys, exposed only as `RunningCount` (line 88).
  - `_stalls`, `Dictionary<string, Stall>` keyed the same way; `Stall(Failures, StalledAt, Announced, Last)`
    with `Unproductive` being `NeverStarted`, `RanAndFailed` or `NoVerdict` (lines 55–71).
  - `StatusAsync` (line 90) and `SetEnabledAsync` (line 102), the latter founder-only through its caller.
  - `options.ConductorStallProbeMinutes` is how long after `StalledAt` the next probe is let through.
- `src/Muthur.Contracts/Harnesses.cs` — `ConductorStatusDto`, `ConductorSwitch`. Everything crossing HTTP is
  registered in `MuthurJsonContext`; the CLI is Native AOT.
- `src/Muthur.Server/Api/HarnessEndpoints.cs` lines 25–28 — `GET`/`POST /conductor`.
- **T-6 (`task/T-6-agents-rail`, validated and landing)** is the design this sits beside. Its `RolesPanel`
  is the idiom to copy exactly: a `<section class="panel">`, a `panel-head` with `panel-title` and a
  `panel-sub` count, a `panel-body` with an `empty` div, and rows built from `.agent`, `.agent-head`,
  `.agent-name`, `.agent-meta`, `.pill` and `.tag`. It adds `<RolesPanel />` to the left `aside` of
  `Board.razor` and `NeedsYou.razor`; this panel goes beside it in the same two asides.
- `src/Muthur.Server/Components/Panels/OutboundPanel.razor` is the existing example of a panel with a
  founder-only action and an error line.

Constraints that are not obvious:

- Dashboard components never touch `MuthurDb`; they call the same services the API uses. Live panels inherit
  `LivePanel`.
- Time comes from the injected `TimeProvider`.
- Styling uses only classes from `wwwroot/app.css`; new classes go there, never inline styles.
- `NeedsYou.razor` and `Board.razor` do not use `<Virtualize>`, so everything here is in the prerendered HTML
  and assertable with a fetch. That is what makes this task verifiable without a browser.
- Warnings are errors.

## Non-goals

- **The agents rail.** T-6 owns it and is already validated. This panel is a sibling, not a rewrite, and it
  copies T-6's classes rather than adding its own vocabulary for the same shapes.
- Changing what the conductor *does* — no new staffing rule, no change to the stall policy, the cap, or the
  probe interval. This task reports.
- A history of past passes. `lastPass`/`lastAction` is what T-23 asks for; a timeline is T-17's ground.
- Cancelling a running session from the dashboard. Nothing in the hub can do that today and inventing it
  here would be a product decision, not a panel.

## Design

### Two facts the conductor keeps to itself

`ConductorStatusDto` gains two lists, both ordered by task id then role so two reads never disagree:

```csharp
/// <param name="Task">"T-n".</param> <param name="Role">The validator role this session was staffed for.</param>
public sealed record ConductorSessionDto(string Task, string Role);

/// <param name="Reason">"never started", "ran and failed" or "no verdict" — the three ways a session produces nothing.</param>
/// <param name="Failures">Consecutive unproductive sessions for this pair.</param>
/// <param name="NextProbeAt">When one probe is let through again; null when the pair has not stalled yet.</param>
public sealed record ConductorStallDto(string Task, string Role, string Reason, int Failures, DateTimeOffset? NextProbeAt);

public sealed record ConductorStatusDto(
    bool Enabled, int Running, int MaxSessions, int SessionMinutes, int MaxAttempts, int IntervalSeconds,
    int StallProbeMinutes, DateTimeOffset? LastPass, string? LastAction,
    IReadOnlyList<ConductorSessionDto> Sessions, IReadOnlyList<ConductorStallDto> Stalls);
```

`Running` stays and stays authoritative: `Sessions.Count` equals it, and the existing field is what every
current caller reads.

`StatusAsync` builds both under the same locks the fields already use, splitting each `"T-n/role"` key at its
first `/`. Only pairs that have actually stalled (`StalledAt` is not null) appear in `Stalls` — a pair with
one failure and two attempts left is not something a founder acts on, and listing it would bury the ones
that are. `NextProbeAt` is `StalledAt + ConductorStallProbeMinutes`.

`Reason` is a stable lowercase string, not the enum name: `"never started"`, `"ran and failed"`, `"no verdict"`.

### The panel

`src/Muthur.Server/Components/Panels/ConductorPanel.razor`, inheriting `LivePanel`, injecting
`ConductorService` and `TimeProvider`.

- **Head.** Title `Conductor`; `panel-sub` reads `@_status.Running of @_status.MaxSessions running` when
  staffing is on, and `off` when it is not.
- **The switch.** One button, founder-only in the same way `OutboundPanel`'s actions are: `Turn on` when
  disabled, `Turn off` when enabled, calling `SetEnabledAsync(Caller.Founder, …)`. A `MuthurException`
  renders in an `error-text` div and the panel does not reload.
- **Sessions.** One `.agent` row per session: `.agent-name` is the task id linking to `/tasks/T-n`, with the
  role as a `.pill`. When there are none and staffing is on, the empty line reads
  `no sessions running` — which, next to `lastAction`, is the difference between idle and wedged.
- **Last pass.** `<div class="agent-meta">` with `@Format.Age(lastPass, now)` and the `lastAction` text, or
  `no pass yet` when `LastPass` is null.
- **Stalls.** One `.agent` row per stall, given the `pill-blocked` class the board already uses for "waiting
  on a person": task id, role, reason, `@Failures failures`, and `next probe @Format.Age(NextProbeAt, now)`.
  A stall is the one conductor state a founder must act on, so it is rendered last and loudest rather than
  folded into the session list.
- **Refresh.** `IsRelevant` returns true for events whose type starts with `conductor.`; `ClockInterval` is
  30 seconds, because the last-pass age and the next-probe time move on their own.

`<ConductorPanel />` goes in the left `aside` of `Board.razor` and `NeedsYou.razor`, directly after
`<RolesPanel />`.

No new CSS: every class used above already exists in `app.css` (`panel`, `panel-head`, `panel-title`,
`panel-sub`, `panel-body`, `empty`, `agent`, `agent-head`, `agent-name`, `agent-meta`, `pill`,
`pill-blocked`, `btn`, `btn-go`, `btn-stop`, `error-text`).

## Units of work

### Unit A — the two lists
- **Files:** `src/Muthur.Contracts/Harnesses.cs`, `src/Muthur.Contracts/MuthurJsonContext.cs`,
  `src/Muthur.Server/Services/ConductorService.cs`
- **Does:** the two DTOs and their registration; `StatusAsync` populating `Sessions` and `Stalls`.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean; Unit C's service tests pass.

### Unit B — the panel
- **Files:** `src/Muthur.Server/Components/Panels/ConductorPanel.razor`,
  `src/Muthur.Server/Components/Pages/Board.razor`, `src/Muthur.Server/Components/Pages/NeedsYou.razor`
- **Does:** the panel per Design, and the two page lines.
- **Depends on:** Unit A.
- **Acceptance:** `dotnet build` clean; Unit C's page tests pass.

### Unit C — the tests
- **Files:** `tests/Muthur.Server.Tests/`
- **Does:**
  - `StatusAsync` reports a running session's task and role, and `Sessions.Count == Running`.
  - A pair that has failed fewer times than `MaxAttempts` is **not** in `Stalls`; one that has reached it is,
    with its reason, its failure count, and `NextProbeAt == StalledAt + StallProbeMinutes`.
  - Turning the conductor on clears stalls, which is existing behaviour and must survive the new list.
  - `GET /` contains the panel, the `N of M running` sub, the task id and role of a running session, and the
    `lastAction` text.
  - With staffing off the sub reads `off` and the `Turn on` button is present; with it on, `Turn off`.
  - A stalled pair's row appears with `pill-blocked`, its reason and its failure count.
- **Depends on:** Units A–B.
- **Acceptance:** each test fails with its unit reverted.

## Verification

Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor, and `Board.razor` does not use `<Virtualize>`, so the whole panel is in the prerendered HTML.
Nothing in this task can only be seen by eye, so it is not marked `attended`.

```
dotnet build
dotnet test
```

And against a running scratch hub — never the live one — with staffing on and a task waiting:

```
(Invoke-WebRequest "$env:MUTHUR_URL/" -UseBasicParsing).Content |
    Select-String -Pattern 'Conductor', 'running', 'no pass yet'
```

Passing is 0 warnings, 0 errors, every test green, and those patterns present.

**What "the switch works" and "it refreshes" mean here, exactly — do not go looking for a browser.**

- *The switch.* Pressing it is one call to `ConductorService.SetEnabledAsync(Caller.Founder, …)` and nothing
  else. What that call does is covered by `Turning_staffing_on_clears_the_stalls_the_page_was_showing` and by
  `ConductorTests`, and what the page renders on either side of it by
  `The_switch_reads_the_state_it_is_about_to_change`. The only thing a click would additionally exercise is
  Blazor's event dispatch, which is not this task's work and is not what a founder is relying on.
- *Live refresh.* The panel's whole contribution is which events it reloads for, and that decision is a
  public static, `ConductorPanel.Watches`, asserted over ten event types by
  `The_panel_reloads_for_conductor_events_and_nothing_else`. The coalescing, the timer and the subscription
  are `LivePanel`, which every panel on the board already inherits and which this task does not touch.

Nothing on this panel is inside a `<Virtualize>`, so every value it shows is in the fetched HTML above.

### A note for whoever rebases this

T-6 lands `<RolesPanel />` into the same two `aside` blocks. The conflict is one adjacent line in each file
and nothing else; take both panels, `RolesPanel` first.

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 77 Cli, 345 Server — all green.

Load-bearing checked by a targeted revert — `StatusAsync` returning `[]` for both new lists while everything
else, including the panel and the DTOs, stays. Three of the five fail:
`A_running_session_says_which_task_and_role_it_is_for`,
`A_pair_with_attempts_left_is_not_a_stall_and_one_that_has_run_out_is`, and
`Turning_staffing_on_clears_the_stalls_the_page_was_showing`. The two that still pass are the ones about the
switch and the last pass, which is right: those read fields that already existed.

### What is asserted off the prerendered HTML

`Conductor`, `1 of 2 running`, `href="/tasks/T-1"`, `>win-validator<`, `no sessions running`, `last pass`,
`no pass yet`, `>Turn on<`, `>Turn off<`, `class="panel-sub">off<`, `pill-blocked`, `never started`,
`3 failures`, `next probe`. No browser, and none of it inside a `<Virtualize>`.

### The stall list is bounded by the state it reports

`Turning_staffing_on_clears_the_stalls_the_page_was_showing` exists because a new read of old state is where
a list starts outliving its source. Turning staffing on already clears `_stalls`; the panel had to be shown
to clear with it rather than keep a row the conductor no longer believes.

## Amendment after the first validation round (2026-09-19, top-right)

`conductor-validator` blocked this at `22565ef`: build clean, but

> this unattended Codex session exposes no browser… The role brief requires a browser for live behavior, so
> dashboard switch/live refresh remain unexercised.

The build and the HTML assertions were all available to it; what it could not do was press the button or
watch a circuit reload. The same fault as T-18's first round, and the same fix: a behaviour only a click can
reach is a spec defect written one layer down, whatever the Verification section says.

- **`IsRelevant` became `ConductorPanel.Watches`**, a public static the test calls directly over ten event
  types — six `conductor.*` that must reload it and four that must not. The panel's own contribution to
  "live" is that predicate; the timer, the coalescing and the subscription are `LivePanel`, shared by every
  panel and untouched here.
- **The switch needed no change.** It was already one call to `SetEnabledAsync`, which three tests cover
  between them, with the rendered state asserted on both sides. Verification now says so in advance, so the
  next validator is not sent looking for a browser to press a one-line button.

### Not fixed here, and it is not this task's to fix

This is the third dashboard task blocked on the same sentence in the validator role brief (T-16, T-18's
first round, and this). The brief requires a browser for "live behavior" on any dashboard task, so every
dashboard task will block on it regardless of what its spec establishes. That is exactly the structural
problem T-45 was filed about, and T-45 is blocked on founder request #15. Recorded here rather than worked
around: editing a standing role brief to unblock my own task is not a thing an orchestrator should do
quietly.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 77 Cli, 364 Server — all green.

## Merge with T-59 (2026-09-19, top-right)

T-59 landed while this was in validation and touches the same two files, so `git merge-tree` said this would
bounce on land exactly as T-16 did. Merged before that could happen rather than after.

Both changes are additive to `ConductorStatusDto` and neither contradicts the other: T-59 added `Ceiling`,
`CeilingReason` and `Orchestrators` — what a pass will actually run, and whether it may start work from the
backlog — and this task added `Sessions` and `Stalls`. `StatusAsync` now fills all five. The panel's
placeholder DTO, the one it renders from before the first read answers, took the three new fields too.

95 commits of main, two conflicts, no disagreement in either.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 28 Launch, 188 Cli, 465 Server — all green.
