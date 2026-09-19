# T-68 — The delegation procedure says where a spawned worktree actually comes from

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

The kit stops relying on implementers to discover, one at a time, that the worktree they were handed is not
on the branch their orchestrator named. `kit/core/orchestrate.md` states the fact — a natively-spawned
worktree is cut from the repository's HEAD, not from the orchestrator's — and requires every delegation
prompt to carry the base branch, the commit sha of the frozen spec on it, and the default branch.
`kit/claude/skills/muthur-orchestrate.md` stops asserting the opposite. `kit/core/implementer.md` says why
its base check exists, so nobody tidies it away as belt-and-braces. A test pins all of it to every harness.

Thirteen occurrences in a single day, from two orchestrators, are documented in T-68: nine consecutive units
from one, four more on T-59 and T-60 from another, each implementer reporting in its own words that its
worktree was cut from main and the spec was not there. Every one recovered, because `implementer.md` tells an
implementer to verify its base rather than trust the brief. The cost is paid silently, thirteen times over,
by sessions re-deriving the same fact — and the mitigation working is exactly why nobody has written it down.

## Context

### Two delegation paths, and only one of them is ours

- `muthur worker run` is correct and is **not touched by this task**: `src/Muthur.Cli/Commands/WorkerCommands.cs:170`
  resolves the base as `o.Base ?? git branch --show-current ?? "HEAD"`, so a worker starts where its
  orchestrator stands.
- The other path is the harness's own worktree isolation, which a Claude orchestrator uses when it delegates
  natively (`isolation: worktree` in `kit/claude/agents/muthur-implementer.md`). It cuts from the
  repository's HEAD — the shared main checkout — which sits on whatever branch anybody last used, usually the
  default branch. MUTHUR does not own that tooling and cannot fix it. The kit is the whole of the fix.

### How a kit file reaches a session

`kit install` is the distribution mechanism and it differs per harness:

| File changed | claude | codex / generic |
|---|---|---|
| `kit/core/orchestrate.md` | `.claude/skills/muthur-orchestrate/SKILL.md`, through `{{core:orchestrate.md}}` | `.muthur/procedures/orchestrate.md`, copied |
| `kit/core/implementer.md` | `.claude/agents/muthur-implementer.md` **and** `.claude/agents/muthur-specialist.md`, both through `{{core:implementer.md}}` | `.muthur/procedures/implementer.md`, copied |
| `kit/claude/skills/muthur-orchestrate.md` | the same `SKILL.md` | not installed |

The expansion is `KitCommands.Expand` (`src/Muthur.Cli/Commands/KitCommands.cs:137`), a single non-recursive
regex replace of `{{core:([A-Za-z0-9._-]+)}}` against `kit/core/`.

### The test file already exists and has the shape to follow

`tests/Muthur.Cli.Tests/KitInstallTests.cs` installs this repository's own kit into throwaway repositories
and reads back what a session would be given. `Every_harness_installs_the_orchestrate_procedure_with_the_headless_rule`
is the pattern: a `[Theory]` over `(harness, path)`, `Invoke("kit", "install", …)`, then one assertion per
marker phrase with a message naming which half went missing. Follow it exactly — the helpers
(`NewRepository`, `Invoke`, the `MUTHUR_KIT` handling in the constructor) are already there and are not
modified.

### The two halves of "where am I" do not contradict each other

`kit/core/implementer.md` step 1 requires the base to be named as a **branch**, not a commit, because
`git rev-parse <sha>` never moves and an amendment to the spec would be invisible. This task adds the sha
**alongside** the branch, not instead of it: the branch is the base and stays the base; the sha is what that
branch pointed at when the orchestrator dispatched, and is the value that lets an implementer tell, before it
writes a line, that it woke up somewhere else. Nothing in step 1's resolution procedure changes.

## Non-goals

