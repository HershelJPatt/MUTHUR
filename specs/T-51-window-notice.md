# T-51 — the receipts window says when it could not read you

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`GET /receipts?hours=7d` renders the last 24 hours and says nothing about it. After this task it still renders
the last 24 hours — `hours` is a number of hours, and that is settled — but the page says out loud that `7d` is
not one, says which window it is showing instead, and names the three numbers the controls use, so a founder
who typed a label can fix the URL from what they are reading.

This is the third time in this repository that the receipts page has quietly shown something other than what
was asked for. T-52 landed because the window marker followed the requested window rather than the effective
one; T-17's Amendment 5 exists because the page 500'd on a window it invited you to type. This closes the
third instance as what it is.

## The decision, and who made it

T-51 was filed as a vocabulary question: should `?hours=` accept `24h`, `7d`, `30d` — the labels the controls
print? The founder answered it in **request #27** and the answer is **no, and that is not the defect**:

> D: hours stays a number on every surface, and a value that is not one is refused out loud instead of falling
> back.
>
> You have framed this as a vocabulary question, but the defect you found is not the vocabulary. It is the word
> 'silently' in your own sentence: '?hours=7d silently shows 24h'. […]
>
> So the rule I want: '?hours=' takes a number of hours. A value that does not parse to a positive integer is
> not silently replaced — the page says it did not understand, shows what it is showing instead, and the
> controls still work. Nobody gets a page that looks like an answer to a question they did not ask.
>
> One thing to get right while you are in there: the page links to ?hours=24, 168 and 720, so clicking is
> always correct and only hand-editing can go wrong. Make the refusal name the numbers the buttons use, so the
> message teaches the mapping rather than just rejecting: something a person reading it can act on without
> opening the source.

The founder also ruled out accepting the labels on the page alone ("two vocabularies for one concept") and
everywhere ("a parser, its validation, its error code and its tests on three surfaces, for a convenience"),
and ruled out closing T-51 with no code change ("that leaves the silent fallback exactly where it is").

**The escape hatch, and why it is not taken.** The founder's answer ends: *"If it turns out the refusal is more
code than accepting the three labels on the page alone, come back and say so and I will take A."* It is not.
The refusal is one computed property on the page, one parameter and one `@if` block on the panel, and one CSS
class. Accepting the labels would need a parser and its own tests. The hatch stays shut; do not reopen it.

## Context

Three files carry `hours`, and **only the first of them is in scope**:

- **`src/Muthur.Server/Components/Pages/Receipts.razor`** — binds `[SupplyParameterFromQuery(Name = "hours")]`
  as a **string** (T-17 Amendment 5 made it a string because the framework throws on an `int?` it cannot parse,
  before any code of ours runs) and hands the panel an `int?`. A value it cannot read becomes `null`, which the
  panel reads as the absent window: 24 hours. That silence is the defect.
- `src/Muthur.Server/Api/SystemEndpoints.cs:33` — `GET /api/v1/receipts` takes `int? hours` and the framework
  answers a clean **400** for `7d`. Already refused out loud. **Not touched.**
- `src/Muthur.Cli/Commands/SystemCommands.cs:26` — `--hours` is `Option<int?>` and System.CommandLine refuses
  `7d` with its own error before we see it. Already refused out loud. **Not touched.**

What the panel already knows, from T-52, which landed:

```csharp
private int Window => Hours ?? Windows[0].Hours;                       // ReceiptsPanel.razor:131
private int Effective => _report.At == default ? Window : (int)Math.Round((_report.At - _report.Since).TotalHours);
private static readonly (int Hours, string Label)[] Windows = [(24, "24h"), (168, "7d"), (720, "30d")];
```

The controls are anchors to `/receipts?hours=24`, `?hours=168`, `?hours=720` — **numbers, never labels**. Only
the link text is `24h`/`7d`/`30d`. So clicking a control is always correct and the URL a founder copies out of
the page is already a number; only hand-typing from a remembered label goes wrong. That is exactly the case
this notice is for, and it is why the notice has to teach the mapping rather than only reject.

Rules from `CLAUDE.md` that bite here:

- Dashboard components never touch `MuthurDb`; this task touches no service and no data at all.
- Styling uses only classes from `wwwroot/app.css`. The new class goes **in that file**; no inline style, ever.
- Warnings are errors. File-scoped namespaces, XML doc comments only where they say something the name doesn't.
- Tests never sleep. Nothing here needs a clock at all.

## Non-goals

- **Accepting `24h`, `7d`, `30d`, `1w`, `2mo` or any other label, anywhere.** Settled above. `hours` is a
  number of hours on the page, in the API and in the CLI.
