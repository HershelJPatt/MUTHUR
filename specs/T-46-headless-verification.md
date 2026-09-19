# T-46 — The orchestrate procedure lets an orchestrator write a spec no unattended validator can run

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`kit/core/orchestrate.md` — the procedure every orchestrator reads before writing a spec — gains the rule the
organization keeps re-learning: **a spec whose *Verification* needs a browser, a GUI or a human, on a task not
marked `attended`, is a defect in the spec.** It states how to write a verification that a conductor-started
session can actually run, what genuinely cannot be checked that way, and what to do when a task really does
need eyes. The spec template gains the same requirement at the point of authorship. A test pins that the rule
reaches all three harnesses, because the mechanism that carries it there has never had one.

Six validator sessions were spent in a single day discovering this on T-14, T-13, T-17, T-5, T-26 and T-36.
Every refusal was right; every one of those specs was wrong. The two orchestrator loops now carry the warning
in their `/loop` prompt text, which dies with the session — a rule an organization keeps re-learning belongs in
the procedure.

## Context

### How the procedure reaches a session

`kit install` is the whole distribution mechanism, and it differs per harness:

| Harness | What the orchestrator reads | How `kit/core/orchestrate.md` gets there |
|---|---|---|
| claude | `.claude/skills/muthur-orchestrate/SKILL.md` | `kit/claude/skills/muthur-orchestrate.md` contains the token `{{core:orchestrate.md}}`, expanded at install |
| codex | `.muthur/procedures/orchestrate.md` | `kit/codex/kit.json` copies `../core/orchestrate.md` directly |
| generic | `.muthur/procedures/orchestrate.md` | `kit/generic/kit.json` copies `../core/orchestrate.md` directly |

So editing `kit/core/orchestrate.md` reaches all three. The expansion is
`KitCommands.Expand` (`src/Muthur.Cli/Commands/KitCommands.cs:137`), a single regex replace of
`{{core:([A-Za-z0-9._-]+)}}` against `kit/core/`. It is not recursive.

**`Expand` has no test of any kind.** If that token ever stopped resolving — a renamed file, a changed
pattern, a new harness kit that forgets to include the procedure — the rule would silently never reach a
Claude orchestrator, which is the majority harness here. That is the gap Unit B closes, and it is worth more
than a test that pins prose.

### The template is two files

`kit/core/spec-template.md` and `specs/_TEMPLATE.md` are **byte-for-byte identical today**. The first is the
source; `kit install` writes it to the second (`"from": "../core/spec-template.md"`, `"to":
"specs/_TEMPLATE.md"`, in all three `kit.json` files, mode `replace`). This repository has its own copy checked
in because it is itself a MUTHUR project. **Both must be changed, identically, or the next `kit install` in
this repository produces a diff nobody asked for.**

### The prose already exists, in the wrong place

`specs/T-14-doctor.md:471-492` contains the passage this task generalizes, written after the fact by the
orchestrator who caused the six blocked sessions. It ends: *"A spec that cannot be validated by the sessions
this organization actually starts is a defect in the spec."* The text below is that lesson, rewritten for the
procedure's voice and audience. Read T-14's version before you start, so you can see what is being generalized
and what is being left behind as product-specific.

### The other half of the rule is already written

`kit/briefs/validator.md:47-50` tells the validator what to do when it hits one of these
(`muthur validate blocked`). This task is the half aimed at the orchestrator who caused it. **Do not edit
`kit/briefs/validator.md` or `kit/core/validate.md`** — they are correct and this task does not touch them.

## Non-goals

- Any change to `src/`. `Expand` is not modified; it works, it merely has no test.
- Any product change to make virtualized rows assertable. Explicitly rejected below.
- Enforcement in the hub — MUTHUR does not read specs and this task does not make it start.
- `kit/core/validate.md`, `kit/briefs/validator.md`, and the `/loop` prompt text in anyone's session.

## Design

### 1. `kit/core/orchestrate.md` — the new section

