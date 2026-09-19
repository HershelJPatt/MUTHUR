# T-44 — The base-verification contract should say the commits a reset discards are not yours

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`kit/core/implementer.md` says, at the moment an implementer is about to run the only destructive command in
the whole contract, **what that command destroys and whose it is**. The behaviour does not change; this is
about whether a reader believes the step enough to run it. The same file also gains the one piece of practical
knowledge T-40's implementer paid for: how to show a test is load-bearing without tripping the sandbox.

## Context

### Who this reaches

`kit/core/implementer.md` is included by `{{core:implementer.md}}` into **both** Claude agent definitions —
`kit/claude/agents/muthur-implementer.md` and `kit/claude/agents/muthur-specialist.md` — and copied verbatim
to `.muthur/procedures/implementer.md` by the codex and generic manifests. So one edit reaches every
implementer on every harness. The include itself is already guarded by
`KitInstallTests.No_installed_file_is_left_holding_an_unexpanded_include` (T-46, landed).

### Why the wording matters

T-40's implementer was the first agent to use T-37's contract in anger rather than while writing it. Its
containment test worked and caught a real miss — a worktree cut from `main`'s tip, carrying `Land T-43` and
one commit more than the task branch. `--is-ancestor HEAD <base>` failed, `--is-ancestor HEAD main` succeeded,
bullet 2 applied, and it reset. Its note:

> the contract says "everything you have is already contained somewhere", which is true, but the thing worth
> saying out loud is that the reset discards commits that belong to another task — I would have found an
> explicit "the commits you are dropping are not yours" sentence reassuring before running `--hard`.

The current text justifies the safety by containment. That is correct and abstract, and abstraction is the
wrong register three words before `git reset --hard`.

**The strong form of the reassurance is already sitting in the check itself**, unsaid: if
`--is-ancestor HEAD <default branch>` succeeds, then everything on this branch is landed — and the
implementer's own work, by definition, is not landed. So the check that licenses the reset is the same fact
that proves nothing being dropped is theirs. Say that.

### The two bullets are not the same case

Do not give them the same sentence.

- **Bullet 1** (`--is-ancestor HEAD <base>` succeeds): HEAD is an ancestor of the base, so `reset --hard`
  moves the branch *forward*. **No commit is discarded at all.**
- **Bullet 2** (`--is-ancestor HEAD <default branch>` succeeds): HEAD carries commits the base does not, so
  the reset genuinely drops them off this branch — and every one is already on the default branch.

Writing "the commits you drop are not yours" into bullet 1 would be wrong: there are none.

## Non-goals

- Any behaviour change. No command, order, or condition in step 1 moves. This is prose.
- `kit/core/orchestrate.md`, `validate.md`, `oncall.md`, the briefs, or any `kit.json`.
- Editing `tests/Muthur.Cli.Tests/KitInstallTests.cs`. T-8 is in validation and touches that file; the
  include mechanism it guards is generic and already covers this change. Keep clear of it.
- Any new test file. This task's proof is the install, which the Verification section runs.

## Design

### 1. Bullet 1 of step 1 — say that nothing is dropped

Current text:

> **If `git merge-base --is-ancestor HEAD <base>` succeeds**, everything here is already in the base, so
> nothing can be lost: with `git status --porcelain` empty, `git reset --hard <base>`, and report it as a
> fast-forward reset.

Replace the clause `so nothing can be lost:` with:

```
so the reset only moves this branch forward and discards no commit at all:
```

Nothing else in the bullet changes.

### 2. Bullet 2 of step 1 — say whose commits are being dropped

Current text:

> **Otherwise, if `git merge-base --is-ancestor HEAD <default branch>` succeeds** — `main` unless your
> project says otherwise, and your orchestrator should name it alongside the base — everything here is
> already landed. The extra commits are inherited from another lineage and belong to nobody in this unit:
> reset the same way and on the same clean-tree condition, and report it as a **divergent** reset.

Replace the sentence beginning `The extra commits are inherited` with:

```
The extra commits are inherited from another lineage and belong to nobody in this unit. Say it plainly,
because `git reset --hard` is the only destructive command in this contract and you are about to run it:
**the commits it drops off your branch are not yours.** Every one of them is already on the default branch —
that is exactly what the check you just ran proved — so each belongs to a task that has already landed, and
none of your unit's work can be among them, because you have not written any yet and unlanded work could not
have passed that check. Reset the same way and on the same clean-tree condition, and report it as a
**divergent** reset.
```

The rest of the bullet — the `git log --oneline <base>..HEAD` sentence and its warning that the list measures
history rather than containment — stays exactly as it is, and stays *after* the new text.

### 3. Step 4 — how to show a test is load-bearing

Step 4 currently reads, in full:

> 4. Build and run the verification commands. Fix what fails. Do not weaken, skip or delete a test to get green.

Append to it, as part of the same numbered item:

```
   If you are asked to show that a test is load-bearing — that it fails when the change it guards is removed
   — put the production code back as it was and re-run: `git checkout <base> -- <the file>` for a file you
   edited, or delete a file you added. Then restore it and run the suite again before you commit, and report
   what you saw at each step. Do not try to disable the code in place with `if (false && …)` or by commenting
   the call out: an auto-mode classifier reads that as removing a security check and refuses to run it, which
   costs a round trip to discover.
```

Keep the indentation of the surrounding numbered list. The `if (false && …)` failure is reported by T-40's
implementer; the `git checkout <base> -- <file>` form is what implementers on this project have used
successfully, repeatedly, when asked for exactly this proof.

## Units of work

### Unit A — three edits to one file
- **Files:** `kit/core/implementer.md`. Nothing else, in the whole repository.
- **Does:** sections 1, 2 and 3 above, verbatim.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean, `dotnet test` green (no code changes, so this is a regression check on
  the kit tests, not a claim about the edit), and the three replacements present with no other diff —
  `git diff --stat` must show `kit/core/implementer.md` and no other file.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then the install proof, which is the only thing that shows the words reach an implementer. From
the task branch, into a scratch directory:

```powershell
pwsh ./scripts/install.ps1 -Destination ./artifacts/t44
$scratch = "<a scratch dir>"
$env:MUTHUR_KIT = "<the task worktree>\kit"
foreach ($h in 'claude','codex','generic') {
    $repo = Join-Path $scratch $h
    New-Item -ItemType Directory -Force $repo | Out-Null
    & ./artifacts/t44/muthur.exe kit install --harness $h --repo $repo | Out-Null
}
Get-ChildItem $scratch -Recurse -File |
    Select-String -Pattern 'the commits it drops off your branch are not yours' |
    Group-Object Path | ForEach-Object { $_.Name }
```

Passing looks like **four** files matching: `.muthur/procedures/implementer.md` under codex and under generic,
and both `.claude/agents/muthur-implementer.md` and `.claude/agents/muthur-specialist.md` under claude — the
specialist agent includes the same core file, which is the reason to check for four rather than three.

Also confirm, in the same installed tree:

- `discards no commit at all` appears in the same four files (section 1);
- `reads that as removing a security check` appears in the same four files (section 3);
- no `{{core:` token survives anywhere under the scratch directory.

No browser is needed and none should be used.

## Out of scope / follow-ups

- **The contract still never asks an implementer to prove a check is load-bearing.** Section 3 documents the
  technique for when an *orchestrator* asks, which is what happens in practice. Whether the contract should
  require it for any test that guards a production change is a real question about how much this organization
  wants to pay for mutation evidence, and it belongs in its own task.

## Amendment 1 — the Verification script's ordering, and a rewrap (2026-09-19)

Two things the build surfaced, neither changing what was written.

**1. The script in Verification installs before committing, and `install.ps1` refuses a dirty tree** — *"the
working tree has uncommitted changes, so the build would correspond to no commit."* So the install proof
necessarily runs after the commit, which is also the stronger proof: it measures a named commit rather than
whatever happened to be on disk. Anyone reusing that script should commit first. Left as a note rather than
rewritten, because the ordering it implies is the right one and only the prose was misleading.

**2. The replacements changed where the surrounding prose wraps.** Inserting them left a 60-character line
stranded mid-paragraph in bullet 2, so the implementer re-filled the affected paragraphs to the file's
existing ~110-column width. That touched the tail of bullet 1 and the `git log --oneline <base>..HEAD`
sentence — which this spec said stays exactly as it is. **Its words and its position after the new text are
untouched**; only the line breaks moved. That is the right call: a spec that says "leave this sentence alone"
means the sentence, not its accidental line endings, and leaving the file ragged to honour the letter of it
would have been worse.

## Proof (2026-09-19)

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test
  Muthur.Launch.Tests    23/23
  Muthur.Core.Tests      3708/3708
  Muthur.Cli.Tests       65/65
  Muthur.Server.Tests    280/280
```

`git diff --stat origin/main..HEAD` — `kit/core/implementer.md` and this spec, nothing else.

The install proof, with a CLI built from this branch installing into three scratch repositories:

```
'the commits it drops off your branch are not yours'   files: 4
'discards no commit at all'                            files: 4
'reads that as removing a security check'              files: 4

      \claude\.claude\agents\muthur-implementer.md
      \claude\.claude\agents\muthur-specialist.md
      \codex\.muthur\procedures\implementer.md
      \generic\.muthur\procedures\implementer.md

unexpanded {{core: tokens: none
```

Four, not three: `kit/claude/agents/muthur-specialist.md` includes the same `{{core:implementer.md}}` as the
implementer agent, so a mastermind-tier specialist gets the contract too. Worth knowing for any future edit to
a core procedure — the count of files an edit reaches is not the count of harnesses.

No browser was used.
