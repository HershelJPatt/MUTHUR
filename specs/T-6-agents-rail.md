# T-6 — Agents rail on the dashboard: who is live, what they hold, what they own

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

The left rail answers the two questions it currently cannot: **what is this agent working on right now**, and
**who holds each role — or is it unheld**. Everything else T-6 asks for already exists and is not rebuilt.

## Context — most of this task is already built

Read this before writing anything. T-6 was filed on 2026-09-18, when the dashboard had no agent visibility.
It does now.

`src/Muthur.Server/Components/Panels/AgentsPanel.razor` already exists, already inherits `LivePanel`, and is
already in the left rail of both pages that have one (`Pages/Board.razor:7`, `Pages/NeedsYou.razor:7`, inside
`<aside class="side side-left">`). Against T-6's bullets:

| T-6 asks for | State |
|---|---|
| Live agents with harness/model/tier | **done** — plus account |
| Last heartbeat | **done** — `Format.Age` |
| Held roles | **done** — `pill-role` per role |
| Open task **count** per agent | **done** — `@a.OpenTasks tasks` |
| **The task they own now** | **missing** |
| **Roles listed with their holder, or unheld** | **missing** — no panel lists roles at all |
| Live like the rest of the board, no reload | **done** — `LivePanel`, 15-second `ClockInterval` |
| Components call services, never `MuthurDb` | **done** |

So this task is two additions, not a rail.

### The data is already there; no contract changes

- `TaskDto` (`src/Muthur.Contracts/Tasks.cs:20`) carries `Owner` and `State`, and `TaskService.ListAsync`
  takes a `TaskQuery`. `BoardPanel.razor:46` shows the idiom:
  `await Tasks.ListAsync(new TaskQuery(Project: _project, OpenOnly: true, Limit: 2000))`.
- `RoleDto` (`src/Muthur.Contracts/Roles.cs:9`) carries `Key`, `IsValidator`, `Capacity`,
  `Holders` (a list of `RoleHolderDto(Agent, HeldSince, LeaseExpires)`), `HasBrief` and `UpdatedAt`.
  `RoleService.ListAsync()` returns them. An unheld role is simply one with `Holders.Count == 0`, because the
  service only returns live holds — a lapsed lease is already absent.

**Do not add a field to `AgentDto` for the current task.** `AgentDto` crosses HTTP and the CLI prints it;
widening a shared contract for one dashboard panel is the wrong trade when the panel may read
`TaskService` directly, which is the documented pattern and what `BoardPanel` already does.

### `OpenTasks` is not "what they are working on"

`AgentService.ListAsync` counts every task an agent owns whose state is not `Done` or `Cancelled`
(`AgentService.cs:116-120`) — so backlog, in progress, blocked, validating and validated all count. An
orchestrator with six tasks in validation and one being worked shows `7 tasks`. That is the right number for
"what they hold" and the wrong one for "what they are working on", which is why this task adds the second
thing rather than changing the first.

### The rail is assertable from fetched HTML

`DashboardAgentsTests.Agents_panel_lists_live_agents_before_stale_ones` already asserts agent markup out of
`GET /`. The rail is an `<aside>` sibling of `BoardPanel`, so it is **outside** the `<Virtualize>` that makes
the board's own cards invisible to a prerender. No browser is needed for any part of this task.

### The deliberate overlap with the validation queue

`ValidationQueuePanel` (right rail, T-13) already shows validator roles as `Holders/Capacity`. The new roles
panel overlaps it for validator roles and that is intended: the queue answers *"is anyone validating, and how
deep is the queue"*, the rail answers *"who holds what"*. The rail also covers the on-call roles
(`comms-oncall`, `knowledge-oncall`, `observability-oncall`, `release-oncall`), which appear nowhere today.
Do not exclude validator roles from the new panel to avoid the overlap — a rail that omits half the roles
misrepresents the organization.

## Non-goals

- Any change to `src/Muthur.Contracts`, any service, the API, or the CLI. This is two components and one
  stylesheet rule.
- Any change to `ValidationQueuePanel`, `BoardPanel`, or the right rail.
- Showing a role's brief, or making anything on the rail clickable.
- Reordering or restyling the existing agent card beyond the one line added in Unit A.

## Design

### Unit A — the task an agent is working on

In `AgentsPanel.razor`:

- `@inject TaskService Tasks` alongside the existing injections.
- In `LoadAsync`, after the agents read:

  ```csharp
  var open = await Tasks.ListAsync(new TaskQuery(OpenOnly: true, Limit: 2000));
  _working = open
      .Where(t => t.Owner is not null && t.State == TaskState.InProgress)
      .GroupBy(t => t.Owner!, StringComparer.Ordinal)
      .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Priority).ThenBy(t => t.Id, StringComparer.Ordinal).First(), StringComparer.Ordinal);
  ```

  `private Dictionary<string, TaskDto> _working = new(StringComparer.Ordinal);`