- **Changing what the page shows.** `?hours=7d` renders the same 24 hours after this change as before it. The
  only new thing on the page is the notice.
- **Changing the anchors.** They keep linking to `?hours=24`, `?hours=168`, `?hours=720`.
- **The API and the CLI.** No change to `SystemEndpoints.cs`, `SystemCommands.cs`, `Routes`, any contract, or
  `MuthurJsonContext`. A DTO change here would be a sign of having misread the task.
- **Clamping.** A number out of range keeps clamping silently — see the boundary below, which is the single
  most likely thing to get wrong.
- `ReceiptsService`, `Muthur.Core`, `Muthur.Data`, migrations. Untouched.

## Design

### The rule, exactly

`?hours=` takes a number of hours. Three cases, and the middle one is the boundary:

| What arrives | What the page shows | Notice? |
|---|---|---|
| absent, `?hours=`, or whitespace only | the default 24h window | **no** |
| anything `long.TryParse` reads as an integer — `24`, `0`, `-9`, `100000`, `9999999999999` | the window, clamped to 1..720 by `ReceiptsService` exactly as today | **no** |
| anything else — `7d`, `24h`, `abc`, `1e9`, `24,168`, `null` | the default 24h window, exactly as today | **yes** |

**Why a clamped number gets no notice, even though `0` is not a positive integer.** A number out of range *did*
parse, and T-17's design has already decided what happens to it and stated why: *"anything outside is clamped
rather than refused — a founder typing `--hours 100000` wants 'everything', not an error."* T-52 then made the
lit control follow the effective window, so `?hours=100000` lights `30d` and the page already says which window
it is on. That is not a page pretending to answer a question nobody asked; it is the documented contract the
CLI shares, and `DashboardReceiptsTests` pins it in three places
(`An_out_of_range_window_lights_the_control_it_was_clamped_to`,
`A_window_no_control_names_lights_nothing_rather_than_the_nearest`,
`A_window_too_large_for_an_int_is_clamped_to_thirty_days_rather_than_refused`). **Do not add a notice to the
clamped case and do not change those tests.** The silence being fixed is the fallback, not the clamp.

**Why a blank `hours` gets no notice.** `?hours=` is nobody asking anything. A notice there would fire on a URL
a founder can produce by clearing the value, and would say "I could not read your empty window", which is
noise. Blank and whitespace-only read as absent.

### The page — `src/Muthur.Server/Components/Pages/Receipts.razor`

Keep `Hours` and `Window` exactly as they are. Add one property and pass it down. The whole file becomes:

```razor
@page "/receipts"
@using System.Globalization

<PageTitle>MUTHUR · Receipts</PageTitle>

<div class="page">
    <ReceiptsPanel Hours="Window" Unreadable="Unreadable" />
</div>

@code {
    /// <summary>
    /// The window the panel's links navigate to, so it can be linked to and read back on a fresh load. A string
    /// and not an <c>int?</c>: the framework throws before any code of ours runs on a query value it cannot parse,
    /// and this is a URL the page invites a hand to edit — the controls it prints are labelled "24h" and "7d".
    /// </summary>
    [SupplyParameterFromQuery(Name = "hours")] public string? Hours { get; set; }

    // Saturated to int rather than clamped to a window: the bounds are ReceiptsService's, where they are already
    // tested, so 9999999999999 arrives there as int.MaxValue and is clamped to 720 like any other number too big.
    // A value that is not a number at all is the same class of mistake, and becomes the absent window: 24 hours.
    private int? Window => long.TryParse(Hours, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
        ? (int)Math.Clamp(hours, int.MinValue, int.MaxValue)
        : null;

    /// <summary>
    /// What was typed, when it was a window the page could not read — so the panel can say so rather than answer
    /// a question nobody asked. A number out of range is not this: it parsed, and the service clamps it. An
    /// absent or blank <c>hours</c> is not this either: nobody asked anything, and a notice there is noise.
    /// </summary>
    private string? Unreadable => Window is null && !string.IsNullOrWhiteSpace(Hours) ? Hours : null;
}
```

### The panel — `src/Muthur.Server/Components/Panels/ReceiptsPanel.razor`

**Markup.** The notice goes immediately after the `btn-row` div and **outside it**, as its sibling:

