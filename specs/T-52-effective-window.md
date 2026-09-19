# T-52 — The Receipts window marker follows the window you are looking at

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the lit control on the Receipts page is the window the page is actually showing. Today the
marker compares the **requested** hours to each control while `ReceiptsService` clamps that number to
1..720, so any out-of-range request renders thirty days of receipts with no control lit at all:

```
?hours=100000        -> 200, since 720h back, nothing lit
?hours=9999999999999 -> 200, since 720h back, nothing lit
?hours=0             -> 200, since one hour back, nothing lit
```

On a page whose whole job is to be read at a glance, the only remaining clue is the `since` line, and that is
a date rather than a window.

## Context

- `src/Muthur.Server/Components/Panels/ReceiptsPanel.razor`:
  - `Windows` (line 120) — `(24, "24h") (168, "7d") (720, "30d")`.
  - `Window` (line 131) — `Hours ?? Windows[0].Hours`, the **requested** value, used for both the read and
    the marker.
  - The control (line 17) — `<a class="btn @(Window == hours ? "btn-on" : "")" href="/receipts?hours=@hours">`.
    T-17's Amendment 4 made these anchors on purpose, so the window is linkable and hand-editable.
- `src/Muthur.Server/Services/ReceiptsService.cs` line 41 —
  `now - TimeSpan.FromHours(Math.Clamp(hours, MinHours, MaxHours))`, with `MinHours = 1`, `MaxHours = 720`.
- `src/Muthur.Contracts/Receipts.cs` — `ReceiptsDto(Since, At, …)`. `At - Since` **is** the clamped window,
  exactly, because `Since` is built by subtracting a whole number of hours from `At`.

Constraints that are not obvious:

- The panel reads through `ReceiptsService` like the API and the CLI; it never touches the database.
- The page does not use `<Virtualize>`, so the controls and their classes are in the prerendered HTML.
- Warnings are errors.

## Non-goals

- **T-51**, whether `?hours=7d` should be accepted. That is a product decision about URL vocabulary and is
  explicitly not this task; `hours` stays a number here.
- Changing the clamp, its bounds, or what `ReceiptsService` returns. This task changes which control is lit
  and nothing else.
- Reporting the clamp to the caller as an error or a redirect. T-17 settled that an out-of-range window is
  clamped rather than refused, and that stands.

## Design

`Window` stops being used for the marker. A second expression is added beside it:

```csharp
/// <summary>The window actually being shown: what the service clamped the request to, read back off the report.</summary>
private int Effective => _report.At == default ? Window : (int)Math.Round((_report.At - _report.Since).TotalHours);
```

and the control becomes `@(Effective == hours ? "btn-on" : "")`.

**Derived from the report rather than re-clamped in the panel.** `Math.Clamp(Window, MinHours, MaxHours)`
would light the same control today and is one line shorter, but it is the panel deciding again what the
service already decided; if the bounds ever move, the page would go on lighting the old answer until someone
noticed. `At - Since` is what the service did, so it cannot drift from it.

`Window` keeps its job: it is what gets **read** (`Receipts.ReadAsync(Window)`) and what
`OnParametersSetAsync` compares, so a new URL still triggers exactly one reload. Only the marker moves.

The `_report.At == default` guard is the pre-read state — `Unread` is an all-default DTO, and before the
first read completes `At - Since` is zero, which would light nothing. Falling back to the requested window
there means the first paint marks what was asked for, which is the best available answer until the report
arrives.

### The behaviour change this accepts, on purpose

Once the marker follows the effective window, clicking `30d` while on `?hours=9999999999999` lights the
control that was already lit and changes nothing on the page. That is correct rather than merely tolerable:
the page *is* showing thirty days, and a control that claimed otherwise was the defect. A founder who wants a
different window clicks a different control, which still works.

## Units of work

### Unit A — the marker
- **Files:** `src/Muthur.Server/Components/Panels/ReceiptsPanel.razor`
- **Does:** `Effective` per Design, and the control using it.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean; Unit B's tests pass.

### Unit B — the tests
- **Files:** `tests/Muthur.Server.Tests/`
- **Does:**
  - `/receipts?hours=100000` and `?hours=9999999999999` light `30d`, and only `30d`.
  - `?hours=0` lights `24h` — the clamp floor is 1 hour, which is inside the 24h control's window… **no**:
    the floor is 1, and 1 is not any control's value, so nothing is lit. Assert that explicitly: a window no
    control names has no control lit, which is honest, and is a different thing from the 720 case where one
    does name it.
  - `?hours=24`, `?hours=168`, `?hours=720` each light their own control and no other — the ordinary path is
    unchanged.
  - `/receipts` with no query lights `24h`.
- **Depends on:** Unit A.
- **Acceptance:** each test fails with Unit A reverted.

## Verification

Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor, and the Receipts page does not use `<Virtualize>`, so the controls and their classes are in the
prerendered HTML. There is no live behaviour in this change: it is which class one anchor carries. Nothing
here can only be seen by eye, so it is not marked `attended`.

```
dotnet build
dotnet test
```

And against a running scratch hub — never the live one:

```
(Invoke-WebRequest "$env:MUTHUR_URL/receipts?hours=100000" -UseBasicParsing).Content |
    Select-String -Pattern 'btn-on'
```

Passing is 0 warnings, 0 errors, every test green, and exactly one `btn-on` in that page, on the `30d` anchor.

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 77 Cli, 353 Server — all green,
including `Every_window_is_an_anchor_to_its_own_url_and_the_row_holds_no_button`, which is what says the
ordinary in-range path did not move.

Load-bearing checked by a targeted revert — the control back to `Window == hours`, everything else including
`Effective` left in place. The three out-of-range rows fail and nothing else does:

```
An_out_of_range_window_lights_the_control_it_was_clamped_to(requested: "100000",       lit: 720)  FAIL
An_out_of_range_window_lights_the_control_it_was_clamped_to(requested: "9999999999999", lit: 720)  FAIL
An_out_of_range_window_lights_the_control_it_was_clamped_to(requested: "721",           lit: 720)  FAIL
```

### One correction to the spec, made while writing the tests

The Units of work section first said `?hours=0` "lights 24h", then corrected itself mid-sentence to "nothing
is lit". The second reading is right and is what the test asserts: the floor is one hour, no control names
one hour, so nothing is lit. That is honest, and it is a *different* case from the ceiling, where a control
does name the window the page ended up on. Lighting the nearest control instead would be the page claiming a
window it is not showing, which is the defect this task exists to remove, inverted.
