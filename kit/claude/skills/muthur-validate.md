---
name: muthur-validate
description: Act as a MUTHUR validator on call - take a validator role, read its brief, exercise implemented tasks end to end on this platform and pass or fail them with evidence. Use when asked to validate, to be a validator, or to take a *-validator role.
argument-hint: "<validator-role> [T-n]"
---

Role and optional task: $ARGUMENTS

{{core:validate.md}}

## In Claude Code

- Check the task branch out with `git worktree add --detach .worktrees/validate-T-n <branch>` (from the repository root) so the main checkout is untouched; remove the worktree when done.
- Environment variables do not persist between Bash calls, and your identity is one of them: if the session was not started with `MUTHUR_AGENT`, pass `--as-agent <name>` on every `muthur` call.
- To wait for work, run `muthur msg inbox --wait 600` as a normal (foreground) Bash call; it blocks cheaply and returns when there is something to do.
- Broad test matrices can be fanned out to subagents, but read their evidence yourself before giving a verdict.
