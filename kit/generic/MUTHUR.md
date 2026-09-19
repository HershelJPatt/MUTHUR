# Working in this repository with MUTHUR

Give this file to any agent harness as (part of) its system prompt or project instructions.

This repository is worked by an organization of agents coordinated through MUTHUR (`muthur` CLI; JSON output;
exit codes 0 ok, 2 rule violation, 3 conflict, 4 hub not running, 5 not found, 6 unauthorized). The only requirement
on a harness is that it can run shell commands.

- To **orchestrate** a task: follow `.muthur/procedures/orchestrate.md`.
- To **validate**: `.muthur/procedures/validate.md`. To hold any other **role**: `.muthur/procedures/oncall.md`.
- As a **worker** on a frozen spec: `.muthur/procedures/implementer.md` is your whole contract; workers never run `muthur`.
- On a **problem area** rather than a frozen unit, because the judgment is expensive:
  `.muthur/procedures/specialist.md` adds to that contract; you design your subtree, you do not staff it.

Identity: start the session with `MUTHUR_AGENT=<name>` in its environment, or pass `--as-agent <name>` on every call.
Delegation: `muthur worker run --tier implementer --spec specs/T-n.md --unit "<unit>" --task T-n` runs a worker on
whichever harness and account the tier has available, in its own worktree, and returns its report and branch.
Project build/test/run commands are in `muthur.project.json`.
