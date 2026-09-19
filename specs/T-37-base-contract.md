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
0. **Check you are where you were told.** Your orchestrator names a base branch. Run
   `git rev-parse HEAD` and `git rev-parse <base>` and confirm **they are the same commit**. Presence of the
   spec file is not enough: a stale ancestor often already contains an older version of it, so the file is
   there, the check passes, and you build from a spec that has since been amended. That failure looks
   correct all the way to the verdict. If the two commits differ:
   - `git status --porcelain` and `git log --oneline <base>..HEAD`. If your branch has **no commits of its
     own** and the tree is clean, nothing of yours can be lost: `git reset --hard <base>`, and say in your
     report whether that was a fast-forward (`git merge-base --is-ancestor HEAD <base>` succeeds) or a
     divergent reset. Both happen; which one it was is worth a line.
   - If you **do** have commits of your own, you are being resumed for another round. Do not reset, do not
     merge and do not rebase. Read the spec from the base instead of from your working tree —
     `git show <base>:<spec path>` — because the orchestrator has very likely amended it, and your copy is
     the old one. Commit your new work on top of what you have, and say in your report that you did this.
     If the two histories have genuinely diverged in a way you cannot read past, stop and report `blocked`:
     recovering a mixed history is the orchestrator's decision, not yours.
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

**`kit/claude/agents/muthur-implementer.md` frontmatter, line 3.** Its `description` says *"Give it the
spec path, the unit it owns, and the exact verification commands"* — the same list `orchestrate.md` step 4
is amended to extend, and **the copy an orchestrator actually meets at the moment of delegation**, since the
agent card is what the harness surfaces when spawning while `orchestrate.md` is read once at the start of a
loop. Add the base branch to that list too. Found by the implementer after the first amendment closed the
gap in one file and left it open in the more-read one.

**`kit/core/orchestrate.md`, the stale count.** Lines 56-58 say the worktree tooling "has handed
implementers a stale base **three times in a row** here". It is now nine, and this is the only place in the
kit carrying a number. Remove the count rather than correcting it — a figure in a procedure drifts the day
after it is written, and "repeatedly" carries the same weight without going stale.

**`kit/core/implementer.md`, the residual in step 1.** It opens "Your orchestrator names a base branch",
stated as a fact about the world — the same shape of sentence the agent-card fix was about. Add: if it did
not, ask before starting. The implementer flagged this against its own work.

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

## Proof (2026-09-18)

```
dotnet build   0 warnings, 0 errors
dotnet test    Launch 23 + Cli 40 + Core 3692 + Server 196 = 3951, all passing
git diff --stat main...HEAD -- src/ tests/   empty
```

The tests are run to prove a documentation change touched no code, not because they exercise it. The real
check is the cold read, and the implementer — the ninth to be handed a wrong base, and saved for the ninth
time by a hand-written line in its prompt rather than by this contract — did it three times and found
something real each time:

1. **"Stop and report" named no status.** A bad base carrying your own commits fits none of
   `done | blocked | spec-problem` cleanly, so implementers would guess differently and the case would be
   invisible in aggregate — exactly when someone wants to count how often it happens. Now says `blocked`.
2. **`kit/claude/agents/muthur-implementer.md` asserted the worktree is "based on the orchestrator's task
   branch"** — false in all nine cases, and sitting *below* the `{{core:implementer.md}}` include, so it was
   the document's last word. A fresh implementer would have read the new step and then read a reassurance
   that the step exists to distrust. Now "intended to be based on", which as the implementer put it *states
   the orchestrator's intent, which is true, rather than the outcome, which has been false nine times*.
3. **The first fix closed the gap in the less-read file.** `orchestrate.md` step 4 gained "name the base
   branch", but the agent card's frontmatter `description` carried the same list and is **the copy an
   orchestrator meets at the moment of delegation** — the harness surfaces the card when spawning, while
   `orchestrate.md` is read once at the start of a loop. Both now name it.