- **Any change to `src/`.** `muthur worker run` is already correct. `Expand` works.
- **Any attempt to fix the harness's worktree isolation.** It is not MUTHUR's tooling.
- **Removing or weakening the implementer's base check**, or touching step 1's resolution cases. It is the
  thing that has kept this harmless thirteen times; this task explains it and leaves it alone.
- `kit/core/specialist.md`, `kit/core/validate.md`, `kit/briefs/*`, `kit/core/spec-template.md` and
  `specs/_TEMPLATE.md`. The specialist inherits the implementer contract by include and needs no edit of its
  own.
- Whether `muthur task spec` should refuse a spec path that is not on the branch the caller names. See
  *Out of scope / follow-ups*.

## Design

Five edits. Every block below is verbatim: copy it, do not paraphrase it. The test in edit 5 pins phrases
from edits 1–4, so a reworded sentence is a failing build.

### 1. `kit/core/orchestrate.md` — step 4 says where the worktree comes from

Step 4 currently ends with this paragraph (the last three lines of the step, immediately before step 5
`**Review like it's going to production…**`):

```
   Check every branch a worker names, and verify your own base before you dispatch: worktree tooling has
   repeatedly handed implementers a stale base here. Tell them to check `git log` rather than trusting what
   you said the base was.
```

Replace those three lines with, keeping the three-space indent that holds the text inside the numbered item:

```markdown
   Check every branch a worker names, and verify your own base before you dispatch. Then expect the worktree
   your implementer gets to be somewhere else entirely: a natively-spawned worktree is **cut from the
   repository's HEAD, not from yours**, so it comes up on whatever branch the shared main checkout is
   standing on — usually the project's default branch — whatever branch you named. `muthur worker run`
   resolves the base from where you are standing and gets this right; a harness's own worktree isolation does
   not, and it is not MUTHUR's tooling to fix. Thirteen units in a single day, across two orchestrators,
   arrived this way. Every one was caught by the implementer. None was caught by the orchestrator that
   dispatched it.

   So every delegation prompt must **carry the branch name and the commit sha** of the frozen spec: the
   branch because that is the base, and an implementer reading the spec from a branch sees an amendment while
   one reading it from a sha never can; the sha because it is the only thing that tells the implementer,
   before it writes a line, that the worktree it woke up in is not the one you meant. Name the project's
   default branch as well — the implementer's contract needs it to tell an inherited lineage from work of its
   own. And say in the prompt that the worktree may have arrived somewhere else, and that checking is the
   first thing it does.
```

### 2. `kit/core/orchestrate.md` — one line in `## Rules`

The `## Rules` list is what gets skimmed, and this file's shape is that the section teaches and the rule
enforces. Add as the **third** bullet, directly after the
`- A spec's *Verification* must be runnable with no browser…` bullet and its continuation line:

```markdown
- Every delegation prompt names the base branch, the commit sha of the spec on it, and the default branch.
  A worktree spawned by the harness does not arrive where you told it to.
```

### 3. `kit/claude/skills/muthur-orchestrate.md` — stop asserting the opposite

The first bullet of `## In Claude Code` currently reads:

```
- **Delegation** uses the Agent tool: `muthur-implementer` (Opus, frozen spec) or `muthur-specialist`
  (your own tier, for risky units). Each runs in its own git worktree, created from your current HEAD — so
  create and check out `task/T-n-<slug>` and commit the spec **before** you spawn anyone. Launch independent
  units in a single message so they run in parallel.
```

`created from your current HEAD` is false, and it is the sentence that makes a Claude orchestrator trust the
base it named. Replace the whole bullet with:

```markdown
- **Delegation** uses the Agent tool: `muthur-implementer` (Opus, frozen spec) or `muthur-specialist`
  (your own tier, for risky units). Each runs in its own git worktree — cut from the repository's HEAD, which
  is the shared main checkout's branch, **not the branch you are standing on**. So create and check out
  `task/T-n-<slug>` and commit the spec **before** you spawn anyone, and then assume the subagent woke up on
  the default branch without it: give it the base branch by name, the commit sha that branch pointed at when
  you dispatched, and the project's default branch, and tell it to check where it is before it writes
  anything. Launch independent units in a single message so they run in parallel.
```

