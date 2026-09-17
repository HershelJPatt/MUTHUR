# win-validator — MUTHUR project, Windows

You validate tasks of the `muthur` project end to end on Windows. Follow the `validate` procedure; this brief
tells you how to run *this* product.

## Two hubs — never confuse them

| | Live hub (the organization) | Build under test |
|---|---|---|
| CLI | `%LOCALAPPDATA%\Muthur\bin\muthur.exe` | `<worktree>/artifacts/validate/muthur.exe` |
| URL / data | `127.0.0.1:7420`, `%LOCALAPPDATA%\Muthur` | `127.0.0.1:7431`, a scratch directory |
| You use it to | take the role, read tasks, give the verdict | everything else |

**Never `export MUTHUR_HOME` or `MUTHUR_URL`.** An exported variable silently redirects the *live* CLI too.
Prefix every command against the build under test instead:

```bash
T="MUTHUR_HOME=$SCRATCH MUTHUR_URL=http://127.0.0.1:7431"     # SCRATCH=$(mktemp -d), once
env $T ./artifacts/validate/muthur.exe status
```

**Always pass `-Destination` to `scripts/install.ps1`.** Its default destination is the live hub's directory.

## Build the branch

Run from the repository root (`C:\src\Experiment`); never check the branch out in the main checkout.

```bash
git worktree add .worktrees/validate-T-n <branch>
cd .worktrees/validate-T-n
dotnet build                                           # 0 warnings, 0 errors
dotnet test                                            # all green
powershell -NoProfile -File scripts/install.ps1 -Destination ./artifacts/validate
```

## Drive it

1. `env $T ./artifacts/validate/muthur.exe up` → JSON status, exit 0; `status` answers in well under a second.
2. From the worktree root: `project add demo --repo . --founder`, register two agents
   (`agent register --name a1 --harness claude --model x`), add tasks, then race a claim:
   ```bash
   (env $T $X task claim T-1 --as-agent a1 >/dev/null 2>&1; echo "a1=$?") & \
   (env $T $X task claim T-1 --as-agent a2 >/dev/null 2>&1; echo "a2=$?") & wait      # exactly one 0, one 3
   ```
   `agent heartbeat --summary "…"` and `agent limited --minutes 45` populate the summary/limited parts of the Agents panel.
3. Exercise what the task changed, as its spec's *Verification* section describes, then its neighbors and unhappy paths
   (HTML in titles/bodies/summaries must render escaped).
4. Dashboard: `curl -s http://127.0.0.1:7431/<path>` shows the server-rendered HTML. Virtualized lists (board cards,
   stream rows) and anything "live" render only in a real browser. **If the spec requires live behavior, a browser
   check is mandatory**: use a browser tool (e.g. the `web-engine` skill). If you have none, the verdict is not
   "pass" — report blocked to the task owner (`muthur msg send --to <owner> --blocking …`) and release the role.
5. Read `$SCRATCH/muthur.log`: no warnings, errors or unhandled exceptions the change could have caused.
6. `env $T … down` returns only once the server is gone; `status` must then exit 4.

## Healthy looks like

- CLI calls return in < 300 ms; JSON on stdout (UTF-8) for success, JSON error on stderr with the documented exit code.
- The log has only Information lines. The scratch directory contains `muthur.db`, `founder.token`, `muthur.log`.

## Clean up, then prove you touched nothing

`down` the scratch instance · `git worktree remove --force .worktrees/validate-T-n` (from the repository root) ·
delete `$SCRATCH` · then on the **live** hub: `agent list` and `project list` contain nothing you created in scratch.
