# T-24 — A founder console on the dashboard: do the hub half, hand over the paste-line

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

A `/console` page where the founder does founder-only work by reading labels instead of remembering flags —
register an agent, define a role, set a project's gate, add an outbound target, change a task's priority.
Each control performs the **hub half** itself and, where a terminal is genuinely required, hands over the
exact line to paste.

## This task is marked `attended`, and why that was decided before a line was written

The whole dashboard is interactive: `App.razor` renders `<Routes @rendermode="InteractiveServer" />` and
`Program.cs:26` calls `AddInteractiveServerRenderMode()`. **Every button rides a SignalR circuit.** A fetched
page shows the controls; nothing a conductor-started session can do will click *Register* and see an agent
appear.

Building the controls as plain HTML form POSTs instead would make them curl-drivable, and is rejected: it
fights the architecture every other control in this dashboard already uses (`RequestsPanel`, `OutboundPanel`
and `DoctorPanel` are all `@onclick`), and it would make this page the one that behaves differently for
reasons no reader could infer.

So `muthur task attended T-24` was set **before** building, with the measurement in its reason, rather than
after a validator spends a session discovering it. T-14 and T-5 each cost exactly that before T-46 landed the
rule.

**What a machine still checks** is everything short of the click: that the page exists, that every control and
its labels are in the fetched HTML, that no secret is ever rendered into it, and that the services behind the
controls do what the controls claim. Those are unit-testable and are this spec's acceptance. The human
confirms the clicking.

## Context

### The dashboard is the founder, not a second authority

Panels call services directly with `Caller.Founder` — `RequestsPanel.razor:118`:

```csharp
private Task AnswerAsync(int id, string answer) =>
    ActAsync(id, () => Requests.AnswerAsync(Caller.Founder, id, new AnswerRequest(answer)));
```

That is the pattern to follow exactly. The hub runs on the founder's machine; the dashboard is a second
client of the same services, not a second authority. **Never add an endpoint to do what a service call does**,
and never reach past a service into `MuthurDb`.

### T-24's own sequencing advice has expired

The task says to do this with T-6 and T-23 *"as one piece of work, or at minimum settle the layout before the
first of them ships"*. **T-6 landed** (the agents rail, with `AgentsPanel` and `RolesPanel` in
`<aside class="side side-left">` on `/` and `/needs-you`), and **T-23 is in validation** with another
orchestrator. The layout question is therefore already answered by what shipped, and the remaining choice is
where the console lives — settled below.

### The `--validator` trap is real and must not reach the founder

`KitCommands.RoleDefineCommand` adds `--validator` only when the key does not already end in `-validator`.
So `muthur role define validator --brief-file …` silently creates a **non**-validator role unless the flag is
passed. The task names this as *"a trap the CLI already works around and a founder should never meet"*.

## Non-goals

- **Conductor controls.** T-24 lists them, and they are cut with a reason: **T-23 is in validation right now**
  and owns the conductor's presence on the dashboard. Adding controls to a panel that does not exist on
  `main`, owned by another orchestrator mid-flight, is precisely the collision T-16 exists for. Filed as a
  follow-up to be done once T-23 lands — one panel, one owner.
- Starting a session. The hub cannot spawn a process into a window the founder is looking at; the paste-line
  is the whole of the answer.
- Any change to `AgentsPanel`, `RolesPanel`, `BoardPanel`, the rails, or `/`.
- Any new API endpoint, any change to `Caller`, `RequireFounder`, or the auth model.
- Editing a role's brief text in the browser. Briefs are files; `role define --brief-file` takes a path, and a
  textarea that silently becomes the source of truth for a versioned document is a different task.

## Design

### The page

New `src/Muthur.Server/Components/Pages/Console.razor`, `@page "/console"`, titled `MUTHUR · Console`. A tab
in the nav beside the others, `href="/console"`, exactly as `/receipts` was added by T-17.

One column of `<section class="panel">` blocks, one per control group, in this order: **Agents**, **Roles**,
**Projects**, **Outbound**. Reuse `.panel`, `.panel-head`, `.panel-title`, `.panel-sub`, `.panel-body`,
`.agent`, `.agent-head`, `.agent-name`, `.btn`, `.btn-go`, `.pill`, `.empty`, `.kv`. **Add no CSS** unless a
control genuinely has no existing class, and if one does, add it to `app.css` — never an inline style.