### 4. `kit/core/implementer.md` — why the base check exists

Step 1 ends with these two sentences:

```
   Never begin work against a tree whose spec you could not verify. A spec read from the wrong base is the
   wrong spec, and the work will look correct and be wrong.
```

Add one paragraph **after** them, still inside step 1 (same three-space indent), so the procedure comes first
and the reason closes it:

```markdown
   **This check is not belt-and-braces, and it is not yours to tidy away.** The tooling that cuts your
   worktree cuts it from the repository's HEAD — the shared main checkout's branch — and not from the branch
   your orchestrator named, so arriving somewhere else is the ordinary case rather than the rare one. In a
   single day, thirteen units across two orchestrators came up on the default branch: no spec, and none of
   the units already integrated. Not one of them was built on the wrong lineage, and the only reason is that
   every one of those implementers ran this check before writing anything. It is the whole of what stands
   between a misplaced worktree and a unit written against a spec that was never there.
```

### 5. `kit/claude/agents/muthur-implementer.md` — the description asks for the sha

The `description:` line in the front matter currently ends:

```
Give it the spec path, the unit it owns, the base branch it should be on by name, the default branch to measure that base against, and the exact verification commands.
```

Change that sentence to:

```
Give it the spec path, the unit it owns, the base branch it should be on by name, the commit sha that branch pointed at, the default branch to measure that base against, and the exact verification commands.
```

Nothing else in the file changes. The front matter stays valid YAML on one line.

### 6. `tests/Muthur.Cli.Tests/KitInstallTests.cs` — the rule reaches every harness

Three additions to the existing class. No existing test, helper or field is modified. Every assertion uses
`StringComparison.Ordinal` and carries a message naming what went missing, as the existing ones do.

**a.** A `[Theory]` named `Every_harness_tells_an_orchestrator_where_a_spawned_worktree_comes_from`, over the
same three `(harness, procedure)` rows as the existing orchestrate theory
(`("claude", ".claude/skills/muthur-orchestrate/SKILL.md")`, `("codex", ".muthur/procedures/orchestrate.md")`,
`("generic", ".muthur/procedures/orchestrate.md")`). Install, assert the file exists, then assert it contains
both:

| String | Where it appears | Message when absent |
|---|---|---|
| `cut from the repository's HEAD, not from yours` | step 4, only | has lost the paragraph saying where a spawned worktree is cut from |
| `carry the branch name and the commit sha` | step 4, only | no longer requires a delegation prompt to carry the spec's branch and sha |
| `names the base branch, the commit sha of the spec on it` | the `## Rules` bullet, only | has lost the `## Rules` bullet that restates it |

**b.** A `[Theory]` named `Every_harness_says_why_the_implementers_base_check_exists`, over
`("claude", ".claude/agents/muthur-implementer.md")`, `("claude", ".claude/agents/muthur-specialist.md")`,
`("codex", ".muthur/procedures/implementer.md")` and `("generic", ".muthur/procedures/implementer.md")`.
Install, assert the file exists, then assert it contains `not yours to tidy away`, with a message saying the
contract no longer says why its base check exists and that a future reader will tidy it away. The specialist
row is there because the specialist agent reaches the same contract through its own `{{core:implementer.md}}`.

**c.** A `[Fact]` named `The_claude_kit_does_not_tell_an_orchestrator_the_worktree_comes_from_its_own_HEAD`.
Install the claude kit; read `.claude/skills/muthur-orchestrate/SKILL.md`; assert it does **not** contain
`created from your current HEAD`, with a message saying that claim is false and is what makes an orchestrator
trust a base the harness never used; and assert it **does** contain `not the branch you are standing on`.

