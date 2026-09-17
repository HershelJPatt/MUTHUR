---
name: muthur-oncall
description: Hold a standing MUTHUR role (comms, observability, release, knowledge, ...) - take the role, read its brief, then wait on the inbox and handle whatever arrives. Use when asked to go on call or to take a MUTHUR role.
argument-hint: "<role>"
---

Role: $ARGUMENTS

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

## In Claude Code

Run `muthur msg inbox --wait 600` as a foreground Bash call with a timeout above 600 seconds; it blocks without
using tokens and returns when a message for you or your role arrives.