The page does **not** inherit `LivePanel`. These are forms, not feeds; a live re-render while the founder is
typing would wipe the field, which is the mistake `RequestsPanel.razor:58` already documents avoiding.
Re-read only after a successful action.

### Every control has the same shape

```
<the inputs>  [ Do it ]
  → on success: a confirmation line, and the paste-line where one applies
  → on failure: the service's own message, in .error-text, and the inputs keep their values
```

Catch `MuthurException` and render `ex.Message` — the services already say the right thing, and a console
that invents its own wording would drift from what the CLI says about the same refusal.

### Agents — register, and hand over the paste-line

Inputs: **name** (text), **harness** (select: `claude`, `codex`, `generic`), **model** (text),
**tier** (select: `mastermind`, `implementer`, `utility`), **account** (text, optional).

On **Register**, call `Agents.RegisterAsync(Caller.Founder, new RegisterAgentRequest(name, harness, model, tier, account))`.

`RegisterAgentResponse` carries `(AgentDto Agent, string Token)`. On success render:

```
Registered corner (codex/gpt-6-astra, mastermind).

    $env:MUTHUR_AGENT = 'corner'
    $env:MUTHUR_TOKEN = '<the token>'
    claude            # then: /muthur-orchestrate next
```

**The token is shown exactly once**, held in component state, and is gone the moment any other action runs or
the page is left. It is never re-read, never stored, and never rendered again — the same contract the CLI
keeps. Say so in a comment on the field, because the next reader's instinct will be to persist it for
convenience.

The paste-line's harness word is the harness that was registered — `claude` for claude, `codex` for codex —
not a hardcoded `claude`.

### Roles — define, with the trap handled

Inputs: **key** (text), **brief file** (text, a path relative to the repository root), and a checkbox
**gives validation verdicts**.

The checkbox defaults to `true` when the key ends in `-validator` **or** equals `validator`, and updates as
the key is typed. That is the trap, closed: a founder typing `validator` gets a validator role, which is what
they meant, and the box is visible so they can see and override what was inferred.

On **Define**, read the brief file and call
`Roles.DefineAsync(Caller.Founder, new DefineRoleRequest(key, brief, isValidator))`. A path that does not
resolve, or is outside the repository, fails with the service's message — do not invent a new code.

No paste-line: defining a role needs no terminal.

### Projects — set the gate

A row per project from `Projects.ListAsync()`, each with its key, its current required validators as
`pill-role` pills, its land mode, and controls to change them: a **validators** text input (comma-separated,
empty means none) and a **land mode** select (`merge`, `pr`).

On **Save**, call the same service `muthur project set` calls. When the result leaves `requiredValidators`
empty, render the sentence T-5 landed — *"none required — implemented goes straight to validated"* — beside
the row, so the console and `/projects` say the same thing about the same state.

**Ingest sources are not editable here.** The task lists them; they are a comma-separated list of
`scheme:location` strings whose failure mode is a silent non-ingesting source, and they deserve the same
probe-before-save treatment `muthur doctor` gives them. Follow-up, noted below.

### Outbound — add a target, write-only

Inputs: **name**, **channel** (select, from the registered `IOutboundChannel` schemes), **address** (text).

On **Add**, call the service `muthur out targets` calls. **The address is never rendered back** — the row
shows the name, the channel, and nothing else, exactly as `out targets` already behaves. A comment on the
markup says why, because "show the address so the founder can check it" is the obvious wrong idea.

### Tasks — priority, cancel, reopen

A compact control: **task id** (text), **priority** (number), and three buttons — **Set priority**,
**Cancel**, **Reopen**. Cancel takes a reason (text, required by the service). Each calls the corresponding
`TaskService` method with `Caller.Founder`.

No list of tasks here: the board is the list, and duplicating it on the console is how two views of the same
thing start disagreeing.

## Units of work

### Unit A — the page, the nav tab, and agent registration
- **Files:** new `src/Muthur.Server/Components/Pages/Console.razor`;
  `src/Muthur.Server/Components/Layout/MainLayout.razor` (the tab);
  new `tests/Muthur.Server.Tests/DashboardConsoleTests.cs`.
- **Does:** the page, the nav tab, and the Agents section including the paste-line and the show-once token.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and tests from `GET /console`
  that the page renders with the Agents control and its labels; that `href="/console"` appears on `/`,
  `/needs-you`, `/operations`, `/projects` and `/receipts`; and — driving `AgentService` directly, not the
  page — that a registered agent's token appears in the response exactly once and that `AgentService.ListAsync`
  never returns it.

