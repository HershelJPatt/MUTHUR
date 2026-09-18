# T-15 — Does `muthur worker run` hold up?

> The outcome of this task is a verdict, not a feature. This document is the record: what was run, what
> happened, and what an orchestrator needs to know. The change to `kit/core/orchestrate.md` is the
> deliverable that outlives it.

## Verdict

**It holds up.** `muthur worker run` does what M6 claimed: it stakes out an isolated worktree and branch,
hands a worker a frozen spec and one unit of it, runs it headless on whichever harness the tier has
available, keeps hub identity away from it, and brings back a structured report with the branch, the
commits, the duration and — where the harness reports it — the cost. Four exercises, four harness runs, two
vendors, no intervention needed in any of them.

It is also, for the first time, how real work got built here: **T-5 was built entirely by workers** — Unit A
by claude, Unit B by codex, in separate worktrees, then integrated, rebuilt and verified by the
orchestrator.

**One of the four criteria is established and one is not.** See *The two criteria the first version of this
document claimed wrongly*, below. Nothing else in this verdict changed.

Three things an orchestrator must know before relying on it are in **Findings**. None of them is a reason
not to use it; two of them will bite someone who assumes otherwise.

## What was run

All four against `task/T-5-ungated-projects`, whose spec (`specs/T-5-ungated-projects.md`) was written with
two deliberately independent units so this exercise had something that genuinely splits.

| # | Exercise | Harness | Result | Time | Cost |
|---|---|---|---|---|---|
| 1 | Unit A — the hub says it | claude/opus | `done`, committed itself, 4 tests | 389 s | $1.66 |
| 2 | Unit B — the dashboard shows it | codex | `done`, **launcher committed for it**, 2 tests | 232 s | not reported |
| 3 | A unit the spec does not contain | claude/opus | `spec-problem`, refused, no commits | 38 s | $0.39 |
| 4 | Same, with `claude-subscription` limited | **codex** (fell through) | `spec-problem`, refused | 22 s | not reported |

Exercises 1 and 2 produced the work now in validation as T-5: `dotnet build` clean, the server suite 139 →
145, and the behaviour confirmed end to end against a scratch hub.

## Findings

### 1. A worker cannot run `muthur`, so every spec's end-to-end verification falls to the orchestrator

By design: the launcher denies `muthur *` and the worker has no hub identity. Both real workers said so
unprompted — *"CLI verification omitted per assignment prohibition"*. This is correct and should not change:
a worker with hub authority could claim tasks, post verdicts, or land work.

The consequence is procedural and is the single most useful thing learned here. **A spec's Verification
section will be executed by two different parties**: the `dotnet build` / `dotnet test` part by the worker,
and anything invoking `muthur`, a scratch hub or a browser by the orchestrator or a validator. An
orchestrator who writes an end-to-end block and assumes the worker ran it is handing unverified work to
validation.

### 2. `committedByLauncher` is a real signal, not bookkeeping

Codex's worker finished its work and did not commit it. The launcher's safety net committed the tree and
set `committedByLauncher: true`, so nothing was lost — but the report still said `STATUS: done`, and the
report's own `COMMITS:` line read `pending (launcher)`. Without that net the orchestrator would have
reviewed an empty branch while holding a report claiming success.

Read `committedByLauncher` on every run. When it is true, the worker did not finish the job it was told to
finish, and the diff deserves more suspicion than usual — not because the code is wrong (it was not here),
but because the worker's account of itself is already known to be incomplete.

### 3. A limited candidate is invisible in the report

With `claude-subscription` marked limited, a run with no `--harness` correctly chose codex. But `skipped[]`
came back **empty**: a limited candidate is filtered out of the candidate list *before* any attempt, while
`skipped[]` only records candidates that were attempted and fell through. So the report says
`"harness": "codex"` and offers no way to tell whether codex was the first choice or the fallback.

That matters when comparing two workers' output, or explaining after the fact why a unit was built by the
vendor it was built by. Today the only record is the hub's own account-limit state at that moment, which is
not in the report and does not persist.

### 4. Failure reporting is good, and better than expected