Also removed: the kit's only hard count of this failure, `orchestrate.md`'s "three times in a row". It was
nine by the time it was read. A number in a procedure is stale the day after it is written.

### What this does not do

The contract only reaches an implementer once the kit is reinstalled into the live hub, which is a founder
action, and the live installation is at `54c455d` — well behind. Until then the instruction to verify your
base exists only on this branch. For a change specifically about implementers trusting a base nobody told
them to check, that gap is worth closing sooner rather than later.

## Amendment 1 — presence is not enough (2026-09-18)

`conductor-validator` failed T-37 at `134d8bc`, having installed the kit from this branch and reproduced the
hole against real git:

> Installed Claude/Codex contracts allow a stale ancestor containing an older spec: file-presence check
> succeeds, recovery is skipped, named base/spec content is never verified.

They are right, and the defect is worse than the one this task was filed for. "Confirm the spec it named is
present" passes on any base that already contains *some* version of that file — which a stale ancestor
usually does, because the spec is committed early and amended later. The implementer then reads a spec that
has since changed, builds exactly what it says, and reports success. **Nothing downstream can tell that
apart from correct work**, which is precisely the failure this contract exists to stop, arrived at through
the contract itself.

This organization amends specs constantly — T-15, T-16, T-22, T-32 and T-37 itself all gained amendments
mid-build — so the stale-spec case is not a corner, it is the common one.

The check is now identity, not existence: `git rev-parse HEAD` must equal `git rev-parse <base>`.

And the resumed-round case is written down rather than left to each implementer to reinvent. Every resumed
implementer in this session was told by hand to read the amended spec with `git show <base>:<spec>`; none of
them could have known to from the contract, and "stop and report" would have been wrong advice for all of
them — they had their own commits legitimately, because they were on round two.

## Amendment 2 — identity is not enough either (2026-09-18)

Amendment 1's implementer was asked to attack its own work the way the validator attacked the last version,
and **built a counterexample that passes the identity check while serving a stale spec**:

```
git checkout <old-sha> -- specs/T-9.md    # stage the pre-amendment spec; HEAD untouched
HEAD == base : True            <- the new check passes
spec on disk : "v1: build a widget"       (the amendment says gadget)
```

The check reads commits; nothing on the happy path ever reads the working tree. `git status --porcelain`
appears only inside the failure branch. So any worktree whose spec file is dirty — a stray revert, a
partially applied stash, a harness that populates files rather than checking them out — passes and hands
over the wrong spec.

Its own fix is the right one and costs a clause, so all four holes below close together.

### 1. Read the spec from the base, always

Not from the working tree, and not only on the resumed path:

```
git show <base>:<spec path>
```

The amended text already reaches for this command when an implementer is resumed; it simply did not reach
for it on the ordinary path. Doing it always makes the working tree irrelevant to what gets read, which is
the only way to be sure the bytes are the ones the orchestrator froze.

### 2. The base must be a branch, not a commit

`git rev-parse <sha>` is constant, so if an orchestrator names a SHA and then amends the spec, `HEAD == base`
passes forever and the amendment is invisible. Identity only tracks amendments when `<base>` is a moving
ref. Say so in the step: if you were given a bare commit, ask for the branch.

### 3. Run every command in your own worktree

The step never says where to run. This repository has a main checkout plus many `.claude/worktrees/agent-*`
siblings sharing one `.git`, and orchestrator prompts hand out absolute paths — the prompt for this very
amendment said *"in the MUTHUR repository (`C:\WorkSrc\MUTHUR`)"*, which is the **main** checkout, on
whatever branch it happens to be sitting. Reading a spec from there passes every check and returns another
branch's file. The implementer called this the likeliest real recurrence "because it is the mistake the
prompt invites", and it is right: that phrasing is mine, in nine prompts.

Add: confirm `git rev-parse --show-toplevel` is your own worktree, and never read a file through an absolute
path into another checkout.

### 4. The closing line still says "find"

