# T-19 — A specialist that can run its own fleet, not just work alone

> **NOT A FROZEN SPEC.** This is the investigation the decision needs, and the decision is the founder's.
> `muthur ask` #18 is open against this task. Nothing is built until it is answered.

## What the task asks, and what turned out to be already true

T-19 ends with a precondition:

> Related: `muthur worker run` has never actually been exercised by this organization. Every task so far was
> small enough that the orchestrator did the work itself. **Before building a second level, prove the first
> one works.**

**That is out of date.** The task was filed 2026-09-18 04:12; `worker run` was exercised that evening. The
ledger holds six `worker.*` events:

| seq | at | event | harness | unit |
|---|---|---|---|---|
| 320 | 18 Sep 18:51 | `worker.finished` | claude/opus | T-5 unit A |
| 322 | 18 Sep 18:55 | `worker.finished` | codex/default | T-5 unit B |
| 333 | 18 Sep 19:14 | `worker.failed` | claude/opus | T-15 "Unit Z — reconcile the billing ledger against Stripe" |
| 335 | 18 Sep 19:15 | `worker.failed` | codex/default | T-15 "Unit Z — reconcile the billing ledger against Stripe" |
| 653 | 18 Sep 22:15 | `worker.finished` | claude/opus | T-15 unit A |
| 654 | 18 Sep 22:17 | `worker.finished` | codex/default | T-15 unit B |

**The two failures are not defects.** Both are the same unit — reconciling a billing ledger against Stripe,
which this product does not have — run once on each vendor. That is T-15 exercising the `spec-problem` path
on purpose: a worker asked for something the spec does not contain returns `success: false`, no commits, a
clean tree. It failed in 38 and 22 seconds respectively, which is the shape of a worker reading a spec and
declining rather than flailing.

So the first level is proven, on two vendors, in both directions: four real units built, two impossible units
correctly refused. `kit/core/orchestrate.md` already says as much — *"`worker run` works — T-5 was built
entirely this way, one unit on claude and one on codex."* The precondition is met.

## The first question has a measured answer: no, twice over

> Can a `worker run` process itself call `worker run`?

**No, and two independent mechanisms stop it**, both deliberate. `src/Muthur.Cli/Commands/WorkerCommands.cs:19`:

```csharp
private static readonly string[] Denied =
    ["muthur *", "muthur.exe *", "git push*", "git merge*", "git rebase*", "git checkout*", "git switch*", "git worktree*", "gh *"];
```

1. **`muthur *` is denied**, so the worker cannot invoke the CLI at all — which is the stated invariant, the
   founder's own words in this task: *"it has no hub identity by design, and that is deliberate — the
   launcher, not the worker, talks to the hub."*
2. **`git worktree*` is denied**, so even if it could, `worker run` would fail at
   `git worktree add` (`WorkerCommands.cs:139`), which is how it gives each worker a tree of its own.

And `worker run` genuinely needs the hub, not just for bookkeeping: it reads the tier catalog
(`Routes.Tiers`), reports account exhaustion (`Routes.AccountLimits`) and posts the run
(`Routes.WorkerRuns`). A worker with no identity cannot do any of those.

So nesting is not a small relaxation. It is a decision about an architectural invariant.

## The fork

Three ways to get a second level, none free. **This is `muthur ask` #18.**

### A — native subagents

The specialist uses its own harness's subagent mechanism (Claude's `Task`, which the
`muthur-specialist` agent definition already has). Works today, needs no change to MUTHUR at all.

- **Costs nothing to build.** It is already possible; this orchestrator has used it all session.
- **Harness-specific.** Codex and local models have no equivalent, so the shape exists on one vendor only —
  in a system whose whole point is that the tier is staffed by whichever vendor is available.
- **The ledger records nothing.** `worker.*` events come from the launcher, and a native subagent never
  reaches it. The tree is not reconstructable, which this task names as a requirement.

### B — let a worker launch workers

Relax `Denied` for `muthur worker run` specifically, and give the worker a scoped identity so the hub calls
it needs can succeed.

- **Vendor-neutral and fully recorded** — every nested run lands in the ledger like any other.
- **Breaks the stated invariant.** A worker that can talk to the hub is a worker that can do other things to
  the hub; the deny list is coarse and `muthur worker run` is not separable from `muthur task land` by a glob
  without care.
- Needs a new kind of credential — a token scoped to one run — which does not exist today.

### C — the specialist proposes, the level above staffs

The specialist does not launch anything. It returns a **plan of units** in its report, and the orchestrator
(or the launcher, mechanically) runs them.

- **Keeps both invariants**: the launcher still talks to the hub, the worker still cannot.
- **Fully recorded** with one new field, because the runs are ordinary `worker run` calls.
- **It is not quite the reference shape.** "The specialist runs its own fleet" becomes "the specialist
  designs a fleet and the level above staffs it". Whether that difference matters is a judgment about what
  the shape is *for* — if it is for concentrating judgment while spreading volume, C does that; if it is for
  the specialist owning its subtree end to end, C does not.

Not in the task's framing, and offered because it is the only option that costs neither an invariant nor the
record.

## What is needed whatever the answer, and what is not

**The ledger cannot reconstruct a tree today.** `worker.finished` records tier, harness, model, account,
branch, unit, seconds and cost — and **no parent**. Under B or C a nested run needs a parent link or the
record of who did what is lost, exactly as the task says. Under A there is nothing to record at all, which is
the option's real cost rather than a detail.

**Depth.** The task asks how deep is too deep and answers itself — *"Two levels is the reference; three is
probably a smell."* Under B that needs enforcing (a depth counter on the run); under C it is structural,
since only an orchestrator staffs anything. Under A it is unbounded and invisible.

## Why this is not being guessed at

The loop's instruction is to ask the founder rather than guess at what only they can settle, and B in
particular rewrites a rule the founder stated in the task text itself. The measured facts above are what the
decision needs; the decision is not this orchestrator's.

`muthur ask` #18 is open with these three options. T-19 is blocked on it.