- Render one line inside the existing `<div class="agent …">`, **after** the `agent-model` line and **before**
  the `agent-summary` block, so the identity of the agent reads before what it is doing:

  ```razor
  @if (_working.TryGetValue(a.Name, out var working))
  {
      <div class="agent-task">@working.Id · @working.Title</div>
  }
  ```

- Nothing renders when the agent has no task in progress. The existing count pill already says how many they
  hold; an agent between tasks gets no line rather than an empty one.

**Only `InProgress` counts.** A task in `Validating` is owned but not being worked, and it is the state this
organization's orchestrators are in most of the time. Showing one there would make the rail say an
orchestrator is working on something it handed away.

`IsRelevant` already includes `task.`, so the line refreshes when a task is claimed or handed on. Do not
change it.

### Unit A — the one stylesheet rule

In `src/Muthur.Server/wwwroot/app.css`, immediately after the existing `.agent-model` rule, matching its
shape:

```css
.agent-task { font: 11px var(--mono); color: var(--text); margin-top: 4px; }
```

No inline styles, and no other rule changes.

### Unit B — a roles panel in the left rail

New `src/Muthur.Server/Components/Panels/RolesPanel.razor`, `@inherits LivePanel`, `@inject RoleService Roles`.

**Reuse the existing card classes.** `Pages/Projects.razor` already dresses a *project* in `.agent`,
`.agent-head` and `.agent-name`, so these are the codebase's generic card classes rather than agent-specific
ones. Reuse them and add **no** CSS for this unit.

```razor
<section class="panel">
    <div class="panel-head">
        <span class="panel-title">Roles</span>
        <span class="panel-sub">@_held of @_roles.Count held</span>
    </div>
    <div class="panel-body">
        @if (_roles.Count == 0)
        {
            <div class="empty">no roles defined · muthur role define &lt;key&gt; --brief-file … --founder</div>
        }
        @foreach (var r in _roles)
        {
            <div class="agent">
                <div class="agent-head">
                    <span class="agent-name">@r.Key</span>
                    @if (r.IsValidator)
                    {
                        <span class="tag">validator</span>
                    }
                    @if (r.Capacity > 1)
                    {
                        <span class="agent-age">@r.Holders.Count of @r.Capacity</span>
                    }
                </div>
                <div class="agent-meta">
                    @if (r.Holders.Count == 0)
                    {
                        <span class="pill">unheld</span>
                    }
                    @foreach (var h in r.Holders)
                    {
                        <span class="pill pill-role">@h.Agent</span>
                    }
                </div>
            </div>
        }
    </div>
</section>
```

- `_roles` is `RoleService.ListAsync()` ordered by `Key` with `StringComparer.Ordinal`. Alphabetical, not
  held-first: a rail whose rows move as leases come and go is harder to read than one that does not.
- `_held` is the count of roles with at least one holder.
- `unheld` uses the **plain** `pill`, not an amber one. An unheld role is a normal state of the organization,
  not a fault, and the rail should not shout about `knowledge-oncall` being free.
- `IsRelevant` returns true for event types starting `role.` or `agent.` — a role is defined, taken, released,
  or its holder goes stale.
- `ClockInterval` of 15 seconds, matching `AgentsPanel`: a lease lapses with nothing written.

### Unit B — put it in the rail

In `Pages/Board.razor` and `Pages/NeedsYou.razor`, add `<RolesPanel />` immediately after `<AgentsPanel />`
inside `<aside class="side side-left">`. Both pages, so the rail is the same rail.

## Units of work

### Unit A — what an agent is working on
- **Files:** `src/Muthur.Server/Components/Panels/AgentsPanel.razor`,
  `src/Muthur.Server/wwwroot/app.css`, `tests/Muthur.Server.Tests/DashboardAgentsTests.cs`.
- **Does:** both Unit A sections.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and tests that, from
  `GET /`:
  - an agent holding an `in_progress` task shows that task's id **and** title;
  - an agent whose only open task is `validating` shows **no** `agent-task` line, while its count pill still
    reads `1 task` — this is the distinction the panel exists to draw, so assert both halves in one test;
  - an agent holding two `in_progress` tasks shows the higher-priority one.

### Unit B — the roles panel
- **Files:** new `src/Muthur.Server/Components/Panels/RolesPanel.razor`,
  `src/Muthur.Server/Components/Pages/Board.razor`, `src/Muthur.Server/Components/Pages/NeedsYou.razor`,
  new `tests/Muthur.Server.Tests/DashboardRolesTests.cs`.