Insert a new `##` section **between step 8 of "The loop" and the `## Rules` heading**, verbatim:

```markdown
## Verification a conductor-started session can run

Validation here is done by sessions a conductor starts: no browser, no GUI, no hands. **A spec whose
*Verification* asks for a click, on a task not marked attended, is a defect in the spec** — and an
orchestrator who writes one has not finished the task. Six validator sessions were spent in a single day
discovering this on T-14, T-13, T-17, T-5, T-26 and T-36. Every one of those refusals was right; every one of
those specs was wrong.

- **Write what a fetch can check.** A Blazor Server dashboard prerenders, so a page fetched with `curl` or
  `Invoke-WebRequest` already contains the panel, the row, the badge and the text. Assert against that HTML:

  ```
  (Invoke-WebRequest "$env:MUTHUR_URL/operations" -UseBasicParsing).Content |
      Select-String -Pattern 'Doctor', 'Re-check', 'check-warn'
  ```

- **Know what no fetch can check.** `<Virtualize>` renders nothing during prerender, so virtualized rows — and
  anything that depends on them — are not assertable from fetched HTML at all. That is settled; do not
  re-derive it, and do not redesign a component to make a two-word badge testable.
- **A task that genuinely needs eyes is marked, not shipped and hoped over.** Run
  `muthur task attended T-n --reason "…"` before you hand it to validation. The reason is the founder's signal
  that a human must look, and it stops the conductor spending a session to find out.
```

### 2. `kit/core/orchestrate.md` — step 3 points at it

Step 3 currently ends `…you haven't finished step 2.` Append one sentence, so the requirement is visible where
the spec is written:

```
Its *Verification* section has a bar of its own: see **Verification a conductor-started session can run**.
```

### 3. `kit/core/orchestrate.md` — one line in `## Rules`

The `## Rules` list is what gets skimmed. Add as the **second** bullet, directly after the
`Never edit product code directly…` line:

```markdown
- A spec's *Verification* must be runnable with no browser, no GUI and no human. One that is not, on a task
  not marked `attended`, is a defect in the spec — you have not finished the task.
```

This restates the section deliberately: the section teaches, the rule enforces. That is the file's existing
shape, not redundancy.

### 4. The spec template, in both copies

In `kit/core/spec-template.md` **and** `specs/_TEMPLATE.md`, under `## Verification`, after the existing line
`Plus anything a validator should exercise end to end (the user-visible behavior, not the unit tests).`, add:

```markdown
Every command here runs with no browser, no GUI and no human — the sessions that validate this are started by
a conductor. If the change can only be seen by eye, mark the task
`muthur task attended <id> --reason "…"` and say so here.
```

The two files must remain byte-for-byte identical afterwards. Verify that; do not assume.

> The founder's task names `kit/core/orchestrate.md` as the place. The template is a deliberate addition by
> the orchestrator: it ships through the same three `kit.json` files to the same repositories, and it is the
> point at which an orchestrator actually writes the section in question. It adds no rule the procedure does
> not already state.

### 5. `tests/Muthur.Cli.Tests/KitInstallTests.cs` — new

Two tests. Neither asserts a sentence of prose beyond one stable marker; the point is the mechanism.

**Locating the kit.** Follow `ProjectManifestTests`:
`ProjectContext.FindFile(AppContext.BaseDirectory)` returns this repository's `muthur.project.json`; its
directory is the repository root, and `kit/` sits beside it. Throw with a clear message if it is not found,
as `ProjectManifestTests.Manifest` does.

**Pointing the CLI at it.** Set `KitCommands.KitVariable` (`MUTHUR_KIT`) to that `kit/` directory in the class
constructor and restore the previous value in `Dispose`. xUnit runs the tests within one class sequentially,
and no other test class in this assembly reads that variable, so this is safe — say so in a comment.

**Invoking install.** Follow `HubIsolationTests.Invoke`: build a `RootCommand`, `KitCommands.AddTo(root)`,
`root.Parse([...])`, `Assert.Empty(parse.Errors)`, `await parse.InvokeAsync()`. Assert the exit code is
`ExitCodes.Ok`.