The step ends *"Never begin work against a tree whose spec you could not find"* — presence language under a
step that no longer checks presence, and the one sentence a cold reader could take as permission to go back
to looking for the file. Replace "could not find" with "could not verify".

### What this pattern is worth recording

Three rounds, three different people, three real holes, each found only by attacking the previous fix rather
than reading it: the validator installed the kit and drove it against git; this implementer built a dirty
tree and watched the check pass. Reading would have caught none of the three, because each version *looks*
right. That is the argument for adversarial verification of documentation, which is normally the artefact
least likely to get it.

## Amendment 3 — the ring outside the spec, and where this stops (2026-09-18)

A third adversarial round broke the step again, less deeply, and the implementer's own assessment is the one
I am acting on: the spec-read path is now sound and it could not break it; everything it found is one ring
outward. Two of those are cheap and close here. The rest are recorded as known limits, and **the adversarial
loop stops at this amendment** — four rounds have produced four real findings, and the remaining three are
outside what a contract written for an implementer can reach.

### Closing: the rest of the working tree

`git show <base>:<spec>` secures the *spec*. Nothing secures any other file, demonstrated:

```
git checkout <old-sha> -- src/Config.cs   # HEAD untouched
HEAD == base : True
spec via show: "spec v2 AMENDED"      <- Amendment 2 works
code on disk : "int Limit = 10; // v1"   <- stale, unchecked
```

This is worse than a stale read. Step 2 says "read the code you will touch and the code next to it", from
disk; the implementer then edits that stale file and commits, so the diff **silently reverts** the base's
change and looks like a deliberate edit. You cannot `git show` your way out of it, because the work must
happen in the working tree.

Add to step 1: **`git status --porcelain` must be empty before you begin.** The command is already in the
step, used only inside the failure branch; this promotes it to a precondition. `git show` secures a read; a
clean tree is the only thing that secures an edit.

### Closing: a resumed round runs on a stale code tree

Proven on the implementer's own branch this round. The step correctly tells a resumed implementer not to
reset, merge or rebase, and `git show` gets it the right spec bytes — but its commits sit on the old base
while the branch has moved. Had the amendment also touched `kit/core/implementer.md`, the file it was
editing, it would have edited the pre-amendment version and its branch would have clobbered it at merge,
with no check anywhere firing. It was safe only because the base moved the spec file alone — which it
verified by hand with `git diff --stat <start>..<base>`, and which the contract never asks for.

Add to the resumed bullet: **run `git diff --stat <your starting commit>..<base>` and say in your report
whether it touched any file in your unit.** If it did, stop and report `blocked` rather than guessing.

### Recorded, not closed

- **The base *name* is unverifiable by the implementer.** Every check is consistency with the named base. An
  orchestrator who names a base carrying an *older* copy of the spec — an earlier task branch, or `main`
  after a spec has landed — passes toplevel, branch-ness, identity and `git show`, and the implementer
  builds the wrong thing with four green checks. The orchestrator's prompt is the single point of trust and
  no contract written for the implementer can check it. The mitigation belongs in `orchestrate.md`: name the
  branch you committed the frozen spec to, and no other. Added there as a clause, not a mechanism.
- **A detached HEAD passes everything.** `rev-parse HEAD == base` is satisfied at the base commit; work
  commits, `git branch --show-current` is empty, and the orchestrator merges the named branch and gets
  nothing. Only the blank `BRANCH:` line in the report catches it. Harness worktrees are created with `-b`,
  so this is latent rather than live.
- **The local base ref can be behind its remote.** Airtight while one `.git` is shared — commits from any
  worktree are visible instantly — and an exact reproduction of the stale-spec failure the day orchestration
  crosses clones or machines. Worth knowing as the next one, not worth text today.

## Amendment 4 — the resumed clause fires on the common case (2026-09-18)

Amendment 3's moved-files clause blocked on a benign base move the **very next time it ran**, and it was
found by obeying the step rather than probing it.