- **Does:** both Unit B sections. No CSS changes at all.
- **Depends on:** nothing. Does not touch `AgentsPanel.razor` or `app.css`.
- **Acceptance:** `dotnet build` clean, `dotnet test` green, and tests that, from `GET /`:
  - a defined role with a live holder shows the role key and the holder's name;
  - a role nobody holds shows `unheld`;
  - a role whose holder's lease has lapsed shows `unheld` — advance `HubFactory.Clock` past the role lease
    rather than sleeping;
  - a validator role with capacity above 1 shows `n of c`;
  - a hub with no roles shows the empty state;
  - `/needs-you` carries the panel too.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then the rail itself, fetched — **no browser, and none should be used**. With an installed CLI
from this branch (`pwsh ./scripts/install.ps1 -Destination ./artifacts/t6`) against a scratch hub on its own
`MUTHUR_HOME` and port, never the live one:

```powershell
muthur agent register --name worker --harness claude --model opus --tier implementer --founder
muthur role define reviewer --brief-file <any file> --founder
muthur role define pair --brief-file <any file> --validator --holders 2 --founder
# then, as the agent, claim a task so it has one in progress
$html = (Invoke-WebRequest "$env:MUTHUR_URL/" -UseBasicParsing).Content
```

Passing looks like, in that fetched HTML:

- the agent's name, `claude/opus`, `implementer`;
- an `agent-task` line carrying the id **and** title of the task it has in progress;
- a `Roles` panel head with `n of m held`;
- `reviewer` with the holder's name in a `pill-role` when held, and `unheld` when nobody holds it;
- `pair` showing `0 of 2` when unheld;
- the same `Roles` panel present on `/needs-you`.

Exercise the lapse too: advance nothing, simply release the role (`muthur role release <key>`) and re-fetch —
the row must flip to `unheld` without a reload of anything but the page you fetch.

## Out of scope / follow-ups

- **`OpenTasks` counts tasks in validation**, which is correct for "what they hold" but means the count pill
  and the new task line describe different sets. Whether the pill should break down by state is a design
  question for the rail's next pass.
- **The validation queue and the roles panel both show validator roles.** Deliberate, per Context. If the
  rail ever feels crowded, the question is which panel should stop — not whether to filter one silently.

## Proof (2026-09-19)

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test
  Muthur.Launch.Tests    23/23
  Muthur.Core.Tests      3708/3708
  Muthur.Cli.Tests       65/65
  Muthur.Server.Tests    316/316     (307 before this task: +3 Unit A, +6 Unit B)
```

The rail itself, fetched from a scratch hub on port 7521 with a CLI installed from this branch — one agent
holding `reviewer` and working `T-1`, one validator role with room for two and nobody in it:

```html
<div class="agent "><div class="agent-head"><span class="dot"></span>
  <span class="agent-name">worker</span><span class="tag">implementer</span><span class="agent-age">0s</span></div>
  <div class="agent-model">claude/opus</div>
  <div class="agent-task">T-1 · Wire the rail</div>
  <div class="agent-meta"><span class="pill pill-role">reviewer</span><span class="pill">1 task</span></div></div>
```

```html
<span class="panel-title">Roles</span> <span class="panel-sub">1 of 2 held</span>
  <div class="agent"><div class="agent-head"><span class="agent-name">pair</span>
    <span class="tag">validator</span><span class="agent-age">0 of 2</span></div>
    <div class="agent-meta"><span class="pill">unheld</span></div></div>
  <div class="agent"><div class="agent-head"><span class="agent-name">reviewer</span></div>
    <div class="agent-meta"><span class="pill pill-role">worker</span></div></div>
```

Every bullet of the Verification section: the agent's name, `claude/opus` and `implementer`; an `agent-task`
line carrying **both** the id and the title of the task in progress; the `Roles` head reading `1 of 2 held`;
`reviewer` naming its holder in a `pill-role`; `pair` showing `0 of 2` and `unheld`; and the same panel on
`/needs-you`.

Releasing the role and re-fetching flips it, with nothing written but the release:

```
after release: <span class="panel-sub">0 of 2 held</span> … reviewer … unheld
```

Scratch hub stopped; the live hub's `processId` 63224 is unchanged.

**No browser was used, and none is needed.** The rail is an `<aside>` sibling of `BoardPanel`, outside the
`<Virtualize>` that hides the board's own cards.

## Out of scope / follow-ups (added)

- **The rail's task query has no project filter.** `BoardPanel` filters by the selected project; the rail does
  not, deliberately, because it describes agents rather than a project. On a multi-project hub with the board
  filtered, an agent's `agent-task` line can name a task the board is not showing. Correct today; worth a
  decision if the rail ever gains a project sense.
- **`TaskService.ListAsync` clamps `Limit` to 2000** and orders by priority descending, so past 2000 open
  tasks a low-priority in-progress task could go unnamed. The same exposure `BoardPanel` already carries, so
  it belongs to whichever task addresses that, not this one.
