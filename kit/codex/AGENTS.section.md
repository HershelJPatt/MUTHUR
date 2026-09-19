## MUTHUR

This repository is worked by an organization of agents coordinated through MUTHUR (`muthur` CLI; JSON output;
exit codes 0 ok, 2 rule violation, 3 conflict, 4 hub not running, 5 not found, 6 unauthorized).

- Asked to **orchestrate**, work the backlog, or take a task (`T-n`) through MUTHUR → read and follow `.muthur/procedures/orchestrate.md`.
- Asked to **validate** or take a `*-validator` role → `.muthur/procedures/validate.md`.
- Asked to **go on call** or hold any other role → `.muthur/procedures/oncall.md`.
- Given a frozen spec and a worktree as a **worker** → `.muthur/procedures/implementer.md` is your whole contract.
  Workers never run `muthur`, never push, never merge.
- Given a **problem area** rather than a frozen unit, because the judgment is expensive →
  `.muthur/procedures/specialist.md` adds to the implementer contract; you design your subtree, you do not staff it.

In Codex:
- Your identity is the `MUTHUR_AGENT` environment variable the session was started with; if it is not set, pass
  `--as-agent <name>` on every `muthur` call.
- **Delegate with** `muthur worker run --tier implementer --spec specs/T-n.md --unit "<unit>" --task T-n`. It creates the
  worker's worktree and branch from your current branch, staffs the tier with whichever harness and account is
  available, and prints the worker's report with its branch. Commit the spec before delegating. Run independent units in parallel.
- Integrate worker branches into the task branch only (`git merge --no-ff <branch>`). Landing on the default
  branch is MUTHUR's job: `muthur task land T-n`.
- Wait for work with `muthur msg inbox --wait 600`; it blocks without using tokens.
- Project build/test/run commands are in `muthur.project.json`.