```razor
        <div class="btn-row">
            @* A link, not a script: the window already lives in the query string, so the control is the URL it
               leads to. That keeps middle-click and copy-link working, and keeps the page followable by anything
               that only fetches markup. *@
            @foreach (var (hours, label) in Windows)
            {
                <a class="btn @(Effective == hours ? "btn-on" : "")" href="/receipts?hours=@hours">@label</a>
            }
        </div>
        @if (Unreadable is { } typed)
        {
            <div class="notice">
                <b>hours=@Clip(typed)</b> is not a number of hours, so this is the last @Windows[0].Label.
                The windows are @Vocabulary.
            </div>
        }
```

Outside the row, not inside it, for two reasons: the controls row is a row of controls, and
`DashboardReceiptsTests.WindowRow` isolates it with a non-greedy `<div class="btn-row">.*?</div>` so that "no
`<button>` here" means in the selector. A div nested inside that row would end the match at the wrong `</div>`
and quietly weaken every assertion that uses it.

**Code.** `Hours`, `Window` and `Effective` are unchanged. Add:

```csharp
    /// <summary>
    /// What the page was asked for when it could not be read as a number of hours, or null. Held here rather
    /// than inferred, because the panel only ever sees the window that survived parsing and cannot tell a
    /// fallback from a founder who asked for twenty-four hours.
    /// </summary>
    [Parameter] public string? Unreadable { get; set; }

    /// <summary>
    /// The three windows spelled the way the URL spells them, so a founder who typed a label reads the number
    /// that means it rather than having to find it. Built from <see cref="Windows"/>, so it cannot drift from
    /// the controls it is describing.
    /// </summary>
    private static readonly string Vocabulary = string.Join(", ",
        Windows.Select(w => $"hours={w.Hours.ToString(CultureInfo.InvariantCulture)} ({w.Label})"));

    /// <summary>
    /// What was typed, echoed back short. A query string can carry any number of characters and a one-line
    /// notice is not the place to render them all; Razor encodes the value, so what is cut here is noise
    /// rather than risk.
    /// </summary>
    private static string Clip(string typed) => typed.Length <= 40 ? typed : typed[..40] + "…";
```

`Vocabulary` is a `static readonly` field and must be declared **after** `Windows` in the file, since static
field initialisers run in declaration order. `@using System.Globalization` is already at the top of the file.

**The rendered sentence**, for `?hours=7d`:

> **hours=7d** is not a number of hours, so this is the last 24h. The windows are hours=24 (24h), hours=168
> (7d), hours=720 (30d).

It names what was typed, says what is being shown instead, and teaches the label→number mapping — which is the
founder's "something a person reading it can act on without opening the source". The controls below are
untouched and still work.

`@Windows[0].Label` rather than a literal "24h": the unreadable case always falls back to the default window,
which is `Windows[0]`, so the sentence follows the controls if the windows ever change.

### The stylesheet — `src/Muthur.Server/wwwroot/app.css`

One new class, in the **receipts** section beside `.section-note` (line 126), using existing tokens only:

```css
.notice { font: 11px var(--mono); color: var(--amber); background: var(--amber-bg); border: 1px solid rgba(226, 166, 68, .4); border-radius: 6px; padding: 8px 10px; margin: 6px 4px 0; }
```

Amber and not red: the page did not fail, it did not understand. That is the register `.pill-blocked`,
`.tag-open` and `.check-warn` already use, and `rgba(226, 166, 68, .4)` is the border those three use verbatim.

## Units of work

One unit. The page, the panel, the stylesheet and the tests are four small edits to one behaviour, and
splitting them across worktrees would cost more than it buys.

### Unit A — the notice
- **Files, all modified, none created:**
  - `src/Muthur.Server/Components/Pages/Receipts.razor`
  - `src/Muthur.Server/Components/Panels/ReceiptsPanel.razor`
  - `src/Muthur.Server/wwwroot/app.css`
  - `tests/Muthur.Server.Tests/DashboardReceiptsTests.cs`
- **Does:** everything in **Design**, and nothing else.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean — 0 warnings, 0 errors, no test skipped — plus the
  tests below, and every existing test in `DashboardReceiptsTests` still passing **unchanged** except the one
  amendment named in point 6.

#### The tests, in `tests/Muthur.Server.Tests/DashboardReceiptsTests.cs`

`TypedAsync(string hours)` already exists and sends the value through the URL without going via an `int`, which
is what every one of these needs. Add a private helper beside `WindowRow`:

```csharp
    /// <summary>The notice, or null when the page is not showing one.</summary>
    private static string? Notice(string page) =>
        Regex.Match(page, "<div class=\"notice\">.*?</div>", RegexOptions.Singleline) is { Success: true } m
            ? m.Value
            : null;
```

