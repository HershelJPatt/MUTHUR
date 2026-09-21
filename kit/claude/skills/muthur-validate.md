---
name: muthur-validate
description: Act as a MUTHUR validator on call - take a validator role, read its brief, exercise implemented tasks end to end on this platform and pass or fail them with evidence. Use when asked to validate, to be a validator, or to take a *-validator role.
argument-hint: "<validator-role> [T-n]"
---

Role and optional task: $ARGUMENTS

{{core:validate.md}}

## In Claude Code

- Retain the claim's `currentSubject.id` and inspect its exact `currentSubject.implementationSha` with `git worktree add --detach .worktrees/validate-T-n <implementation-sha>`; remove the worktree when done. Every verdict uses `--subject <retained-guid>` and `--evidence-file <UTF-8 report>` or `--evidence <report>`.
- Environment variables do not persist between Bash calls, and your identity is one of them: if the session was not started with `MUTHUR_AGENT`, pass `--as-agent <name>` on every `muthur` call.
- To wait for work, run `muthur msg inbox --wait 600` as a normal (foreground) Bash call; it blocks cheaply and returns when there is something to do.
- Broad test matrices can be fanned out to subagents, but read their evidence yourself before giving a verdict.
