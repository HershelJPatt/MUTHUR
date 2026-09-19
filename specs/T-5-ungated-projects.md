# T-5 — Say it out loud when a project has no validation gate

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a project with no required validators says so — in the response to the command that
created or changed it, and on the dashboard. The gate is presently shown in the UI as a row of validator
pills and is absent in fact when that row is empty, with nothing anywhere saying that `implemented` will
walk straight to `validated` with nobody looking. This bit the founder's own first project on 2026-09-17.

## Context

`ProjectService.AddAsync` and `UpdateAsync` (`src/Muthur.Server/Services/ProjectService.cs`) normalise
`RequiredValidators` and return a `ProjectDto`. An empty list is legitimate — a solo repository with no
validator is a real choice — and nothing in the code path remarks on it.

`LifecycleService` is what makes the consequence real: with no required validators, `implemented` moves a
task to `validated` without a verdict. That behaviour is correct and is **not** changing here.

The CLI is a pure HTTP client: `muthur project add` and `project set` print the API's JSON response
verbatim through `Output.Emit`. So anything the command "says" must come from the response body — there is
no room for the CLI to add prose of its own, and it must not grow any.

### The founder has already settled the one open question

T-5's body says *"Consider refusing `land` on an unvalidated task when `land_mode = pr`."* Asked as founder
request #2; the answer was:

> **Do not refuse: keep land permissive everywhere and let the visibility changes carry the whole task.**

So `land` is out of scope entirely. Do not add a check, a flag, or a warning to it.

### Files and patterns

- `src/Muthur.Contracts/Projects.cs` — `ProjectDto`, a positional record. Every type crossing HTTP is
  registered in `MuthurJsonContext` (`ProjectDto` already is, at lines 20-21); a computed property on an
  existing record needs no new registration.
- `src/Muthur.Server/Services/Mapper.cs:10` — `Project.ToDto()`, one expression.
- `src/Muthur.Server/Components/Pages/Projects.razor` — already branches on
  `p.RequiredValidators.Count == 0` to print "none required" in the Validators row.
- `src/Muthur.Server/wwwroot/app.css:87-93` — the `pill-*` family. `--amber` / `--amber-bg` already exist
  and are what `pill-blocked` uses.
- `tests/Muthur.Server.Tests/DashboardOperationsTests.cs` — how dashboard rendering is tested here.

Codebase rules that apply: warnings are errors; file-scoped namespaces; dashboard components never touch
`MuthurDb` and call the same services the API uses; styling uses only classes from `wwwroot/app.css`, never
inline styles; new behaviour comes with tests.

## Non-goals

- **`land` does not change.** Settled by the founder, above.
- Validators do not become mandatory. An ungated project stays legal and fully usable.
- `LifecycleService`'s auto-validate path does not change.
- No new CLI command, option or output formatting. The CLI stays a passthrough.
- Do not touch `Projects.razor`'s existing inline `style="margin: 8px 0 0"`. It violates the house rule and
  predates this task; fixing it here would bury this change in noise. Leave it and let it be found.

## Design

### The single source of truth

A project is **ungated** when `RequiredValidators` is empty. That list is already on `ProjectDto`, and both
halves of this task read it — the API consumer through a computed property, the dashboard from the list it
already renders. Neither derives the rule from the other, and there is no third place to keep in step.

### Unit A — the hub says it

Add a computed property to `ProjectDto` in `src/Muthur.Contracts/Projects.cs`, after the positional
parameter list:

```csharp
/// <summary>
/// No validator is required, so `implemented` goes straight to `validated` with nobody looking. A
/// legitimate choice for a solo repository, and one the founder should never make by accident.
/// </summary>
public bool Ungated => RequiredValidators.Count == 0;
```

`System.Text.Json`'s source generator serialises public read-only properties on a record, so `"ungated"`
appears in every `ProjectDto` the API returns — `project add`, `project set`, `project show`, `project
list` — with no change to `Mapper.cs`, `ProjectService.cs`, or `MuthurJsonContext`. **Verify this is true
rather than assuming it**: the acceptance below requires seeing the field in real CLI output.