1. **It says what it could not read, and names the numbers.** `?hours=7d` answers 200 and the notice contains
   `hours=7d`, `is not a number of hours`, `the last 24h`, and all three of `hours=24 (24h)`, `hours=168 (7d)`,
   `hours=720 (30d)`. Assert the last three individually, so a message that drops one fails naming it.
2. **The controls still work under it.** On the same `?hours=7d` page, `WindowRow` still holds all three
   anchors with their exact hrefs and no `<button>`, and `btn-on` is on `/receipts?hours=24`. Also assert
   `WindowRow(page)` does **not** contain `class="notice"` — the notice is the row's sibling, not its child,
   and that is what keeps `WindowRow`'s non-greedy match honest.
3. **Every window the page cannot read says so.** A `[Theory]` over `7d`, `24h`, `abc`, `1e9`, `24,168`,
   `null` — the same set T-17 Amendment 5 pinned — asserting a notice is present and echoes the typed value.
4. **A window the page can read never does.** A `[Theory]` over `24`, `168`, `720`, `0`, `-9`, `100000`,
   `9999999999999`, `""` and a single space, plus a separate assertion for `/receipts` with no query at all,
   asserting `Notice(page)` is null. This is the boundary: it is what stops someone "fixing" the clamp into a
   refusal and breaking T-17's stated contract and T-52's three tests with it.
5. **A long value does not become the page.** `?hours=` followed by 200 `x` characters: a notice is present,
   the page does **not** contain all 200 characters, and it does contain the 40-character prefix followed by
   `…`.
6. **Amend `Every_class_the_page_renders_is_defined_in_app_css`** — the only existing test that changes. It
   unions the classes of a full page and an empty one; add a third, `await (await TypedAsync("7d")).Content
   .ReadAsStringAsync()`, so `.notice` is covered by the check that every rendered class exists in `app.css`.
   Assert `Assert.Contains("notice", classes)` beside the existing `Assert.Contains("receipt-cost", classes)`,
   so the page really did render it rather than the union quietly not containing it.

Every other test in the file — `A_window_the_page_cannot_read_renders_rather_than_erroring`,
`The_window_the_page_cannot_read_falls_back_to_twenty_four_hours`,
`An_out_of_range_window_lights_the_control_it_was_clamped_to`,
`A_window_no_control_names_lights_nothing_rather_than_the_nearest`,
`A_window_too_large_for_an_int_is_clamped_to_thirty_days_rather_than_refused`,
`Every_window_is_an_anchor_to_its_own_url_and_the_row_holds_no_button`,
`Money_is_on_the_run_that_reported_it_named_by_harness_and_totalled_nowhere` — **passes unchanged**. If one of
them goes red, the change is wrong; do not edit the test.

## Verification

```
dotnet build
dotnet test
```

0 warnings, 0 errors, every project green.

End to end, against an **installed** build and a **scratch** home — never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t51
$env:MUTHUR_HOME = "$PWD/artifacts/t51-home"; $env:MUTHUR_URL = "http://127.0.0.1:7451"
./artifacts/t51/muthur.exe up
```

Fetched, never browsed — Blazor Server prerenders this page, so the markup `curl` returns already contains
everything below:

- `curl -s 'http://127.0.0.1:7451/receipts?hours=7d'` → 200, and the markup holds a `<div class="notice">`
  saying `hours=7d is not a number of hours, so this is the last 24h. The windows are hours=24 (24h),
  hours=168 (7d), hours=720 (30d).`
- The same page still holds the three window anchors (`/receipts?hours=24`, `?hours=168`, `?hours=720`), with
  `btn-on` on the first, and no `<button>` in the `btn-row`.
- `curl -s 'http://127.0.0.1:7451/receipts'`, `?hours=24`, `?hours=168`, `?hours=0`, `?hours=100000` and
  `?hours=9999999999999` → 200, **no** `class="notice"` anywhere in any of them. The last two still show the
  clamped window with `30d` lit; `?hours=0` still lights nothing.
- `./artifacts/t51/muthur.exe receipts --hours 7d` still fails with System.CommandLine's own error, and
  `curl -s -o /dev/null -w '%{http_code}' 'http://127.0.0.1:7451/api/v1/receipts?hours=7d'` is still **400**.
  Both are unchanged by this task and both are the "refused out loud" the founder asked for on those surfaces.
- `muthur.log` in the scratch home holds no `Error` line and no stack trace for any of the above.

No browser is needed and none should be used.

## Out of scope / follow-ups

- Nothing found. If the implementer finds something, it goes in the ledger as a child of T-51, not in the diff.
