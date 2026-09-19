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
