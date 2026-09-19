# T-10 — Operations page: inbound, outbound (founder approve/decline), harness tiers

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

Ingest (M7), the outbound gate (M8) and the harness catalog (M6) exist only in the CLI. After this task the dashboard
has an **Operations** page showing what arrived, what wants to leave, and who staffs each tier — and the founder can
approve or decline outbound messages that wait for them, from the page. The top-bar badge counts those too.

## Context

- Read `CLAUDE.md`. Dashboard rules: components call application services, never `MuthurDb`; live components inherit
  `LivePanel`; **only classes already in `src/Muthur.Server/wwwroot/app.css`; no edits to `app.css`; no inline styles.**
- Patterns to copy: `Components/Panels/RequestsPanel.razor` (per-item drafts/errors, actions as `Caller.Founder`,
  `ReloadNowAsync()` after an action, `MuthurException.Message` in `.error-text`) and `Components/Panels/AgentsPanel.razor`.
- Services (`Muthur.Server.Services`, singletons):
  - `InboundService.ListAsync(string? status, string? project, int limit)` → `IReadOnlyList<InboundDto>` oldest first; status `"all"` for everything
  - `OutboundService.ListAsync(string? status, int limit)` → `IReadOnlyList<OutboundDto>` oldest first; status `"all"` for everything
  - `OutboundService.FounderApproveAsync(Caller caller, string id, bool approve, string? note)`
  - `HarnessService.TiersAsync(string? tier = null)` → `IReadOnlyList<TierDto>`
- DTOs: `src/Muthur.Contracts/Inbound.cs`, `Outbound.cs`, `Harnesses.cs`. `InboundDto.Status` is
  `unclaimed|claimed|converted|dismissed`; `OutboundDto.Status` is `pending_review|rejected|awaiting_founder|approved|sent|failed`.
- Ledger event type prefixes: `inbound.`, `outbound.`, `account.`, `agent.`.
- `<Virtualize>` renders nothing during prerender; none of these lists is virtualized (they are short).

## Non-goals

- No changes to services, contracts, API, CLI, `app.css`, `LivePanel`, or existing panels other than the two edits named below.
- No claiming/converting inbound items or reviewing outbound from the dashboard (agents do that).
- The target's address must never be shown (it is not in the DTO; do not try to get it).

## Design

### `Components/Pages/Operations.razor`

```html
@page "/operations"
<PageTitle>MUTHUR · Operations</PageTitle>
<div class="dashboard">
    <aside class="side side-left"><HarnessPanel /></aside>
    <OutboundPanel />
    <aside class="side side-right"><InboundPanel /></aside>
</div>
```

### `Components/Layout/MainLayout.razor` (add one line)

After the `Stream` NavLink add `<NavLink href="/operations">Operations</NavLink>`.

### `Components/Layout/NeedsYouBadge.razor` (modify)

`n` additionally counts outbound messages with status `awaiting_founder`
(`OutboundService.ListAsync("awaiting_founder", 500)`). `IsRelevant` additionally matches types starting with `outbound.`.

### `Components/Panels/InboundPanel.razor` (LivePanel)

Loads `InboundService.ListAsync("all", null, 300)`, keeps items with status `unclaimed` or `claimed`, newest first.

```html
<section class="panel">
    <div class="panel-head"><span class="panel-title">Inbound</span><span class="panel-sub">{unclaimed} unclaimed · {claimed} claimed</span></div>
    <div class="panel-body">
        <div class="empty">nothing waiting</div>                                   <!-- when the list is empty -->
        <div class="agent">                                                         <!-- per item -->
            <div class="agent-head">
                <span class="tag tag-open">{Id}</span>                              <!-- "tag tag-held" when status is claimed -->
                <span class="agent-model">{Source}</span>
                <span class="agent-age">{Format.Age(ReceivedAt, now)}</span>
            </div>
            <div class="agent-summary">{Title}</div>
            <div class="agent-meta">                                                <!-- only when it would have a child -->
                <span class="pill">{Author}</span>                                  <!-- when Author != null -->
                <span class="pill pill-owner"><span class="dot"></span>{ClaimedBy}</span>   <!-- when ClaimedBy != null -->
                <a class="pill pill-role" href="{Url}" target="_blank" rel="noopener">source</a>   <!-- when Url != null -->
            </div>
        </div>
    </div>
</section>
```

`IsRelevant`: type starts with `inbound.`. `ClockInterval`: 30 s.

### `Components/Panels/OutboundPanel.razor` (LivePanel)

Loads `OutboundService.ListAsync("all", 200)` and shows, newest first: every message whose status is `pending_review`,
`awaiting_founder`, `approved` or `failed`, followed by at most the 10 most recent `sent` or `rejected`.

