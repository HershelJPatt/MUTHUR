# T-4 — "Needs you" page: answer founder requests and talk to agents from the dashboard

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

Agents can ask the founder questions (`muthur ask`) and message them (`muthur msg send --to founder`), but today
the founder can only answer from the CLI. After this task the dashboard's **Needs you** page lists every open
request with one-click answers, shows the founder's message thread, lets the founder message any agent or role,
and the top-bar tab shows a live count of what is waiting.

## Context

- Read `CLAUDE.md`. Dashboard rules: components call application services, never `MuthurDb`; live components
  inherit `LivePanel`; **only classes already in `src/Muthur.Server/wwwroot/app.css`; no edits to `app.css`; no inline styles.**
- Pattern to copy: `Components/Panels/BoardPanel.razor` (structure) and `Components/Pages/Projects.razor` (a simple page).
- The dashboard runs on the founder's machine and acts as the founder: pass `Caller.Founder` (`Muthur.Server.Auth`) as the caller.
- Services (namespace `Muthur.Server.Services`, all registered as singletons):
  - `RequestService.ListAsync(bool openOnly, int limit = 200)` → `IReadOnlyList<FounderRequestDto>` oldest first
  - `RequestService.AnswerAsync(Caller, int id, AnswerRequest)` · `RequestService.CancelAsync(Caller, int id)`
  - `MessageService.HistoryAsync(int limit, bool founderThread)` → `IReadOnlyList<MessageDto>` oldest first
  - `MessageService.SendAsync(Caller, SendMessageRequest)` — `To` is an agent name, `role:<key>`, or `founder`
  - `MessageService.UnreadForFounderAsync()` → `int` · `MessageService.MarkFounderReadAsync(Caller)`
  - `AgentService.ListAsync()` · `RoleService.ListAsync()`
  - All throw `Muthur.Core.MuthurException` on rule violations/conflicts; show `ex.Message`.
- DTOs are in `src/Muthur.Contracts/Messages.cs`. `FounderRequestDto.Status` is `"open" | "answered" | "cancelled"`.
- Ledger event types involved: `request.asked`, `request.answered`, `request.cancelled`, `message.sent`, `message.read`.

## Non-goals

- No changes to services, contracts, API, CLI, `app.css`, `LivePanel`, or any existing panel.
- No notification sounds, no browser notifications, no markdown rendering of message bodies (plain text).
- No history of answered requests on this page.

## Design

### `Components/Layout/NeedsYouBadge.razor` (LivePanel)

Renders `<span class="badge">{n}</span>` when `n > 0`, otherwise nothing.
`n` = open requests count + `UnreadForFounderAsync()`. `IsRelevant`: type starts with `request.` or `message.`.

### `Components/Layout/MainLayout.razor` (modify one line)

`<NavLink href="/needs-you">Needs you</NavLink>` becomes `<NavLink href="/needs-you">Needs you<NeedsYouBadge /></NavLink>`.

### `Components/Panels/RequestsPanel.razor` (LivePanel)

```html
<section class="panel">
    <div class="panel-head">
        <span class="panel-title">Waiting on you</span>
        <span class="panel-sub">{count} open</span>
    </div>
    <div class="panel-body">
        <div class="empty">nothing is waiting on you</div>                       <!-- when count == 0 -->
        <!-- per request, oldest first -->
        <div class="request">
            <div class="request-head">
                <span class="tag tag-open">#{Id}</span>
                <span class="event-actor">{Agent}</span>
                <a class="event-task" href="/tasks/{Task}">{Task}</a> <span>{TaskTitle}</span>   <!-- both only when Task != null -->
                <span class="card-age">{Format.Age(CreatedAt, now)}</span>
            </div>
            <div class="request-question">{Question}</div>
            <div class="btn-row">                                                  <!-- only when Options.Count > 0 -->
                <button class="btn btn-go" @onclick=answer-with-that-option>{option}</button>   <!-- one per option -->
            </div>
            <textarea class="textarea" placeholder="or answer in your own words…" @bind=draft (per request) />
            <div class="btn-row">
                <button class="btn btn-go" disabled when draft is blank>Answer</button>
                <button class="btn btn-stop">Withdraw</button>                      <!-- CancelAsync -->
            </div>
            <div class="error-text">{message}</div>                                <!-- only after a failed action on this request -->
        </div>
    </div>
</section>
```

- Drafts and error messages are kept per request id (`Dictionary<int, string>`), so a reload triggered by another
  event does not wipe what the founder is typing. Remove a request's draft/error once it is answered or withdrawn.
- After a successful answer/withdraw call `ReloadNowAsync()`.
- `IsRelevant`: type starts with `request.`. `ClockInterval`: 30 s.