A worker given a unit its spec does not contain refused in 38 seconds for $0.39, implemented nothing, left
the tree clean, and returned `success: false` with `status: spec-problem`. It also gathered evidence for the
refusal — grepping the spec for unit headings and the repository for the invented subject matter — and
flagged that the run's `--task` and `--spec` named different tasks, which was true and which the
orchestrator (me) had done deliberately.

Both vendors behaved the same way on the same bad input. An orchestrator gets enough to decide between
re-spec and retry without opening the worktree: `success`, `status`, `commits: []`, and prose naming what
was missing.

## What this does not prove

- **Nothing was tested under a worker that hangs or is killed.** Exercise 3 and 4 fail fast and cleanly; a
  session that runs to its `--timeout-minutes` and is reaped is a different path (`WorkerAttempt.Started`
  exists to distinguish it) and was not exercised.
- **The `utility` tier and local models were not touched.** Only `implementer` ran, on claude and codex.
- **No unit was large enough to strain the frozen-spec discipline.** Both real units were a handful of files.
  Whether a worker holds to a spec over a day's work is not answered here.
- **Cost comparison is not available.** Codex reports no `costUsd`, so the receipts T-17 wants will be
  partial for one of the two vendors this organization actually uses.

## The thing that nearly invalidated this exercise

`muthur.project.json` did not parse — `%LOCALAPPDATA%\Muthur` made `\M` an invalid JSON escape — and
`WorkerCommands.ReadProject` catches `JsonException` and returns empty lists. **Every worker run this
organization could have launched would have been handed no build and no test commands**, while its report
still said whatever the harness exited with.

Found before these exercises and fixed as T-28, which is why exercises 1 and 2 had real verification
commands. Had T-15 been run a day earlier, the verdict would have been drawn from workers that never built
or tested anything, and it would have looked like a pass.

The general lesson is worth more than the bug: **`worker run`'s inputs degrade silently**. A malformed
project manifest, a missing kit, a spec path that resolves to the wrong file — none of these announce
themselves in the worker's report. T-28's follow-up (fold into T-8: one `invalid_manifest` code, exit 2) is
the fix for one of them.

## Follow-ups filed

- The `skipped[]` gap in finding 3 — an orchestrator cannot see that a preferred harness was passed over.
- Codex reporting no cost, which limits T-17's receipts.
- Neither is filed as its own task yet; both belong with T-17 (receipts) and are recorded there rather than
  multiplying p1 tasks.

## Verification of this task

**What a validator must check** — all of it by reading, none of it by spending a session:

1. `kit/core/orchestrate.md` carries findings 1, 2 and 3 from this document. That file is what the next
   orchestrator reads; this document is not, and the doc change is the deliverable that outlives the task.
2. `git diff --stat main...<branch>` shows **exactly two files**: this spec and `kit/core/orchestrate.md`.
   There is no code on this branch, so `dotnet build` and `dotnet test` prove nothing about it. If the
   diff shows source files, the branch is wrong and that is the defect to report.
3. The claims in **Findings** match the recorded evidence in **What was run** — harness, status, timing and
   cost per exercise.

**What a validator must NOT do:** re-run the four `muthur worker run` exercises. They need two vendors'
CLIs on PATH, hub credentials and real subscription spend, and re-running them would cost roughly another
$4 to reproduce a verdict already recorded. They are listed below as provenance, so the work can be
repeated deliberately by someone who wants to, not as a step in validating this task.

(The first version of this section said only "there is no code to build" and then listed the commands,
which read as an instruction to re-run them. A conductor-started validator correctly reported **blocked**
against it: its sandbox had no `codex` on PATH and could not reach a scratch hub. The fault was this
section's, not the validator's.)

The four runs, for provenance:

```
muthur worker run --tier implementer --spec specs/T-5-ungated-projects.md \
  --unit "Unit A - the hub says it" --task T-5 --harness claude --base task/T-5-ungated-projects
muthur worker run --tier implementer --spec specs/T-5-ungated-projects.md \
  --unit "Unit B - the dashboard shows it" --task T-5 --harness codex --base task/T-5-ungated-projects
muthur worker run --tier implementer --spec specs/T-5-ungated-projects.md \
  --unit "Unit Z - reconcile the billing ledger against Stripe" --task T-15 --harness claude ...
muthur harness limit claude-subscription --minutes 10 --founder    # then the same run with no --harness
muthur harness limit claude-subscription --clear --founder
```