### Unit B — roles and projects
- **Files:** `src/Muthur.Server/Components/Pages/Console.razor`;
  new `tests/Muthur.Server.Tests/DashboardConsoleRolesTests.cs`.
- **Does:** the Roles and Projects sections.
- **Depends on:** Unit A having created the page. **Sequence after A**; do not build in parallel.
- **Acceptance:** build clean, tests green, and tests that the sections render from `GET /console`; that a
  role key of `validator` infers the validator flag (test the inference as a pure function, not through a
  click); and that a project with no required validators renders T-5's sentence.

### Unit C — outbound and tasks
- **Files:** `src/Muthur.Server/Components/Pages/Console.razor`;
  new `tests/Muthur.Server.Tests/DashboardConsoleOutboundTests.cs`.
- **Does:** the Outbound and Tasks sections.
- **Depends on:** Unit A. **Sequence after A**, may run alongside B only if the implementer is told which
  sections of the file each owns — otherwise sequence after B.
- **Acceptance:** build clean, tests green, and a test that an added target's **address never appears** in
  `GET /console`, driven by adding a target through the service and then fetching the page.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then the headless half, against a scratch hub with an installed CLI from this branch — never the
live hub:

```powershell
$html = (Invoke-WebRequest "$env:MUTHUR_URL/console" -UseBasicParsing).Content
$html -match 'Register'          # the agents control
$html -match 'Define'            # roles
$html -match 'Save'              # projects
$html -match 'Add'               # outbound
(Invoke-WebRequest "$env:MUTHUR_URL/" -UseBasicParsing).Content -match 'href="/console"'
```

Then, with a target added through the CLI (`muthur out targets put … --founder`), fetch `/console` again and
confirm **the address does not appear anywhere in the HTML**. That is the one security-shaped check a machine
can make here and it must be made.

### The attended half

**This task is marked `attended` and a human must confirm the clicking.** What to check, in a browser:

1. Fill the Agents control and press **Register**. An agent appears in `muthur agent list`, and the paste-line
   names the agent and harness just registered.
2. Copy the paste-line into a terminal. The session starts and `muthur agent whoami` answers as that agent.
3. Reload `/console`. **The token is gone** and nothing displays it again.
4. Define a role with the key `validator`. `muthur role list` shows it with `isValidator: true`.
5. Set a project's validators to empty and confirm `/projects` shows the ungated pill T-5 landed.
6. Add an outbound target and confirm its address is not shown back anywhere.

Every one of those is a founder action in the ledger, indistinguishable from the CLI having done it —
`muthur log` after the session should read the same as if the commands had been typed.

## Out of scope / follow-ups

- **Conductor controls** — deferred while T-23 is in validation, see Non-goals. One panel, one owner.
- **Ingest sources on the Projects row.** A source that does not ingest fails silently; the console should
  probe before saving, the way `muthur doctor` does, and that is a task with its own verification rather than
  a text box.
- **Editing a brief in the browser.** Briefs are versioned files served by `role brief`; a textarea that
  becomes their source of truth needs a decision about where the file then lives.

## Amendment 1 — the harness select named vendors, and there is a fourth answer (2026-09-19)

Unit A built the Agents control as the spec wrote it and then reported the consequence: a `<select>` of
`claude`, `codex`, `generic` is **the first time anything in `Muthur.Server` names a model vendor**. A `grep`
over that project for those words returned zero matches before the commit. `CLAUDE.md` is explicit —
*"Nothing in Contracts/Core/Data/Server names a model vendor. Harness knowledge lives in `Muthur.Launch` and
`kit/<harness>/`"* — and `Muthur.Launch/Harness.cs:20` says *"The only place vendor CLIs are named."*

**That rule is load-bearing, not decorative.** It is what disqualified option A in T-19 two tasks ago, on the
grounds that making a capability depend on which vendor staffed a tier is vendor knowledge leaking into the
organization chart. Shipping a violation of it in the same session would be incoherent.

The implementer also showed why the obvious fix is worse: sourcing the list from
`Muthur.Launch.Harnesses.All` gives `claude`, `codex`, `codex-oss` — the **worker adapter** list, which has
no `generic`, so a founder could no longer register a generic agent from the console. A functional regression
bought to satisfy a layering rule is not a fix.

### The fourth answer

An `<input list="…">` with a `<datalist>` the hub fills from **data it already holds**: the harnesses named in
the tier catalog (`{MUTHUR_HOME}/harnesses.json`, which `muthur harness tiers` already reads) unioned with the
distinct `Harness` values of registered agents.