### `Components/Panels/CommsPanel.razor` (LivePanel)

```html
<section class="panel">
    <div class="panel-head"><span class="panel-title">Comms</span><span class="panel-sub">you ↔ the organization</span></div>
    <div class="panel-body">
        <div class="empty">no messages yet</div>                                    <!-- when the thread is empty -->
        <!-- per message, oldest first, last 60 (HistoryAsync(60, founderThread: true)) -->
        <div class="event">
            <span class="event-age">{Format.Age(At, now)}</span>
            <div>
                <div class="event-line">
                    <span class="event-actor">{From}</span> <span class="event-detail">→ {To}</span>
                    <span class="tag tag-open">blocking</span>                       <!-- only when Blocking -->
                    <a class="event-task" href="/tasks/{Task}">{Task}</a>            <!-- only when Task != null -->
                </div>
                <div class="prose">{Body}</div>
            </div>
        </div>
    </div>
    <div class="panel-head">                                                         <!-- composer, below the list -->
        <select class="select" @bind=recipient>
            <option value="">to…</option>
            <option value="{agent.Name}">{agent.Name}</option>                       <!-- every agent, by name -->
            <option value="role:{role.Key}">role:{role.Key}</option>                 <!-- every role, by key -->
        </select>
    </div>
    <div class="panel-body panel-fit">
        <textarea class="textarea" placeholder="message…" @bind=body />
        <div class="btn-row"><button class="btn btn-go" disabled when recipient or body is blank>Send</button></div>
        <div class="error-text">{message}</div>                                     <!-- only after a failed send -->
    </div>
</section>
```

- Send: `MessageService.SendAsync(Caller.Founder, new SendMessageRequest(recipient, body))`; on success clear
  `body` (keep the recipient) and `ReloadNowAsync()`.
- Mark the founder's messages read **only once the page is interactive**: override `OnAfterRenderAsync(bool firstRender)`
  and on `firstRender` call `MarkFounderReadAsync(Caller.Founder)`. Do not mark read during prerender or in `LoadAsync`.
- `IsRelevant`: type starts with `message.`, `agent.` or `role.`. `ClockInterval`: 30 s.
- Use `@bind:event="oninput"` on the textareas so the disabled state of the buttons follows typing.

### `Components/Pages/NeedsYou.razor`

```html
@page "/needs-you"
<PageTitle>MUTHUR · Needs you</PageTitle>
<div class="dashboard">
    <aside class="side side-left"><AgentsPanel /></aside>
    <RequestsPanel />
    <aside class="side side-right"><CommsPanel /></aside>
</div>
```

## Units of work

### Unit A — all of the above (single unit; the pieces are small and share state conventions)
- **Files:** create `Components/Layout/NeedsYouBadge.razor`, `Components/Panels/RequestsPanel.razor`,
  `Components/Panels/CommsPanel.razor`, `Components/Pages/NeedsYou.razor`,
  `tests/Muthur.Server.Tests/DashboardNeedsYouTests.cs`; modify `Components/Layout/MainLayout.razor` (the one line).
- **Depends on:** nothing
- **Acceptance** (`DashboardNeedsYouTests`, one `HubFactory` per class, style of `AgentTests.cs`):
  1. With a project, an agent `asker`, a claimed task `"Pricing page"` and a request
     `AskRequest("Monthly or annual first?", task.Id, ["monthly", "annual"])`: GET `/needs-you` contains
     `Monthly or annual first?`, `asker`, `Pricing page`, `>monthly<`, `>annual<`, `1 open`; and GET `/` contains
     `class="badge">1<`.
  2. After the founder answers that request through the API (`Routes.RequestAction(id, "answer")`), GET `/needs-you`
     contains `nothing is waiting on you` and GET `/` no longer contains `class="badge"`.
  3. After agent `asker` sends `SendMessageRequest("founder", "deploy is <b>done</b>")`: GET `/needs-you` contains
     `deploy is &lt;b&gt;done&lt;/b&gt;` (escaped), GET `/` contains `class="badge">1<`, and — because prerender must
     not mark messages read — a second GET `/` still contains `class="badge">1<`.

## Verification

```
dotnet build          # 0 warnings, 0 errors
dotnet test           # all green
```

End to end (validator): scratch instance; from the CLI `muthur ask "…" --task T-n --option a --option b` as an agent
and `muthur msg send --to founder "…"`; open `/needs-you` in a browser: the request and message appear without reload,
the tab badge counts them; clicking an option answers the request (the CLI's `muthur msg inbox` then returns the
answer and the task is `in_progress` again); sending a message from the composer reaches `muthur msg inbox` of that agent.

## Out of scope / follow-ups

- Outbound approvals will join this page with M8.