Also record it where the founder's own history is kept: in `ProjectService.AddAsync` and `UpdateAsync`, add
`ungated = project.RequiredValidators.Count == 0` to the anonymous payload already passed to
`m.Record("project.added", ...)` and `m.Record("project.updated", ...)`. Nothing else in those methods
changes.

### Unit B — the dashboard shows it

In `src/Muthur.Server/Components/Pages/Projects.razor`, when `p.RequiredValidators.Count == 0`:

1. In the `agent-head` row, after the existing `land:` pill, add
   `<span class="pill pill-ungated">ungated</span>`.
2. In the Validators row, replace the bare `none required` with text that says the consequence:
   `none required — implemented goes straight to validated`. Keep the existing `panel-sub` class on it.

When the project has validators, both the head row and the Validators row render exactly as they do today.

In `src/Muthur.Server/wwwroot/app.css`, beside the other `pill-*` rules (after line 93), add:

```css
.pill-ungated { color: var(--amber); border-color: rgba(226, 166, 68, .4); background: var(--amber-bg); }
```

Amber, not red: this is a choice the founder is allowed to make, not an error.

## Units of work

The two units are independent and are to be built in parallel, in separate worktrees. They share no file.
Unit B does **not** wait for Unit A's property — it reads `RequiredValidators` directly, which is the same
source of truth and is what that page already does.

### Unit A — the hub says it
- **Files:** modified `src/Muthur.Contracts/Projects.cs`, `src/Muthur.Server/Services/ProjectService.cs`;
  tests in `tests/Muthur.Server.Tests/` (a new file `ProjectGateTests.cs`)
- **Does:** the "Unit A" section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean, `dotnet test` green.
  - New tests: a project created with no validators comes back with `Ungated` true and one created with
    `--validator` comes back false; the `project.added` and `project.updated` ledger events carry `ungated`;
    and adding a validator to an ungated project via `project set` flips it to false in the same response.
  - The serialized JSON really contains `"ungated"`. Assert this on the raw response body, not on the
    deserialized DTO — the point is what an agent or a founder reads on their terminal.

### Unit B — the dashboard shows it
- **Files:** modified `src/Muthur.Server/Components/Pages/Projects.razor`,
  `src/Muthur.Server/wwwroot/app.css`; tests in `tests/Muthur.Server.Tests/` (a new file
  `DashboardProjectsTests.cs`)
- **Does:** the "Unit B" section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean, `dotnet test` green.
  - New tests, in the style of `DashboardOperationsTests.cs`: `/projects` for a project with no validators
    contains `pill-ungated`, the word `ungated`, and the phrase `goes straight to validated`; `/projects`
    for a project with a required validator contains none of those three and still lists the validator.
  - No inline `style` attribute is added, and `pill-ungated` is defined in `app.css`.

## Verification

```
dotnet build
dotnet test
```

End to end, against a scratch hub — never the live one. Install first
(`pwsh ./scripts/install.ps1 -Destination ./artifacts/t5`), then use a scratch `MUTHUR_HOME` and
`MUTHUR_URL`:

```
muthur project add solo --repo <path> --founder      # response contains "ungated": true
muthur project set solo --validator win-validator --founder   # response contains "ungated": false
muthur project add gated --repo <path> --validator win-validator --founder   # "ungated": false
```

(Corrected. This block first said the field would be *absent* once a project had a validator, which
contradicts the Design section: `Ungated` is a non-nullable `bool` on every `ProjectDto`, so a gated
project reports `"ungated": false`. Unit A's worker caught it. Keeping it a plain `bool` rather than a
`bool?` that the `WhenWritingNull` policy would omit is deliberate: a two-valued fact should not be typed
as three-valued to save sixteen bytes, and a reader of the JSON gets an answer either way.)

