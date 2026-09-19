# T-37 — implementers keep getting a worktree that is not the base they were told

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

An implementer verifies the base it was given before doing any work, and knows what to do when it is wrong,
because the contract it reads says so — not because the orchestrator who wrote its prompt happened to
remember. Eight implementers in a row have been handed a worktree that was not the task branch; all eight
recovered, every time because a hand-written line in the prompt told them to check.

## What was actually found, which is not what this task's title says

This task was filed claiming "agent worktrees are created from a stale `origin/main`". **That diagnosis is
wrong, and the evidence for it was one observation over-generalised.** Eight cases now:

| Observed base | Shape |
|---|---|
| `da697a5`, stale `origin/main` while local `main` was ahead | divergent: ~50 commits one side, 3 the other |
| `54c455d`, local `main` at the time | ancestor: clean fast-forward |
| `8687d37`, an older landed commit | ancestor: clean fast-forward (×4) |
| `980b4bb`, local `main`, one commit behind the task branch | ancestor: clean fast-forward |
| `c15a6a0`, current `main`, task branch based 17 commits earlier | divergent: 17 one side, 1 the other |
| `d7a2db7`, a landed commit one behind the task branch | ancestor: clean fast-forward (case nine, found while building this) |

The bases differ; the source is not consistently `origin/main`. **What every case shares is the only claim
worth making: the worktree is never created from the task branch the orchestrator named, so the frozen spec
is missing from it.**

### And it is not MUTHUR's code

Two different mechanisms create worktrees in this repository, and only one of them is ours:

- `.worktrees/` — created by `muthur worker run`, with
  `git worktree add -b <branch> <path> <baseRef>` where `baseRef` is the `--base` it was given. It then
  **verifies the spec is present in the new worktree** and refuses with `spec_not_committed` if it is not
  (`WorkerCommands.cs`, the two checks around the `worktree add`). Every `muthur worker run` this
  organization has done landed on the right base. This path is correct and needs no change.
- `.claude/worktrees/agent-*` — created by the Claude Code harness when an orchestrator delegates through
  its own agent tool. This is where all eight wrong bases came from. It is outside MUTHUR's control.

So MUTHUR already solved this problem for its own delegation path, and already has the guard that catches
it. The eight failures all came from the path that is not MUTHUR's.

**What is in MUTHUR's control is the contract.** `kit/core/implementer.md` is 40 lines and says "the git
worktree and branch you were given" without a word about checking that it is. That is the fix.

## Non-goals

- Do not change `muthur worker run`, `WorkerCommands.cs`, or anything under `src/`. The MUTHUR path is
  correct; this task's finding is that it was never the problem.
- Do not attempt to fix or work around the Claude Code harness's worktree creation. It is not ours, and a
  workaround in our kit that assumes its behaviour would rot the first time it changes.
- Do not add a `muthur` command, a hub check, or a doctor probe. An implementer has no hub identity by
  design, so the check must be something it can do with git alone.
- Do not rewrite the rest of `kit/core/implementer.md`. Add to it.

## Design

Add one step to `kit/core/implementer.md`, as the **first** thing under its numbered procedure — before
reading the spec, because a spec read from the wrong tree is the wrong spec.

```markdown
0. **Check you are where you were told.** Your orchestrator names a base branch. Run `git log --oneline -3`
   and confirm the spec it named is present. If it is not:
   - `git status --porcelain` and `git log --oneline <base>..HEAD`. If your branch has **no commits of its
     own** and the tree is clean, nothing of yours can be lost: `git reset --hard <base>`, and say in your
     report whether that was a fast-forward (`git merge-base --is-ancestor HEAD <base>` succeeds) or a
     divergent reset. Both happen; which one it was is worth a line.
   - If you **do** have commits of your own, stop and report. Do not merge and do not rebase — recovering a
     mixed history is the orchestrator's decision, not yours.
   Never begin work against a tree whose spec you could not find. A spec read from the wrong base is the
   wrong spec, and the work will look correct and be wrong.
```

Renumber the existing steps 1-4 so the list reads 0-4, or renumber 1 → 2 and so on if that reads better in
context — the implementer building this decides which, since it is formatting.

**"Stop and report" must name the status**: `blocked`. The report format offers `done | blocked |
spec-problem`, and a bad base carrying your own commits is none of them cleanly, so a fresh implementer will
guess and different ones will guess differently — which makes the case invisible in aggregate exactly when
someone wants to count how often it happens. Say `blocked` in the step.

### Two further files, because the step is defeated without them

**`kit/claude/agents/muthur-implementer.md`** states as fact that the worktree is "based on the
orchestrator's task branch". That sentence has been false in all nine observed cases, and because it sits
*below* the `{{core:implementer.md}}` include, it is the document's **last word** on the subject — a fresh
implementer reads the new step and then reads a reassurance that the step exists to distrust. Change "based
on" to "intended to be based on", and nothing else in that file.

**`kit/core/orchestrate.md`** — the recovery path substitutes a `<base>` the prompt must have supplied. The
step detects a bad base robustly (the spec file is either present or it is not) but cannot *recover* from
one unless the orchestrator named the branch. Add to step 4's list of what to give an implementer: the base
branch it should be on, by name. One clause, in the existing sentence that already lists the spec path, the
unit and the verification commands.

Add one line to the **Report** block's template so the answer is always present rather than volunteered:

```
BASE: <the commit you started from, and what you had to do to get there, if anything>
```

## Units of work

### Unit A — the whole change
- **Files:** modified `kit/core/implementer.md`, `kit/claude/agents/muthur-implementer.md`,
  `kit/core/orchestrate.md`
- **Does:** the Design section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` and `dotnet test` still green — this is a documentation change and must not touch code,
    so the point of running them is to prove it did not.
  - `git diff --stat` names exactly three files, all under `kit/`.
  - `kit/core/implementer.md` still reads as one document: the new step is in the same voice as the rest,
    the numbering is consistent, and the Report block matches what the procedure now asks for.
  - `muthur kit install` is **not** run by the implementer — the kit is installed into the live hub's
    directory and that is a founder action.

## Verification

```
dotnet build
dotnet test
```

Then read the file end to end. The check that matters is not mechanical: does an implementer who reads only
this document, with no hand-written reminder in its prompt, now know to verify its base and what to do about
a bad one? That is the whole point, and eight prompts written by hand are the evidence it was not true
before.

A validator should also confirm the negative: `git diff --stat main..<branch> -- src/ tests/` is empty.

## Out of scope / follow-ups

- The kit must be reinstalled into the live hub for this to reach anyone, and the live installation is
  currently at `54c455d` — well behind. That is a founder action (`scripts/install.ps1` refuses to replace a
  running hub without `-RestartRunning`, deliberately), and it is the same staleness T-32 exists to make
  visible.
- If the harness's worktree base is ever worth reporting upstream, the eight-case table above is the
  evidence. Nothing in MUTHUR can fix it.