- **No vendor is named in `Muthur.Server`.** The strings come from the founder's own configuration and their
  own ledger, which is exactly where the rule wants harness knowledge to live.
- **No regression.** A datalist suggests without constraining, so `generic` — or anything else — still
  registers. `AgentService.RegisterAsync` accepts any non-empty string, and the console now matches the API
  behind it, where a `<select>` was quietly narrower.
- **The founder still does not have to remember spellings**, which is the task's whole reason for existing.
  They get the list without being locked to it.
- On a fresh hub the list may be short or empty. That is honest: it says what this organization actually
  uses, and the field still works as free text.

**Tier stays a `<select>`.** `mastermind` / `implementer` / `utility` are MUTHUR's own vocabulary, not a
vendor's, so no rule objects to naming them.

### The layout decision, made once

Four stacked `flex: 1` panels each get a quarter of the viewport with their own scrollbar, which cramps a
form. The console scrolls as one column instead, via a **new class in `app.css` used by the console's
container** — `.page` itself is untouched, because every other page depends on it. The class is named for
what it does, not for the console, so the next stacked-form page can use it.

### Ratified from Unit A

- The `<dl class="kv">` with a real `<label for>` inside each `dt` — associated as well as styled, reusing an
  existing class rather than inventing one.
- **A test that every class the page renders exists in `app.css`, and that the body contains no `style="`.**
  That turns a house rule from prose into something mechanical, which is worth more than the control it was
  written for.
- A test that a refused registration carries `AgentService`'s own message and `invalid_name` code — which
  makes *"render `ex.Message`, never invent wording"* testable without a click, on a page where I had assumed
  behaviour was unreachable by a machine.
- `ClearOnce()` and the comment on `_token`.

### Noted, not fixed

`AgentService.RegisterAsync` validates the harness against nothing. With a datalist rather than a select, the
console no longer pretends otherwise — it suggests what exists and accepts what the API accepts.

## Amendment 2 — two spec problems Unit B was right to stop for (2026-09-19)

### 1. Unit A's `<select>` assertion enforced its own deviation, page-wide

`DashboardConsoleTests.cs:56` asserted `DoesNotContain("<select")` over the **whole** fetched page. The Design
gives Projects a land-mode select and Outbound a channel select, so that line blocked both Unit B and Unit C.

The assertion's own comment says "a `<select>` **here**" — its intent was the harness and tier fields, which
the four assertions above it already pin as `<input list>` + `<datalist>`. And Amendment 1 said *"Tier stays a
`<select>`"*; Unit A made tier a datalist too, for a good reason I accepted, and then wrote a page-wide
assertion enforcing that deviation against every later unit.

**Unit B's fix is ratified**: scope it to the Agents section rather than delete it, with a comment saying why
land mode is different. A guard that protects a decision should cover the decision, not the file.

### 2. Nothing in the hub reads a brief file, so "the service's message" does not exist

The Design said to *"read the brief file and call `Roles.DefineAsync(…)`"* and let a bad path *"fail with the
service's message"*. There is no such service: `RoleService.DefineAsync` takes brief **text**, and the file
read lives in the CLI (`RoleCommands.cs:26-32`). I wrote that paragraph as though a service existed because
the CLI made it look like one did.

Unit B's answer is ratified with one addition. What it built:

- a private `ReadBriefAsync` resolving the path against each project's `RepoPath` in key order;
- an `IsInside` guard copied from `TaskService.cs:326`. **That instinct was right and is the important part**
  — this control reads the founder's disk from a browser tab, and a path free to climb out with `..` would
  let that tab read anything on the machine. Nothing in the spec asked for it.

**The addition: mirror the CLI's `brief_file_dirty` refusal.** `RoleCommands.cs:29` refuses a brief file with
uncommitted changes, so the hub's stored brief always corresponds to something in git. Without it the console
is the lax path to the same operation, and a founder would get a hub brief no committed file backs — which is
exactly the drift T-20 exists to prevent. Two clients of one operation must not have different safety rules.

