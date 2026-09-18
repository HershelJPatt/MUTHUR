# T-26 — The Needs You badge and the Needs You page count the same things

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the badge in the top bar and the `/needs-you` page are answering the same question from the
same code, so the badge can never again point at a page that says nothing is waiting. Everything the badge
counts appears on the page, grouped by what it is and saying how long it has waited, and the outbound
messages the founder must approve are reachable there rather than only on `/operations`.

## What is actually wrong, verified against the code

The task body says `NeedsYou.razor` is `<RequestsPanel />` and nothing else. **That is out of date** — it is
now `AgentsPanel` / `RequestsPanel` / `CommsPanel`, and two of the three counted things do render:

| The badge counts | Rendered on `/needs-you` today? |
|---|---|
| Open founder requests (`RequestService.ListAsync(openOnly: true)`) | Yes — `RequestsPanel` |
| Unread messages to the founder (`MessageService.UnreadForFounderAsync()`) | Yes — `CommsPanel`, which also marks them read |
| Outbound in `awaiting_founder` (`OutboundService.ListAsync("awaiting_founder", 500)`) | **No** — only on `/operations` |

So the defect is narrower than filed and entirely real: it is the third row. It is exactly what was seen
live on 2026-09-18 — the badge read **1** while the page read *"nothing is waiting on you"*, and the 1 was
`O-2`, a flagged message sitting in `awaiting_founder` on a different page.

The deeper problem is that nothing makes the two agree. `NeedsYouBadge.razor` sums three numbers it fetches
itself; the page renders whatever panels happen to be placed on it. Nobody changing one is told about the
other. This task fixes the miscount **and** removes the way it happened.

## Context

- `src/Muthur.Server/Components/Layout/NeedsYouBadge.razor` — the three-term sum.
- `src/Muthur.Server/Components/Pages/NeedsYou.razor` — the page, a `.dashboard` grid.
- `src/Muthur.Server/Components/Panels/OutboundPanel.razor` — the gate's panel. `DecideAsync` →
  `OutboundService.FounderApproveAsync(Caller.Founder, id, approve, note)` is the Approve/Decline path, and
  `StatusClass` maps a status to its pill. This task **reuses** it; it does not write a second one.
- `src/Muthur.Server/Components/Panels/RequestsPanel.razor` — the panel to match for markup and voice
  (`.request`, `.request-head`, `.tag tag-open`, `.empty`).
- `src/Muthur.Contracts/Outbound.cs` — `OutboundDto`, including `CreatedAt` and `Flags`.
- `src/Muthur.Server/Services/RequestService.cs` — `ListAsync(bool openOnly, int limit = 200, ct)`.
- `src/Muthur.Server/Components/Shared/Format.cs` — `Format.Age(then, now)` is the how-long-ago helper.
- `tests/Muthur.Server.Tests/DashboardNeedsYouTests.cs` — the existing tests for this page.
- `src/Muthur.Server/wwwroot/app.css` — `.dashboard` is a three-column grid; `.side` is the vertical
  panel stack used by the asides.

Constraints that are not obvious:

- Dashboard components never touch `MuthurDb`; they call the same services the API uses.
- Live panels inherit `LivePanel`. Styling uses only classes from `wwwroot/app.css`.
- Time comes from the injected `TimeProvider`.
- `CommsPanel` marks founder messages read when it renders, which is why the unread term drops to zero once
  the founder looks at the page. That is existing, intended behaviour and this task does not change it.
- Warnings are errors.

## Non-goals

- **T-18 (Needs You at two hundred).** This task groups by kind and shows how long each item has waited,
  because that is what makes the page readable at the size it is now. Ranking by what a wait is *costing*,
  answering several of a kind in one pass, and the count in the top bar at two hundred are T-18's, and this
  task must not start them.
- **T-24 (founder console).** No new controls beyond the Approve/Decline that already exist.
- Changing `/operations`. It keeps its full outbound view; the gate is its page and a comms on-call works
  there.
- Changing what any of the three terms means, or how outbound approval works.
- Anything that answers, defers or expires a founder item automatically.

## Design

### One read model, used by both

New `src/Muthur.Server/Services/FounderAttention.cs`:

```csharp
/// <summary>Everything waiting on the founder, read once so the badge and the page cannot disagree.</summary>
/// <param name="Unread">Messages addressed to the founder that they have not seen.</param>
public sealed record FounderAttentionDto(
    IReadOnlyList<FounderRequestDto> Requests,
    IReadOnlyList<OutboundDto> Outbound,
    int Unread)
{
    public int Total => Requests.Count + Outbound.Count + Unread;
}

public sealed class FounderAttention(RequestService requests, OutboundService outbound, MessageService messages)
{
    public const string AwaitingFounder = "awaiting_founder";

    public async Task<FounderAttentionDto> ReadAsync(CancellationToken ct = default) => new(
        await requests.ListAsync(openOnly: true, ct: ct),
        await outbound.ListAsync(AwaitingFounder, 500, ct),
        await messages.UnreadForFounderAsync(ct));
}
```

