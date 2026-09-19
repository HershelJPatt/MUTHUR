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
  and when `Dependents > 0`, `N behind it`. (No leading `+`: Razor emits one as `&#x2B;`, which is correct HTML and an unpleasant thing to assert against.)
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
  - The blocking tag and `N behind it` appear in that HTML.
  - Answering a group answers every request in it: each is `answered`, each task unblocks, and the ledger
    holds one `request.answered` per request, not one for the group.
- **Depends on:** Units A–C.
- **Acceptance:** each test fails with its unit reverted.

## Verification

Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor. `NeedsYou.razor` does not use `<Virtualize>`, so the whole page is in the prerendered HTML.
Nothing in this task can only be seen by eye, so it is not marked `attended`.

**What "the group answer works" means here, exactly.** Pressing the button is a call to
`RequestService.AnswerManyAsync` and nothing else; the component keeps only the display decision of whose row
shows an error afterwards. So the behaviour a validator needs to establish — every request answered on its own,
in id order, one refusal not taking the rest with it, one ledger event and one message per request — is
established by calling that method, which the tests do. **Do not go looking for a browser to press the button
with.** Clicking it would exercise Blazor's event dispatch, which is not this task's work and is not what the
founder is relying on.

Two things in this area are genuinely click-only, and **neither is new in this task**: the answer and withdraw
buttons on a single request, and the per-id draft boxes surviving a reload. Both predate it and both are
unchanged — `DashboardNeedsYouTests` is the existing coverage, and it still passes.

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

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3708 Core, 23 Launch, 65 Cli, 286 Server — all green,
including the existing `DashboardNeedsYouTests`, which is what says the single-request page did not change.

Load-bearing checked by a **targeted** revert rather than by deleting the feature: putting back
`OrderBy(r => r.Id)` in `ListAsync`, and replacing the panel's `GroupBy` with one group per request, while
leaving the DTO and everything else in place. Exactly two tests fail —
`Questions_come_back_in_the_order_of_what_they_are_costing` and
`A_question_two_agents_asked_in_the_same_words_is_one_group_that_still_shows_both` — which are the two claims
this task rests on. Reverting the whole of `src/` instead only proves the tests reference the new members,
which is not the same thing and is not worth the confidence it looks like.

### One thing the rendering decided

The spec said the dependents tag would read `+N behind it`. Razor emits a literal leading `+` as `&#x2B;`,
so the prerendered HTML said `&#x2B;2 behind it` — correct HTML, and a poor thing to ask a validator to
match. The tag reads `N behind it` instead. The `+` was decoration; the number is the fact.

## Amendment after the first validation round (2026-09-19, top-right)

`conductor-validator` blocked this at `debae06`. The build was clean and the HTML assertions were all
available to it; what it could not do was press the group-answer button:

> browser inventory is empty and unattended browser creation returns Browser is not available: iab. Cannot
> exercise group-answer buttons, partial failures, or live draft preservation.

It was right to stop, and the fault was mine rather than the validator's. My *Verification* did not ask for a
click, but the behaviour it was asking the validator to trust — one answer going to several requests, and one
refusal not taking the rest with it — lived inside `RequestsPanel.AnswerGroupAsync`, where a click is the only
thing that reaches it. A spec whose substance is only reachable by clicking is the same defect as one whose
Verification says "click", written a layer further down.

### The fix: the logic moves to where a test can reach it

`RequestService.AnswerManyAsync(caller, ids, answer)` now holds the whole of it — answering each request on
its own in id order, catching each `MuthurException`, and returning the refusals by id. The panel's handler
is one call to it plus the display decision of whose row shows an error. This is also where the logic belonged
under the existing rule that dashboard components call the same services the API uses.

Three tests now establish, with no browser, what the validator was asked to take on faith:

- `Answering_a_group_answers_every_request_in_it_one_at_a_time` — both tasks leave `blocked`, both requests
  are `answered`.
- `One_request_that_refuses_does_not_take_the_rest_of_the_group_with_it` — a withdrawn request in the middle
  of the group is reported by id with its own message, the other two are answered, and it stays `cancelled`.
  This is the property that matters most and the one a click could least be trusted to show.
- `A_group_answer_is_recorded_once_per_request_and_wakes_each_asker` — one `request.answered` per request in
  id order however the ids were passed, and each asker's own inbox names their own question.

The Verification section now says all this in advance, so the next validator is not sent looking for a browser
to press a button that is one line long.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3708 Core, 23 Launch, 65 Cli, 293 Server — all green.

## Amendment after the second validation round (2026-09-19, top-right)

`conductor-validator` failed `8783ade` with the finding this task is named for, built on a real installed hub
with 210 open requests:

> At 210 open requests the oldest, most costly blocking question disappears and the badge undercounts.
> `GET requests?limit=1000` confirms all 210 and #1 ranked first.

Both halves were right, and both were mine.

### The cap was applied before the ranking

`ListAsync` took `.OrderByDescending(r => r.Id).Take(200)` and *then* ranked the survivors. So at 210 open the
newest 200 were kept and ranked beautifully, while the one question that mattered most — the oldest, with
three tasks stopped behind it — was discarded by an id sort before the ranking ever saw it. The feature was
correct on every queue small enough not to need it.

The cap now runs **after** the ranking: the read is bounded by `Ceiling` (5000, an order of magnitude above
any real queue and far above what is shown), the ranking is over all of it, and `Take(limit)` then keeps the
200 costliest rather than the 200 newest.

### The badge counted the page, not the queue

`Total` was `Requests.Count + Outbound.Count + Unread` over the **capped** lists, so 210 waiting read as 200.
A badge reading 200 while 210 wait is worse than no badge, because it is believable — and T-18 says plainly
that the number itself is the metric.

`RequestService.OpenSummaryAsync` and `OutboundService.SummaryAsync` now return the count and the earliest
`CreatedAt` over everything waiting. `FounderAttentionDto` carries `RequestsWaiting`, `OutboundWaiting` and
`OldestWaitingSince` as values rather than deriving them from the lists, and `Total` is built from those. The
panel's "N open" comes from the same summary.

### Tests

- `At_two_hundred_and_ten_the_costliest_question_is_still_the_first_one_shown` — the validator's scenario:
  210 open, the first-asked question blocking a task with three children, 209 newer routine ones. The list is
  still 200 long, the critical one is `[0]`, the badge and the panel both say 210. With the cap put back
  before the ranking it fails, naming the wrong request as first.
- `The_summary_the_badge_is_built_from_counts_everything_open` — the summary's count and age, and the age
  moving on when the oldest is answered.

### One test I wrote and removed

I first wrote `The_oldest_wait_is_read_from_everything_waiting_not_from_the_shown_page`, asserting the oldest
request was absent from the capped list while still setting the age. It failed, and it was the test that was
wrong: among equally costly questions the tiebreak is oldest-first, so the oldest is never the one dropped.
Constructing a queue where it *is* dropped needs 200-odd requests that all outrank it, which proves nothing
the first test does not. It is replaced by an assertion at `OpenSummaryAsync`, where the behaviour actually
lives. A test that cannot fail for the reason it claims is worse than no test.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3708 Core, 23 Launch, 65 Cli, 295 Server — all green.