Give each new test — or the pair, as one XML doc comment above the first — a comment in this repository's
voice explaining what it guards: thirteen units in one day arrived on the wrong branch, the kit is the only
thing that stops that hurting, and a rule that reaches only some harnesses does not.

## Units of work

### Unit A — the procedure, the contract, and the test that pins them
- **Files:** `kit/core/orchestrate.md`, `kit/claude/skills/muthur-orchestrate.md`,
  `kit/core/implementer.md`, `kit/claude/agents/muthur-implementer.md`,
  `tests/Muthur.Cli.Tests/KitInstallTests.cs`
- **Does:** edits 1–6 above, verbatim.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean (warnings are errors) and `dotnet test` green.
  - Each new test is load-bearing, checked one marker at a time and restored after each:
    1. Delete the step-4 paragraph from `kit/core/orchestrate.md` (edit 1 only, leaving the `## Rules`
       bullet): theory **a** fails on its first two assertions for all three harnesses, and the existing
       headless-rule theory still passes. Restore.
    2. Delete the `## Rules` bullet (edit 2 only): theory **a** fails on its third assertion only. Restore.
    3. Delete the paragraph from `kit/core/implementer.md` (edit 4): theory **b** fails on all four rows.
       Restore.
    4. Put `created from your current HEAD` back into `kit/claude/skills/muthur-orchestrate.md` (edit 3):
       the `[Fact]` fails. Restore.
  - After restoring everything, re-run `dotnet test` and report it green.
  - Report what each step printed. Do not adjust a test to make a step look right: if a removal does not fail
    the test the spec says it should, that is a defect in this spec — report it and stop.

This is one unit on purpose: the test asserts markers the same unit writes, so splitting it would leave one
half unable to verify itself.

## Verification

Headless; no browser, no GUI, no human.

```
dotnet build
dotnet test
```

Both clean. Then the point of the task — that the rule actually reaches a repository — from an installed
build, into a scratch directory, never against this repository or the live hub:

```powershell
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("t68-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:MUTHUR_KIT = "<the task worktree>\kit"
foreach ($h in 'claude','codex','generic') {
    $repo = Join-Path $scratch $h
    New-Item -ItemType Directory -Force $repo | Out-Null
    & "<an installed muthur.exe built from this branch>" kit install --harness $h --repo $repo | Out-Null
}
Get-ChildItem $scratch -Recurse -File |
    Select-String -Pattern "cut from the repository's HEAD, not from yours",
                           'carry the branch name and the commit sha',
                           'names the base branch, the commit sha of the spec on it',
                           'not yours to tidy away' |
    Group-Object Path | ForEach-Object { "{0}  {1}" -f $_.Count, $_.Name }
Get-ChildItem $scratch -Recurse -File | Select-String -Pattern 'created from your current HEAD','\{\{core:'
Remove-Item -Recurse -Force $scratch
```

Passing looks like: the three orchestrate procedures each matched by the first three patterns; the implementer
contract matched by the fourth in all four places it is installed (two under `claude`, one each under `codex`
and `generic`); and the last search — the false claim, and any unexpanded include — returning **nothing**.

Per `CLAUDE.md`, build the CLI with `scripts/install.ps1 -Destination ./artifacts/t68` and run that. `kit
install` reaches no hub, but the rule stands.

## Out of scope / follow-ups

- **Whether `muthur task spec` should refuse a spec path that is not on the branch the caller names.** T-68
  raises it; it is a separate task, and it is filed as one. T-50 made the branch a *hint* deliberately —
  "a source that does not have the file is passed over, not fatal… no caller is worse off than today" — and
  reversing that is a product decision about a shipped guard, not a kit edit. It also would not have caught
  any of the thirteen occurrences, which were implementers with no spec in their tree, not orchestrators
  attaching one.
- **Nothing tests that `muthur worker run` keeps resolving the base from the caller's branch.**
  `WorkerCommands.cs:170` is correct and this task does not touch it, but the behaviour the whole problem
  turns on has no test of its own.