Registered in `src/Muthur.Server/Infrastructure/Startup.cs`, in `AddMuthur`, as a singleton beside the
other services.

`NeedsYouBadge.razor` becomes `_count = (await Attention.ReadAsync()).Total;` and injects `FounderAttention`
instead of the three services. Its `IsRelevant` stays as it is.

This is the point of the task: **the badge's number now comes from the same read the page renders.** A
future term added to `FounderAttentionDto` shows up in both or neither.

### The outbound half, on the page

`OutboundPanel.razor` gains one parameter rather than a second implementation:

```csharp
/// <summary>Show only what the founder must decide. The gate's own page shows everything.</summary>
[Parameter] public bool AwaitingOnly { get; set; }
```

When `AwaitingOnly` is true:

- `LoadAsync` lists only `awaiting_founder` (it already knows that status as `AwaitingFounder`).
- The head reads title `Needs you · outbound`, sub `@_awaiting waiting`.
- `.empty` reads `nothing is waiting to be sent`.
- Each row additionally shows how long it has waited — ` · waiting @Format.Age(message.CreatedAt, _now)` in
  the existing `.request-head`, using the `Clock` the panel already injects.

Everything else — the flags, the masked excerpts, the body, Approve/Decline, `DecideAsync` — is unchanged
and shared. When `AwaitingOnly` is false the panel behaves exactly as it does today; assert that by leaving
`/operations` untouched and letting its existing tests pass unchanged.

### The page

`NeedsYou.razor`'s centre column stacks the two things that need a decision, requests first:

```razor
<div class="stack">
    <RequestsPanel />
    <OutboundPanel AwaitingOnly="true" />
</div>
```

`AgentsPanel` and `CommsPanel` stay where they are. `CommsPanel` is the third term — the unread founder
messages — and it is already there.

Add to `src/Muthur.Server/wwwroot/app.css`, beside `.side`:

```css
.stack { display: flex; flex-direction: column; gap: 14px; min-height: 0; }
```

`.side` already has these rules, but it is named for the asides and carries `side-left` / `side-right`
alongside it; a centre column borrowing it would read as a mistake to the next person. One line is cheaper
than that confusion.

## Units of work

### Unit A — the whole task
- **Files:** new `src/Muthur.Server/Services/FounderAttention.cs`; modified
  `src/Muthur.Server/Infrastructure/Startup.cs`, `Components/Layout/NeedsYouBadge.razor`,
  `Components/Panels/OutboundPanel.razor`, `Components/Pages/NeedsYou.razor`, `wwwroot/app.css`;
  `tests/Muthur.Server.Tests/DashboardNeedsYouTests.cs`.
- **Does:** everything above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus these tests:
  1. **The invariant, and the bug that started this.** A hub with one outbound message in `awaiting_founder`
     and nothing else: `/needs-you` shows that message and does **not** show "nothing is waiting on you" as
     the page's whole answer, and `FounderAttention.ReadAsync().Total` is 1. Before this task the page
     showed nothing while the badge read 1 — write the test so it fails on the old code.
  2. An open founder request and an awaiting outbound message together: both render, requests first, and
     `Total` is 2.
  3. Approve from `/needs-you` reaches `OutboundService.FounderApproveAsync` and the item leaves the page —
     drive it the way the existing Operations tests drive Approve/Decline if they do; if they assert only on
     rendered HTML, assert that the Approve control is present with the message's id and say so in the report.
  4. An empty hub: `Total` is 0 and the page still renders with its empty states.
  5. `/operations` is unchanged — its existing tests pass untouched, and a `OutboundPanel` with
     `AwaitingOnly` false still lists a message that is not awaiting the founder.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub. No browser is required —
the dashboard is Blazor Server and prerenders, so `GET` of each page carries what it shows:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t26
$env:MUTHUR_HOME = "$PWD/artifacts/t26-home"; $env:MUTHUR_URL = "http://127.0.0.1:7438"
./artifacts/t26/muthur.exe up
```

Then, with a `file` outbound target that requires founder approval, and a message drafted and peer-approved
into `awaiting_founder`:

- `GET /needs-you` contains the message id and its Approve control, and the top bar's badge number equals
  the number of items the page shows.
- Reproduce the original defect's shape: with **only** that outbound message waiting and no open requests,
  the page must not be empty. That is the bug, and it is the one to see fail on `main` and pass here.
- `GET /operations` still shows the full gate, including messages that are not awaiting the founder.
- Answer the outbound item; the badge number and the page both drop by one.

## Out of scope / follow-ups

- **T-18** is the same page at two hundred items: ranking by what a wait costs, answering several at once,
  and the top-bar number at that scale. This task deliberately stops at grouping and waiting times.
- `CommsPanel` marks founder messages read on render, so merely opening `/needs-you` clears the unread term.
  That is defensible for a thread the founder is looking at, and surprising next to items that stay until
  they are answered. Worth a decision in T-18, not a change here.
- `FounderAttention.ReadAsync` issues three reads on every badge refresh, and the badge refreshes on every
  `request.`, `message.` and `outbound.` event. Fine at present scale; if the top bar ever feels slow, this
  is the place to look.