That leaves the *file-reading* rule in two places, which is the shape I refused in T-59 ("one deny list, not
two"). It is accepted here only because unifying it means a new service that reads the founder's disk, which
is a real design decision and not a drain-time change. **Filed as a follow-up.**

### 3. The inferred checkbox loses a deliberate override

Unit B implemented *"updates as the key is typed"* literally, and reported the consequence: a founder who
unticks the box and then goes back to fix a typo in the key silently gets it re-ticked. That is a defect —
the control exists so the founder can see **and override** what was inferred, and an override that a later
keystroke revokes is not one. **Take the latch**: once the founder touches the checkbox, inference stops for
that entry.

### Also ratified

- `RequiredValidators` sent as an **empty list, never null**, when the box is cleared — null means "leave the
  gate alone", so a cleared box would do nothing. There is a test for the distinction.
- The test that regexes T-5's sentence out of a fetched `/projects` and asserts `/console` contains that exact
  string, rather than retyping it. Two pages that must agree, proven to agree.
- A test asserting the string `ingest` appears nowhere on `/console`, so nobody adds the field without
  reading why it was cut.
- Not rendering `RepoPath` on the Projects row: the console is a control surface, not a second description.

## Amendment 3 — the security check was written against a command that does not exist

The Verification section tells the validator to add a target with `muthur out targets put … --founder` and
then confirm the address never appears in `/console`. That check is the one security-shaped assertion a
machine can make here, and as written it could not be run: there is no `put` verb, and `targets` is a leaf
that lists the allowlist rather than a group holding subcommands.

Measured against `src/Muthur.Cli/Commands/OutboundCommands.cs:15-28` — `targets` is a `Command` with its own
`SetAction` (a GET of `Routes.OutboundTargets`), and the writer is a **sibling** named `target` added to
`out`, not to `targets`.

The command is:

```
muthur out target <key> --channel file --address <absolute path> --founder
```

`--channel` and `--address` are both `Required = true`; `--founder-approval` is the optional flag that makes
every message to the target need the founder as well.

Nothing about the requirement changes — the address must still appear nowhere in the HTML, and that check is
still mandatory. Only the incantation was wrong. I am recording it rather than quietly fixing the line
because the same wrong form was in front of me each time I read this spec and I did not catch it until I went
to run it; a validator reading the spec cold would have hit exit 1 and had to decide for themselves whether
the product or the spec was broken.

## Amendment 4 — an addition I ordered and never checked, and three deviations ratified

### 1. Amendment 2's `brief_file_dirty` mirror was never built, and I did not notice

Amendment 2 §2 ratified Unit B's brief-file handling "with one addition": mirror `RoleCommands.cs:29`, which
refuses a brief file with uncommitted changes so the hub's stored brief always corresponds to something in git.

It is not on the branch. `ReadBriefAsync` resolves the path, guards it with `IsInside`, and reads the file. The
path guard — the part that stops a browser tab reading anything on the founder's disk — is there and is the
more important half. The dirty refusal is absent. Unit C found this while reading the code it was extending;
I ordered the addition, did not verify it, and wrote the next amendment as though it existed.

It is also harder than the one line Amendment 2 implied, which is worth recording because it is the reason a
future reader should not just add it here:

- `FileProvenance` lives in **`src/Muthur.Cli/Infrastructure/`**. `Muthur.Server` does not reference
  `Muthur.Cli` and must not — the CLI is a client of the server, and that dependency points the wrong way.
- `FileProvenance.IsDirty` shells out to git **directly**, not through `IProcessRunner`. Lifted as-is into the
  server it would be the one piece of the console no test could fake, in a codebase whose rule is that
  anything talking to the outside sits behind an interface.

So the honest state is: **the console is currently the laxer of the two paths to `role define`**, and the
principle Amendment 2 invoked against that — and that Unit C invoked again, correctly, for founder approval on
outbound targets — is not upheld here. That is a real gap, it is filed as its own task rather than folded into
this one, and the human validating this task should know it is open.

### 2. The outbound acceptance check was vacuous, and Unit C repaired it rather than passing it

The Design's check — "an added target's address never appears in `GET /console`" — passes trivially against a
page with no Outbound section at all. Unit C made it fail honestly first: assert the row **is** rendered (key
in `agent-name`, channel in `agent-model`) and only then that the address is absent. Without those two lines
it measured nothing, and it would have gone green the whole time the section did not exist.

This is the same defect as the `-match`/`-cmatch` false pass in this task's own headless harness, found the
same afternoon: an assertion nobody has ever seen fail is not evidence.

### 3. Ratified deviations

- **The fourth Outbound input, the founder-approval checkbox.** Ratified, and it was the right call to make
  rather than ask. `DefineTargetAsync` is an upsert that writes `RequiresFounderApproval` **unconditionally**,
  and `DefineTargetRequest` defaults it to `false` — so a console sending only key/channel/address would strip
  founder approval from an existing target the first time anyone corrected its address, silently, while
  `muthur out target --founder-approval` carries it. That is precisely "two clients of one operation with
  different safety rules", which this spec's own Amendment 2 forbids. The row's pill follows from the same
  flag and names no address.
- **"Key", not "Name".** The service field is `Key`, the CLI argument is `key`, and the refusal says "Target
  keys are 1-48 chars". A label reading "Name" would have the founder reading one word and the error another.
- **`@inject IEnumerable<IOutboundChannel>`.** Ratified. `IOutboundChannel` is declared in
  `src/Muthur.Server/Services/OutboundService.cs` and `OutboundService` takes the identical injection, so the
  page reads a service-layer capability list — not data behind the services, and nowhere near `MuthurDb`. It is
  also strictly better than a literal list: a channel registered on the server appears in the select with no
  edit here, and the set offered is by construction the set `FindChannel` accepts.

### 4. Two more corrections to this spec

- **"Cancel takes a reason (text, required by the service)" is untrue.** `CancelTaskRequest(string? Reason =
  null)`; `CancelAsync` records it and validates nothing, and `muthur task cancel --reason` is optional. The
  console sends what was typed, blank as `null`, and validates nothing ahead of the hub.
- **The attended checklist is in an unrunnable order.** It has the founder define a role (step 4) before any
  project exists, but `ReadBriefAsync` resolves brief paths against `ProjectDto.RepoPath` and a hub with no
  project refuses every brief. **Add a project first** — the checklist should open with
  `muthur project add <key> --repo <path> --founder`.

### 5. A warning for anyone writing a headless check against this dashboard

`BoardPanel` wraps its columns in `<Virtualize>`, which renders nothing on the prerender pass. **No fetched
page ever contains a task title.** A headless assertion that looks for one in `/` will fail against a hub where
the task plainly exists. Unit C's `The_console_is_not_a_second_task_list` proves the task through
`GET /tasks/{id}` instead, which is the shape such a check has to take here.

## Proof — the headless half (2026-09-19)

Rebased onto `main` at `989e18a` (main moved twice during this task), built and tested with `TEMP`/`TMP` on a
private scratch root:

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test
  Muthur.Launch.Tests      28/28
  Muthur.Core.Tests      3736/3736
  Muthur.Cli.Tests        193/193
  Muthur.Server.Tests     500/500     (453 before unit C)
```

Then against a scratch hub installed from this branch — never the live hub — with a project and an outbound
target added through the CLI first, because the Projects and Outbound controls render per row and an empty hub
renders neither:

```
PASS  fixture: target added            PASS  console: Set priority button (tasks)
PASS  fixture: project added           PASS  console: Cancel button (tasks)
PASS  console: Register button         PASS  console: Reopen button (tasks)
PASS  console: Define button           PASS  home links to /console
PASS  console: Save button             PASS  address absent from /console
PASS  console: Add button              PASS  address absent as a full path
T24-HEADLESS: ALL PASS
```

The security check is the one that carries weight, and it is not vacuous here: the same run reports
**`target KEY visible on page = True`**, where the run before unit C reported `False`. The row renders, the key
and channel are on it, and neither `secret-drop-a7f3e9b1` nor the full address appears anywhere in the HTML.
An address-absence assertion against a page with no outbound row measures nothing; this one had a row to be
absent from.

### The harness lied twice before it was trusted

Recorded because the failures are reusable, not because they were interesting:

1. **A false pass.** `$html -match 'Add'` went green against a page with no Add button. PowerShell's `-match`
   is **case-insensitive** and was matching `padding`. Every content assertion now uses `-cmatch` and anchors
   on the button's own markup (`>Add</button>`), which no stylesheet can satisfy by accident.
2. **A false fail.** `Save` failed against working code, because the fixture never created the project the
   control renders per row. The page was right and the harness was asserting against a state it had not built.

Between them those two errors covered both directions — a check that passes when the feature is missing, and a
check that fails when the feature is present — which is the whole space of ways a harness can be useless. The
intermediate run (8 pass, 2 fail, the two being exactly unit C's unmerged sections) is what established it
could tell present from absent before any of this was believed.

### What a human still has to do

The attended checklist stands, with Amendment 4's correction: **add a project first**, or the brief-file step
refuses. Nothing above touches a button; the dashboard is `InteractiveServer` and its controls ride a SignalR
circuit, so what is proven here is that every control renders and that the address never does.