Then **fetch** `/projects` from the scratch hub — no browser, and see **Amendment 1**, which retracts an
earlier claim that one was needed. The page is a plain `@foreach`, not a `<Virtualize>`, so the prerendered
HTML carries everything:

```powershell
$html = (Invoke-WebRequest "$env:MUTHUR_URL/projects" -UseBasicParsing).Content
[regex]::Matches($html, 'pill-ungated').Count          # 1 - the ungated project, and only it
$html -match 'none required &#8212; implemented goes straight to validated'
```

`solo` carries `<span class="pill pill-ungated">ungated</span>` and the sentence about `implemented` going
straight to `validated`; `gated` carries neither and lists its validator instead. `DashboardProjectsTests`
asserts the same two facts against the same fetched page, so a failure here and a red suite mean the same
thing.

A validator should also confirm the negative: `muthur task land` behaves exactly as before on an ungated
project, because the founder settled that it must.

## Out of scope / follow-ups

- `Projects.razor` carries an inline `style="margin: 8px 0 0"` against the house rule. Left deliberately;
  worth a sweep of the dashboard for inline styles as its own task.
- Whether the founder should be *warned* at the moment a task auto-validates (as opposed to when the
  project is defined) is a different question, and belongs with T-18's Needs You work rather than here.

## Proof (run 2026-09-18, integrated branch, scratch hub on port 7472)

Built by two `muthur worker run` workers in separate worktrees — Unit A on claude, Unit B on codex — then
integrated, rebuilt and re-verified here.

```
dotnet build   0 warnings, 0 errors
dotnet test    19 + 9 + 3692 + 145 passing   (server suite 139 -> 145: +4 Unit A, +2 Unit B)
```

End to end through the installed CLI against a scratch `MUTHUR_HOME`/`MUTHUR_URL`, never the live hub:

```
project add solo   (no validator)          -> ungated = true,  validators = []
project set solo   --validator win-validator -> ungated = false, validators = [win-validator]
project add gated  --validator win-validator -> ungated = false, validators = [win-validator]
project show gated                          -> raw body contains "ungated":false
```

Dashboard on the same scratch hub, with one ungated project and one gated one:

```
/projects: pill-ungated appears exactly once
           "none required — implemented goes straight to validated" present
```

Exactly once across two projects is the assertion that matters: the pill is on `solo` and not on `gated`.

A first attempt at this check was wrong and is recorded rather than quietly redone — it added a validator
to `solo` before reading the page, so both projects were gated and no pill appeared. The page was right;
the check was not.

`land` was not touched, as the founder settled.

## State at handover (2026-09-19)

**The work is complete and green. It is not blocked on anything an implementer can do.**

Branch `task/T-5-ungated-projects`, merged up to `main` as of this note, pushed. Build clean;
`dotnet test` 23 + 3708 + 40 + 234, all passing.

### What is done

Both units, built by `muthur worker run` (Unit A on claude, Unit B on codex) and integrated here:

- `ProjectDto.Ungated` — a computed `bool`, true when `RequiredValidators` is empty — appears in every
  project response (`project add`, `set`, `show`, `list`). `ProjectService` records `ungated` in the
  `project.added` and `project.updated` ledger payloads.
- `/projects` shows an amber `pill-ungated` and replaces the bare "none required" with
  "none required — implemented goes straight to validated".
- Tests: `ProjectGateTests` (4, hub side, asserting on the raw response body rather than the deserialized
  DTO) and `DashboardProjectsTests` (2, rendering).

`land` was **not** touched. The founder settled that in request #2: *"Do not refuse: keep land permissive
everywhere and let the visibility changes carry the whole task."*

### What is not done, and why it is not the work's fault

`conductor-validator` returned **blocked**, twice, with the same reason both times:

> browser tool returned No browser is available when opening scratch /projects. Build clean and all tests
> passed; installed CLI, ledger and server-rendered project page behaved as specified. Cannot verify amber
> pill visually.

Everything checkable without a browser passes. The amber pill on a live `/projects` has never been seen by
anybody — I could not see it either, and said so rather than implying coverage.

