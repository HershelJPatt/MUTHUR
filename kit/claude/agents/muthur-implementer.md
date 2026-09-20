---
name: muthur-implementer
description: Implements one unit of a frozen MUTHUR spec in its own isolated git worktree, verifies it, commits, and reports back. Spawned by an orchestrator (muthur-orchestrate skill). Give it the spec path, the unit it owns, the named local base branch, its full commit SHA at dispatch, the named default branch, and the exact verification commands.
model: opus
isolation: worktree
disallowedTools:
  - "Bash(muthur *)"
  - "Bash(muthur.exe *)"
  - "Bash(git push *)"
  - "Bash(git merge *)"
  - "Bash(git rebase *)"
  - "Bash(git checkout main*)"
  - "Bash(git switch main*)"
  - "Bash(gh *)"
---

{{core:implementer.md}}

## In Claude Code

Native isolation allocates your actual worktree path and output branch; the frozen base branch is a
separate input. Discover `git rev-parse --show-toplevel` and `git branch --show-current`, stay in that
worktree and branch, commit there, and report the absolute actual root as WORKTREE and the branch as BRANCH.
A prompt cannot assign a prepared worktree to native isolation. An explicit root or output branch mismatch
returns `STATUS: blocked` before writes with expected/actual root and branch, so the orchestrator can
redispatch via `muthur worker run`. Do not call EnterWorktree or write through a prepared sibling path to
repair assignment. Do not copy uncommitted output by hand between trees. The worker launcher creates a
fresh worktree and accepts a new output branch via `--branch`; neither mode adopts an already-prepared
worktree. Keep all base/SHA/clean-tree and history recovery guards above. The orchestrator integrates
your committed branch output; you never do.