Use one temp repository per harness, from `Directory.CreateTempSubdirectory("muthur-kit-")`, deleted in
`Dispose`.

- `Every_harness_installs_the_orchestrate_procedure_with_the_headless_rule` — a `[Theory]` over
  `("claude", ".claude/skills/muthur-orchestrate/SKILL.md")`, `("codex", ".muthur/procedures/orchestrate.md")`
  and `("generic", ".muthur/procedures/orchestrate.md")`. Install, then assert the named file exists and
  contains **`is a defect in the spec`**. Also assert the installed `specs/_TEMPLATE.md` contains
  **`no browser, no GUI and no human`**.
- `No_installed_file_is_left_holding_an_unexpanded_include` — install the claude kit (the only one that uses
  the token), enumerate every file written under the temp repository, and assert none contains `{{core:`.
  Name the offending path in the assertion message. This is the regression guard for `Expand`.

Give the class an XML doc comment in this repository's voice, saying what it is for: the include that carries
every core procedure into the Claude kit had no test, so a rule added to `kit/core/` could silently fail to
reach the harness most of this organization runs on.

## Units of work

### Unit A — the rule, the template, and the test
- **Files:** `kit/core/orchestrate.md`, `kit/core/spec-template.md`, `specs/_TEMPLATE.md`,
  new `tests/Muthur.Cli.Tests/KitInstallTests.cs`.
- **Does:** sections 1–5 above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and the two new tests fail
  if the rule is removed from `kit/core/orchestrate.md`. Check that by temporarily deleting the section and
  re-running the new tests; restore it afterwards and re-run.

This is one unit on purpose: the test asserts a marker the same unit writes, so splitting it would leave one
half unable to verify itself.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then confirm by hand that the rule reaches a repository, which is the whole point of the task —
from the task branch, into a scratch directory, never against this repository:

```powershell
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("t46-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:MUTHUR_KIT = "<the task worktree>\kit"
foreach ($h in 'claude','codex','generic') {
    $repo = Join-Path $scratch $h
    New-Item -ItemType Directory -Force $repo | Out-Null
    & "<an installed muthur.exe built from this branch>" kit install --harness $h --repo $repo | Out-Null
}
Get-ChildItem $scratch -Recurse -File -Include SKILL.md,orchestrate.md,_TEMPLATE.md |
    Select-String -Pattern 'is a defect in the spec','no browser, no GUI and no human' |
    Group-Object Path | ForEach-Object { "{0}  {1}" -f $_.Count, $_.Name }
Get-ChildItem $scratch -Recurse -File | Select-String -Pattern '\{\{core:'
Remove-Item -Recurse -Force $scratch
```

Passing looks like: the orchestrate procedure matched in all three repositories, `_TEMPLATE.md` matched in all
three, and the `{{core:` search returning **nothing**.

Per `CLAUDE.md`, build the CLI with `scripts/install.ps1 -Destination ./artifacts/t46` and run that, never a
`dotnet run` against the live hub. `kit install` reaches no hub, but the rule stands.

**No browser is required to validate this task, and none should be used** — which is the rule the task adds.

## Out of scope / follow-ups

- **`Expand` is not recursive and fails hard on a missing file.** A `{{core:…}}` token inside a core procedure
  is left unexpanded; a token naming a file that does not exist throws `FileNotFoundException` out of
  `kit install`. Neither is wrong today and neither is this task's business, but the second deserves a real
  error message rather than a stack trace. File it if it ever bites.
- **Nothing checks that `kit/core/spec-template.md` and `specs/_TEMPLATE.md` stay identical.** This task keeps
  them in sync by hand and the new test checks the installed copy carries the line, which catches the common
  case. A test comparing the two files directly would be stronger.
- **The hub cannot see this rule.** `muthur task implemented` accepts any spec. Whether the organization wants
  a gate there — and what it could possibly check — is a product question, not this one.
