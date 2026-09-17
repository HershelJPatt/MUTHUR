---
name: muthur-orchestrate
description: Run the MUTHUR mastermind-orchestrator loop on a task - claim it, become the expert, write the frozen spec, delegate to implementer subagents in worktrees, review, hand to validation, land. Use when asked to orchestrate, to work the MUTHUR backlog, or to take a task (T-n) through MUTHUR.
argument-hint: "[T-n | next]"
---

Task requested: $ARGUMENTS (if empty or "next", take the most urgent backlog task).

{{core:orchestrate.md}}

## In Claude Code

- **Delegation** uses the Agent tool: `muthur-implementer` (Opus, frozen spec) or `muthur-specialist`
  (your own tier, for risky units). Each runs in its own git worktree, created from your current HEAD — so
  create and check out `task/T-n-<slug>` and commit the spec **before** you spawn anyone. Launch independent
  units in a single message so they run in parallel.
- An implementer's report names its branch. Integrate with `git merge --no-ff <branch>` **into the task
  branch only**. Merging into the project's default branch is MUTHUR's job (`muthur task land`).
- **Cross-harness workers:** `muthur worker run --tier implementer --spec specs/T-n.md --unit "<unit>" --task T-n` runs the unit
  headless on whichever harness and account the tier has available (another Claude, Codex, a local model) in its own
  worktree, and returns the report and branch. Use it when your own account is near its limit, to get a second
  vendor's take on a unit, or for utility-tier chores. Run it as a background Bash call for long units.
- Subagents cannot spawn subagents. For a unit too big for one specialist, split it further yourself or make it
  its own ledger task for another orchestrator session.
- The prompt you give a subagent is everything it knows. Always include: the spec path, the unit name, the
  verification commands, and the report format reminder.
- Your identity comes from the `MUTHUR_AGENT` environment variable the session was started with.
