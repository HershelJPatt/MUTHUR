# On call

You hold a standing **role** in the organization (comms, observability, release, knowledge, a validator…).
A role is a responsibility, not a task: you hold it, do what its brief says whenever something arrives,
and release it when your session ends.

## The loop

1. `muthur role take <role>` — exit 3 means another agent already holds it; stop, don't fight for it.
2. `muthur role brief <role>` — the brief is your job description. Read all of it. It is versioned in the hub,
   so re-read it when you take the role even if you held it before.
3. Wait without burning tokens: `muthur msg inbox --wait 600`. It returns as soon as a message for you or your
   role arrives (or after the timeout, with nothing). Every call renews your hold on the role.
4. Handle what arrived, the way the brief says. Typical outcomes:
   - Unclaimed inbound item → `muthur inbound list`, then `muthur inbound claim <id> --as-task` to turn it into a ledger task (or reply through `muthur out`).
   - A question from another agent → answer with `muthur msg send --to <agent> "..."`.
   - Something needs a founder → `muthur ask ...`. Never decide a founder-level question yourself.
5. `muthur agent heartbeat --summary "<one line on what you're watching>"`, then back to step 3.

When you stop: `muthur role release <role>`.

## Rules

- You may hold more than one role, but only roles you can actually serve.
- Real work that results from your role goes into the ledger as a task; you claim it or leave it for an orchestrator — you don't do large work invisibly inside the role.
- Everything leaving the machine goes through `muthur out` and another agent's review.

## Shared incident triage

At intake or after a failed run, explicitly run `muthur incident match --project <key>
--signature <text> --path <text> --configuration <text>` using the observed identifiers.
This is read-only and advisory. Inspect candidates with `muthur incident show I-n`,
including diagnosis, retained evidence, workaround authorization and recovery condition.
Equal tuples can belong to different incidents; matching never proves a common cause.

If the facts support grouping, append independent evidence with `muthur incident
observe I-n --task T-n --evidence <text> --signature <text> --path <text>
--configuration <text> [--run <id>]`. Differing observations may also be retained.
An observation never suppresses work. Suppression requires a confirmed or mitigated
incident, active evidence of the exact current condition and an explicit task/assignment
pair (`#orchestrator` or one validator role). Only the founder or task owner may change
links/suppression; identified agents may link unowned tasks. Correct grouping with
`incident unlink` and a reason, retaining history.

`incident recover --kind probe` attests a successful bounded probe for this exact
condition; it does not execute one. Configuration recovery records a measured change.
Both need evidence, advance the condition version and release only this incident gate.
Fresh observations are required to rearm suppression. Dependencies, human requests,
budgets, attended flags, holds and terminal states remain independent. Workarounds are
documentation, never capability grants. Do not periodically retry unchanged suppression.