The base had moved because I had **integrated the implementer's own three commits**. `git diff --stat
<start>..<base>` therefore named `kit/core/implementer.md` — squarely in its unit — and the clause says
flatly: *if it touched any file in your unit, stop and report `blocked` rather than guessing.* Taken
literally it should have stopped, on a routine integration, with nothing stale and nothing to lose: the
base's copy of the file was byte-identical to its own, and `merge-base --is-ancestor HEAD <base>` was true.

**Integration is the common case for a resumed implementer**, so a reader who reaches that clause first would
block spuriously and often — and a check that cries wolf on the normal case is one people learn to skip,
which is the habit this whole family of contracts exists to prevent.

The step as a whole already lands correctly: the earlier bullet's test, `git log --oneline <base>..HEAD`,
came back empty, so by the contract's own definition it had no commits of its own, and the prescribed move
was a fast-forward reset — which is what happened. Only the resumed clause, read in isolation, misfires.

**The fix is ordering, not new logic** (the implementer's own proposal): test `<base>..HEAD` for commits of
your own **first**, and consult the moved-files diff only if you actually have some. A resumed implementer
whose work has been integrated has no commits of its own any more, so it never reaches the clause; one that
genuinely does have unintegrated commits still gets the check that clause was written for.

Reword the resumed bullet so the moved-files check sits inside the "you do have commits of your own" branch
rather than reading as an independent test.

## Proof (2026-09-18)

```
dotnet build   0 warnings, 0 errors
dotnet test    Launch 23 + Cli 40 + Core 3692 + Server 196 = 3951, all passing
git diff --stat main...HEAD -- src/ tests/   empty
```

Tests are run to prove a documentation change touched no code. The evidence that matters is different in
kind, and it is the point of this record.

### Five rounds, five defects, none of them found by reading

| Round | Found by | Defect |
|---|---|---|
| 1 | the implementer, reviewing its own diff | "stop and report" named no status; the agent card asserted the base *was* the task branch, below the include, so it was the document's last word |
| 2 | the same implementer | the "name the base branch" fix landed in the less-read file; the agent card's frontmatter — what the harness shows at delegation — still omitted it |
| 3 | `conductor-validator`, by **installing the kit and driving it against real git** | a stale ancestor already contains an older spec, so the presence check passes, recovery is skipped, and the implementer builds from a superseded spec. Looks correct to the verdict |
| 4 | the next implementer, by **building a counterexample** | identity passes on a dirty tree: stage an old spec, HEAD unmoved, check green. And `git show` secures the spec while every other file is read from disk — so an implementer edits a stale file and its diff *silently reverts* the base's change |
| 5 | the same implementer, **by obeying the step rather than probing it** | the moved-files clause blocked on routine integration — the common resumed case — and a check that cries wolf on the normal case is one people learn to skip |

Every version looked right. Reading would have caught none of them. Round 5 is the one I would keep if I
kept one: it arrived *after* I ended the adversarial rounds, from ordinary use, which says attack and use
find different things and that stopping at "no one can break it" is stopping early.

### The step as it now stands

Own worktree → base is a branch → `HEAD` == base → recover if not (own-commits test first, moved-files check
only inside that branch) → spec via `git show <base>:<spec path>` → clean tree before starting.

The last run exercised exactly the case Amendment 4 exists for — the base had moved because the
orchestrator integrated the implementer's own commits — and the reordered step routed it correctly and
silently, never reaching the clause that had misfired. That is negative evidence, and it is the right kind.

### What is not closed, and why

Three limits live here rather than in the contract, because no contract written for an implementer can
reach them: an **incorrectly named base** (every check is consistency with whatever branch was handed over;
mitigated as far as it can be by `orchestrate.md`'s "the branch you committed the frozen spec to and no
other"), a **detached HEAD** at the base commit, and a **local base ref behind its remote** — airtight while
one `.git` is shared, and an exact reproduction of the stale-spec failure the day orchestration crosses
clones.

A reader of `kit/core/implementer.md` alone will not know those three are known. That is a real property of
where the record lives, and this section is the record.
