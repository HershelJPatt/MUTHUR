# T-18 — Needs You when it is long: grouped, ranked, and honest about the wait

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the Needs You page stays readable when the queue is long. What is waiting is grouped by
kind, ordered by what it is costing rather than by id, every item says how long it has been waiting, and a
question several agents are asking in the same words can be answered once without any of them disappearing
from the page. The top bar carries the number **and** the oldest wait, because "210 waiting" and "210
waiting, the oldest since Tuesday" are different facts.

Nothing here answers, defers or expires anything. A request that ages out is a decision made by silence,
which is the one thing this design refuses.

## Context

- `src/Muthur.Server/Services/FounderAttention.cs` — `FounderAttentionDto(Requests, Outbound, Unread)` and
  `Total`. The single read both the badge and the page are built from, "so the two can never disagree".
- `src/Muthur.Server/Components/Layout/NeedsYouBadge.razor` — renders `Total` when it is above zero.
- `src/Muthur.Server/Components/Panels/RequestsPanel.razor` — the flat list, one `.request` per row, with
  per-request drafts and errors kept by id so a reload does not wipe what the founder is typing. That
  behaviour is load-bearing and must survive.
- `src/Muthur.Server/Services/RequestService.cs` — `ListAsync` (line 94) orders by id and is the only place
  the ordering can come from; `WithdrawForTaskAsync` and `UnblockAsync` show how a request relates to a task.
- `src/Muthur.Contracts/Messages.cs` — `FounderRequestDto`.
- `src/Muthur.Contracts/Outbound.cs` — `OutboundDto.Channel` is the target's channel, which is where "a
  flagged message to a file target" is distinguishable from one going somewhere real.
- `src/Muthur.Core/Entities/Entities.cs` — `WorkTask.ParentId`, which is how "a task three other tasks
  depend on" is counted.
- `src/Muthur.Server/wwwroot/app.css` — every class used must exist here; new tokens and classes go here,
  never inline styles.

Constraints that are not obvious:

- Dashboard components never touch `MuthurDb`; they call the same services the API uses. Live panels inherit
  `LivePanel`.
- Time comes from the injected `TimeProvider`.
- `NeedsYou.razor` does **not** use `<Virtualize>`, so everything it renders is present in the prerendered
  HTML and can be asserted with a fetch. This is what makes the task verifiable without a browser.
- Warnings are errors.

## Non-goals

- **New sources of attention.** T-18 names "a validation that failed twice" and "a project with no gate" as
  kinds of waiting. Neither reaches `FounderAttention` today, and both belong to tasks already in flight —
  T-45 owns the repeat-block signal, T-5 owns the gateless project. This task groups and ranks *what is
  already waiting*, and the grouping is built so that adding a kind later is adding a case, not a rewrite.
- **Anything automatic.** No answer, no default, no expiry, no "assume yes after a week". Said again here
  because it is the one thing the task forbids and the one thing a ranking feature tempts you into.
- **Paging or virtualizing the list.** `<Virtualize>` would make the page unassertable from prerendered HTML,
  which is a worse trade than a long page. Grouping is the readability fix; scrolling is not the problem.
- Changing how a request is asked, answered, withdrawn, or how it blocks and unblocks a task.

## Design

### What it is costing, stated as an order

`RequestService.ListAsync` stops ordering by id. `FounderRequestDto` gains two facts it already has the data
for, and they are what the order is computed from:

```csharp
/// <param name="BlocksTask">The task this asks about is actually sitting in 'blocked' waiting for the answer.</param>
/// <param name="Dependents">How many tasks name that blocked task as their parent. Work stopped behind this one.</param>
public sealed record FounderRequestDto(
    int Id, string? Task, string? TaskTitle, string Agent, string Question,
    IReadOnlyList<string> Options, string Status, string? Answer,
    DateTimeOffset CreatedAt, DateTimeOffset? AnsweredAt,
    bool BlocksTask = false, int Dependents = 0);
```

Open requests are returned ordered by, in this order:

1. `Dependents` descending — a question three tasks are stopped behind outranks one nothing waits on.
2. `BlocksTask` descending — a question whose task is genuinely blocked outranks one asked in passing.
3. `CreatedAt` ascending — among equals, the one that has waited longest.
4. `Id` ascending, so the order is total and two runs never disagree.

`Dependents` counts tasks whose `ParentId` is the blocked task and which are not `Done` or `Cancelled`:
finished children are not waiting on anything. It is 0 when the request blocks no task.

Closed requests (`openOnly: false`) keep today's `Id` order. The cost order is about what to do next, and
nothing is waiting on an answered question.

### Groups, and answering a kind in one pass

The panel renders one section per group instead of one flat list. A group is a set of **open requests with
the same question text and the same options**, which is the shape that actually recurs: five orchestrators
hitting the same policy question and asking it in the same words. Grouping is by
`(Question, string.Join('\u001f', Options))`, exact and ordinal — near-matching is guessing, and guessing
wrong here answers a question the founder did not read.