## An orchestrator error, recorded so it is not read as a tool finding

The first attempt to run exercises 1 and 2 in parallel launched only exercise 1. A shell variable did not
expand inside the second backgrounded subshell, so codex's run wrote to an unwritable path and never
started. `muthur worker run` did nothing wrong. Recorded because an unexplained "the parallel run failed"
in this record would have been read as evidence against the feature.

## A branch-hygiene defect of the orchestrator's, found by the blocked validation

`task/T-15-worker-run-verdict` was originally cut from `task/T-5-ungated-projects` rather than from `main`,
because T-5 was the vehicle these exercises were run on. That was a mistake: it made T-15's branch carry all
of T-5's source changes, so a validator handed T-15 ran the server suite against T-5's code and hit the
environment problems that stopped it — for a task that has no code in it at all.

Rebuilt from `main` carrying only its two files. The lesson generalises and is worth the next orchestrator's
attention: **a task branch is cut from the default branch, even when the work was performed on top of
another task's branch.** What the work was *done* on and what the task *delivers* are different things.

## The two criteria the first version of this document claimed wrongly

`conductor-validator` failed T-15 and was right to. It did not take this document's prose for evidence: it
read the hub's own worker-run events and found that two of T-15's four criteria were not established by the
runs recorded here.

> T-5 events 320/322 imply non-overlap (estimated Codex start 18:51:41Z after Claude completion 18:51:18Z);
> limit event 334 sits between failed runs 333/335.

Both readings are correct. The first version of this document contained the means to disprove its own claim
— it records, in *An orchestrator error*, that the first parallel attempt launched only one worker — and
then let the sentence "that is the M6 exit criterion, re-run and passed" stand anyway. **A false claim in a
verdict is worse than an unmet criterion**, because the whole value of the document is that its statements
can be relied on without re-deriving them.

### Criterion: two units, in parallel, in separate worktrees — now established

Re-run properly. Wall-clock, from the orchestrator's own log, UTC:

```
A_START 22:13:43.694    claude/opus   worker/t-15-unit-a-the-hub-says-it-cbdc27   126 s   $0.68
B_START 22:15:17.931    codex         worker/t-15-unit-b-the-dashboard-sho-f413f9 105 s   (no cost reported)
A_END   22:15:51.382
B_END   22:17:04.620
```

**33.45 seconds of genuine overlap** — B started while A was still running, on separate worktrees, on two
vendors, and both returned `success: true, status: done`. Adjacency in the first attempt, concurrency in
this one; the hub's own worker-run events for these two runs carry the same shape and are the receipt a
validator should check rather than this table.

Why the first attempt failed is worth keeping, because it recurred: a shell variable does not expand inside
the **second** backgrounded subshell in this environment, so the codex run wrote to an unwritable path and
never started. It happened twice, hours apart, and it is an environment behaviour rather than a slip. The
fix is to hardcode absolute paths in every subshell. `muthur worker run` did nothing wrong on either
occasion.

### Criterion: an account limited mid-run — NOT established, and I cannot establish it

What was actually done: `claude-subscription` was marked limited and a run was *then* started, so the
candidate was filtered out of `WorkerCommands.RunAsync`'s list **before any attempt**. That proves the
filter. It does not prove what the criterion asks for.

What the criterion asks for is an account going out of quota *while a worker is running*, so
`WorkerLauncher`'s `onRateLimited` callback fires and the launcher falls through to the next candidate
inside a single invocation. That path is driven by a harness adapter interpreting a real rate-limit response
from a vendor. Forcing one means genuinely exhausting a subscription; it is not reproducible for a validator
either.

This is recorded as unproven rather than quietly redefined, and the scope question — accept pre-run
filtering as the evidence, cover the path with a fake `IProcessRunner`, or leave it open — is with the
founder as request #9. **Until that is answered, treat mid-run fallthrough as untested.**
