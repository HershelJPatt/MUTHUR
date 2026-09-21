---
name: muthur-orchestrate
description: Run the MUTHUR mastermind-orchestrator loop on a task - claim it, become the expert, write the frozen spec, delegate to implementer subagents in worktrees, review, hand to validation, land. Use when asked to orchestrate, to work the MUTHUR backlog, or to take a task (T-n) through MUTHUR.
argument-hint: "[T-n | next]"
---

Task requested: $ARGUMENTS (if empty or "next", take the most urgent backlog task).

{{core:orchestrate.md}}

## In Claude Code

- **Native isolation:** delegation uses the Agent tool: `muthur-implementer` (Opus, frozen spec) or
  `muthur-specialist` (your own tier, for risky units). Retain `isolation: worktree` for both agents.
  The harness allocates the path and output branch; prompts cannot assign a prepared filesystem path.
  Each worktree is cut from the repository's HEAD, **not the branch you are standing on**. Commit the spec
  on the named base before dispatch. Pass the relative spec path, unit, named local base branch, full
  dispatch SHA, named default branch and verification commands, not a prepared absolute worktree.
  Agents discover `git rev-parse --show-toplevel` and `git branch --show-current`, stay there, and report
  WORKTREE and BRANCH. Launch independent units in a single message so they run in parallel.
- **Worker launcher:** prefer this mode when deterministic output branch placement is required:
  `muthur worker run --tier implementer --spec specs/T-n.md --unit "<unit>" --task T-n --base task/T-n-<slug> --branch task/T-n-<unit-slug> --note "Base branch task/T-n-<slug>; dispatch SHA <full-commit-sha>; default branch main."`
  Replace placeholders and `main` with actual assignment values. The frozen base branch is distinct from
  the output branch. The output branch must not already exist; do not pre-create its worktree.
  The launcher creates a fresh worktree and reports its actual path and branch. Include all three base
  inputs in `--note`: the current WorkerPrompt does not automatically supply them. Use `--tier mastermind` for
  specialist work under the existing rules. This also supports cross-harness staffing when your account
  is near its limit or another vendor's review is useful. Run it as a background Bash call for long units.
- Neither mode adopts an already-prepared worktree. Do not call EnterWorktree or write through a prepared
  sibling path to repair assignment. A contradictory explicit path assignment returns `STATUS: blocked`
  before writes, with expected/actual root and branch; redispatch via `muthur worker run`.
  Do not copy uncommitted output by hand between trees as normal integration.
- Check reported WORKTREE and BRANCH against the native allocation or launcher report before integrating
  committed branch output with `git merge --no-ff <branch>` **into the task branch only**. Merging into
  the project's default branch is MUTHUR's job (`muthur task land`).
- MUTHUR does not select the native Agent worktree start point; the harness owns that creation. Both modes
  retain the implementer's base/SHA/clean-tree guards and history recovery rules. A moved base requires
  an updated frozen redispatch. The worker launcher creates a fresh tree from the explicit base.
  It does not repair arbitrary dirty or divergent existing worktrees.
- Subagents cannot spawn subagents. For a unit too big for one specialist, split it further yourself or make it
  its own ledger task for another orchestrator session.
- The prompt you give a subagent is everything it knows. Always include: the spec path, the unit name, the
  named local base branch, full commit SHA at dispatch, named default branch, verification commands, and
  the report format reminder. A moved base requires `STATUS: blocked` and an updated frozen redispatch.
- Your identity comes from the `MUTHUR_AGENT` environment variable the session was started with.

### Native Agent-tool permissions

Native Agent-tool success depends on the initiating harness permission mode and approval availability.
MUTHUR does not set, record or observe that native mode, or ship a native Agent-tool allowlist.
An unattended denial is an observed configuration outcome, not proof that delegation universally fails.
Read-only success and static settings inspection do not establish build, test or commit capability.
When this gap occurs, record the commands actually attempted and their approval/denial outcomes, report
blocked verification honestly, and preserve artifacts. Do not widen permissions to make a unit pass.
The worker launcher is a separate path; its success does not prove native Agent-tool behavior.

See [docs/claude-agent-permissions.md](../../../docs/claude-agent-permissions.md)
(a repository-root path; installed skill paths differ) for the
historical evidence and the deferred T-97 measurement after T-67 lands. T-67's own required Claude probe
remains mandatory; documentation verification is not native Agent-tool measurement evidence.