- A group of one renders exactly as a request renders today. Nothing about the single case changes.
- A group of several renders a header — `<div class="group-head">` with the question, the count
  (`@group.Count() waiting`), and the age of the oldest — then **every request in it**, each with its own
  agent, task, age, draft box and buttons. Seeing each one is not traded away for answering them together.
- A group of several also renders one `btn-row` of group answers, one button per option plus a shared
  textarea, which answers **every request in the group** with that text. Each answer goes through
  `RequestService.AnswerAsync` individually, in id order, so every unblock, ledger event and message happens
  exactly as if the founder had clicked them one at a time. A failure on one is reported against that
  request and does not stop the rest; the group button is not a transaction and must not pretend to be.

Groups are ordered by their best-ranked member, so the ordering rule above decides the page.

### The wait, on every row

Every request row already shows `Format.Age(request.CreatedAt, _now)`. Two additions:

- A blocking request also shows what it is holding up: `<span class="tag tag-blocked">blocking T-n</span>`,
  and when `Dependents > 0`, `+N behind it`.
- The group header shows the oldest wait in the group.

"Since when the agent has been idle because of it" is `CreatedAt`. A request is created at the moment its
asker stopped being able to proceed, so a second timestamp would be the same number with more ways to be
wrong. The spec says this out loud so nobody adds one later thinking it was missed.

### The top bar

`FounderAttentionDto` gains `OldestWaitingSince`: the earliest `CreatedAt` across open requests and awaiting
outbound, or null when nothing is waiting. Unread messages are counted in `Total` but not in the oldest
wait — a message is not a thing that blocks anybody, and letting it set the headline number's age would
overstate the queue.

`NeedsYouBadge` renders the count, and beside it the age when there is one:

```razor
<span class="badge">@_count</span>
@if (_oldest is { } since) { <span class="badge-age">@Format.Age(since, _now)</span> }
```

## Units of work

### Unit A — the cost order
- **Files:** `src/Muthur.Contracts/Messages.cs`, `src/Muthur.Server/Services/RequestService.cs`
- **Does:** `BlocksTask` and `Dependents` on the DTO, computed in `ListAsync`; the four-key order for open
  requests; closed requests unchanged.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean; Unit D's service-level tests pass.

### Unit B — the oldest wait
- **Files:** `src/Muthur.Server/Services/FounderAttention.cs`,
  `src/Muthur.Server/Components/Layout/NeedsYouBadge.razor`, `src/Muthur.Server/wwwroot/app.css`
- **Does:** `OldestWaitingSince` on the DTO and the badge beside the count; a `.badge-age` class.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean; the badge's age appears in the prerendered HTML of any page.

### Unit C — groups, and one pass over a kind
- **Files:** `src/Muthur.Server/Components/Panels/RequestsPanel.razor`, `src/Muthur.Server/wwwroot/app.css`
- **Does:** grouping, headers, the blocking tags, and the group answer — per Design. Per-request drafts,
  errors and the reload behaviour survive unchanged.
- **Depends on:** Unit A.
- **Acceptance:** `dotnet build` clean; Unit D's page tests pass.

### Unit D — the tests
- **Files:** `tests/Muthur.Server.Tests/`
- **Does:**
  - `ListAsync` returns a question with two dependents before one with none, a blocking question before a
    non-blocking one, and the older of two equals first; closed requests still come back in id order.
  - `Dependents` ignores children that are `Done` or `Cancelled`.
  - `FounderAttentionDto.OldestWaitingSince` is the earliest open request or awaiting outbound, is null when
    nothing waits, and is not moved by an unread message.
  - `GET /needs-you` contains the group header and the count for two alike requests, and contains **both**
    agents' rows — the group did not swallow either.
  - `GET /needs-you` for one request contains no group header, so the ordinary case is untouched.
  - The blocking tag and `+N behind it` appear in that HTML.
  - Answering a group answers every request in it: each is `answered`, each task unblocks, and the ledger
    holds one `request.answered` per request, not one for the group.
- **Depends on:** Units A–C.
- **Acceptance:** each test fails with its unit reverted.

## Verification

Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor. `NeedsYou.razor` does not use `<Virtualize>`, so the whole page is in the prerendered HTML.
Nothing in this task can only be seen by eye, so it is not marked `attended`.

```
dotnet build
dotnet test
```

And against a running scratch hub — never the live one — with two alike requests open:

```
(Invoke-WebRequest "$env:MUTHUR_URL/needs-you" -UseBasicParsing).Content |
    Select-String -Pattern 'group-head', '2 waiting', 'blocking T-', 'behind it'
```

Passing is 0 warnings, 0 errors, every test green, and those patterns present.