```html
<section class="panel">
    <div class="panel-head"><span class="panel-title">Outbound</span><span class="panel-sub">{awaiting} need you · {open} in the gate</span></div>
    <div class="panel-body">
        <div class="empty">nothing wants to leave</div>                             <!-- when the list is empty -->
        <div class="request">                                                       <!-- per message -->
            <div class="request-head">
                <span class="tag tag-open">{Id}</span>
                <span class="pill {statusClass}">{Status with "_" replaced by " "}</span>
                <span class="event-detail">→ {Target} · {Channel}</span>
                <a class="event-task" href="/tasks/{Task}">{Task}</a>               <!-- when Task != null -->
                <span class="card-age">{Format.Age(CreatedAt, now)}</span>
            </div>
            <div class="code">{Body}</div>
            <div class="request-head">
                <span class="event-actor">{Author}</span><span class="agent-model">{AuthorModel}</span>
                <span class="event-detail">reviewed by {Reviewer} ({ReviewerModel})</span>   <!-- when Reviewer != null -->
                <span class="event-detail">{ReviewNote}</span>                      <!-- when ReviewNote is not blank -->
                <span class="event-detail">sha256 {first 12 chars of Sha256}</span>
            </div>
            <div class="error-text">{Error}</div>                                   <!-- when Error != null (failed delivery) -->
            <!-- only when Status == "awaiting_founder": -->
            <textarea class="textarea" placeholder="reason, if you decline…" @bind=note (per message) @bind:event="oninput" />
            <div class="btn-row">
                <button class="btn btn-go">Approve</button>
                <button class="btn btn-stop">Decline</button>
            </div>
            <div class="error-text">{action error}</div>                            <!-- only after a failed approve/decline on this message -->
        </div>
    </div>
</section>
```

- `statusClass`: `pill-blocked` for `pending_review` and `awaiting_founder`; `pill-verdict-yes` for `approved` and `sent`;
  `pill-verdict-no` for `rejected` and `failed`.
- `{awaiting}` = count with status `awaiting_founder`; `{open}` = count with status `pending_review`, `awaiting_founder`, `approved` or `failed`.
- Approve → `FounderApproveAsync(Caller.Founder, id, true, null)`; Decline → `FounderApproveAsync(Caller.Founder, id, false, note)`
  (a blank note is passed as null). Notes and action errors are kept per message id (`Dictionary<string, string>`), survive
  reloads caused by other events, and are removed when the message is no longer `awaiting_founder`.
- `IsRelevant`: type starts with `outbound.`. `ClockInterval`: 30 s.

### `Components/Panels/HarnessPanel.razor` (LivePanel)

```html
<section class="panel">
    <div class="panel-head"><span class="panel-title">Tiers</span><span class="panel-sub">harnesses.json</span></div>
    <div class="panel-body">
        <div class="section-label">{Tier}</div>                                     <!-- per tier, catalog order -->
        <div class="role-row">                                                      <!-- per candidate, catalog order -->
            <span class="tag tag-held">{1-based position}</span>                    <!-- "tag" (no tag-held) when Limited -->
            <span>{Harness}/{Model, or "default" when empty}</span>
            <span class="role-holder">{Account}{" · limited " + Format.Until(LimitedUntil, now) when Limited}</span>
        </div>
    </div>
</section>
```

If `TiersAsync` throws `MuthurException` (invalid catalog file) show `<div class="error-text">{message}</div>` in the body instead.
`IsRelevant`: type starts with `account.` or `agent.`. `ClockInterval`: 30 s.

## Units of work

### Unit A — everything above
- **Files:** create `Components/Pages/Operations.razor`, `Components/Panels/InboundPanel.razor`,
  `Components/Panels/OutboundPanel.razor`, `Components/Panels/HarnessPanel.razor`,
  `tests/Muthur.Server.Tests/DashboardOperationsTests.cs`; modify `Components/Layout/MainLayout.razor` (one line) and
  `Components/Layout/NeedsYouBadge.razor`.
- **Acceptance** (`DashboardOperationsTests`, one `HubFactory` per class, style of `DashboardNeedsYouTests.cs`):
  1. After an agent pushes `AddInboundRequest("email", "m-1", "Export is <b>broken</b>", Author: "customer@example.com")`:
     GET `/operations` contains `I-1`, `Export is &lt;b&gt;broken&lt;/b&gt;`, `customer@example.com`, `1 unclaimed`.
  2. With a `file` target `press` that requires founder approval (address: a path under `hub.DataDir`), a draft by agent
     `author` approved by agent `peer`: GET `/operations` contains `O-1`, `awaiting founder`, `press`, `>Approve<`,
     `>Decline<`, `1 need you`, the first 12 characters of the draft's sha256, and does **not** contain the file path;
     GET `/` contains `class="badge">1<`. After `FounderApproveAsync` through the API (`Routes.OutboundAction(id, "approve")`
     as founder), GET `/operations` no longer contains `>Approve<` and GET `/` no longer contains `class="badge"`.
  3. GET `/operations` contains `implementer`, `mastermind`, `utility` and at least one `claude/` candidate; after an
     agent registered with the first implementer candidate's harness/model/account posts `LimitedRequest(now + 2h)`,
     GET `/operations` contains `limited in`.

## Verification

```
dotnet build          # 0 warnings, 0 errors
dotnet test           # all green
```

End to end (validator, real browser): with `/operations` open, `inbound add …` from the CLI appears without reload;
an outbound draft → peer approval to a `--founder-approval` target shows Approve/Decline, the tab badge counts it,
clicking Approve lets `out send` succeed and the author's `msg inbox` has the founder's message; Decline with a note
reaches the author; a note being typed is not wiped when another event arrives; the target's address appears nowhere in the page.

## Out of scope / follow-ups

- Dashboard actions for inbound (claim/convert) and outbound peer review stay CLI-only by design: those are agents' jobs.