### What was tried and did not work

- **Asserting the pill through `GET /projects` in a test.** It does not work and will not: `BoardPanel` and
  the projects page render inside `<Virtualize>`, which emits nothing during a prerender because it sizes
  its window from a JS measurement the test harness never makes. `curl` of `/projects` on a scratch hub
  returns neither the pill nor the task ids. Confirmed directly; do not spend time re-deriving it. The
  markup is covered instead by rendering the component through `HtmlRenderer`.
- **Marking the task attended** so it routes to a human validator. `muthur task attended` exists on `main`
  (T-31, landed) but **not on the running hub**, which is still the `54c455d` build: `POST
  /api/v1/tasks/T-5/attended` returns 404. Upgrading the live hub is a founder action — `install.ps1`
  refuses to replace a running hub without `-RestartRunning`, deliberately — so I did not do it.

### What I would do next

1. Get the live hub reinstalled from current `main` (founder action). That alone makes `muthur task
   attended` available.
2. `muthur task attended T-5 --reason "the amber ungated pill has to be seen on /projects; Virtualize means
   no test can assert it"`.
3. Hand it to a browser-capable validator. The whole remaining verification is: on a scratch hub with one
   project that has no validators and one that has a required validator, `/projects` shows the amber
   `ungated` pill on the first and not the second, plus the consequence sentence.

Do not re-implement anything to make the pill assertable in a test. That path was investigated and is
closed by `<Virtualize>`; the cost would be redesigning how the dashboard renders, for a task about a
two-word pill.

## Amendment 1 — no browser is needed, and the claim that one was is retracted (2026-09-19)

This task was blocked by a validator **three times**, each on the same thing: the verification asked for a
browser and a conductor-started session has none. It was re-submitted unchanged after the first two, then
released. The handover section below tells the next owner:

> **Asserting the pill through `GET /projects` in a test.** It does not work and will not: `BoardPanel` and
> the projects page render inside `<Virtualize>` … `curl` of `/projects` on a scratch hub returns neither the
> pill nor the task ids. **Confirmed directly; do not spend time re-deriving it.**

**That is wrong, and this amendment retracts it.** It is left in place rather than deleted, because a claim
that cost three validator sessions should stay visible next to its correction.

Two measurements, either of which settles it:

1. `src/Muthur.Server/Components/Pages/Projects.razor` contains **no `<Virtualize>`**. It is a plain
   `@foreach (var p in _projects)`. The board at `/` does virtualize — that is where the belief came from —
   but `/projects` never has.
2. Fetched from a scratch hub built from this branch, one ungated project and one gated one:

```
pill-ungated occurrences : 1
Virtualize in markup     : False

nogate: <span class="agent-name">nogate</span> … <span class="pill pill-ungated">ungated</span>
        <dt>Validators</dt><dd><span class="panel-sub">none required — implemented goes straight to validated</span></dd>

gated:  <span class="agent-name">gated</span> … (no ungated pill)
        <dt>Validators</dt><dd><span class="pill pill-role">validator</span></dd>
```

The pill is in the prerendered HTML, exactly once, on exactly the right project, with the consequence
sentence beside it.

`tests/Muthur.Server.Tests/DashboardProjectsTests.cs` — already on this branch — asserts precisely this
through `GetStringAsync("/projects")`, one test for the pill's presence and one for its absence. The branch's
own suite contradicted its handover, and the suite was right.

### What follows

- The `attended` route in "What I would do next" is **dropped**. This task does not need human eyes, so it
  must not take a founder's. Nothing here requires a GUI.
- Nothing is re-implemented to make the pill assertable. It already was. The handover's closing warning — *do
  not redesign the dashboard for a two-word pill* — is good advice that was aimed at a problem that did not
  exist.
- The Verification section above now says **fetch**, not "open". That one word is what three sessions read as
  a browser requirement, and correcting it only in an amendment would leave the next validator reading the
  same instruction. This is T-46's rule applied to the spec that motivated it.
