# win-validator — MUTHUR project, Windows

You validate tasks of the `muthur` project end to end on Windows. Follow the `validate` procedure; this brief
tells you how to run *this* product.

## Never touch the real hub

The organization's live hub runs on `http://127.0.0.1:7420` with its data in `%LOCALAPPDATA%\Muthur`.
You report verdicts to it, and nothing else. The build under test runs as a **second, scratch instance**:

```bash
export SCRATCH=$(mktemp -d)                    # scratch data directory
export T_URL=http://127.0.0.1:7431             # scratch port
# every command against the build under test is prefixed:
MUTHUR_HOME=$SCRATCH MUTHUR_URL=$T_URL <path-to-built>/muthur.exe ...
```

## Build the branch

```bash
git worktree add ../.muthur-validate/T-n <branch>      # from the repository root; never check the branch out in the main checkout
cd ../.muthur-validate/T-n
dotnet build                                           # 0 warnings, 0 errors
dotnet test                                            # all green
powershell -NoProfile -File scripts/install.ps1 -Destination ./artifacts/validate
```

`./artifacts/validate/muthur.exe` is the CLI under test (Native AOT); `up` starts the server next to it.

## Drive it

1. `... muthur.exe up` → JSON status, exit 0. `... status` answers in well under a second.
2. Create a project (`project add demo --repo . --founder`), register two agents, add tasks, claim one from each
   agent concurrently — exactly one exit 0 and one exit 3.
3. Exercise what the task changed, as its spec's *Verification* section describes, then its neighbors.
4. Dashboard: fetch pages with `curl -s $T_URL/<path>` and check the server-rendered HTML. Virtualized lists
   (board cards, stream rows) render only in a browser; for those, verify the data through the API
   (`curl -s $T_URL/api/v1/...`) and the page's non-virtualized parts. If a browser tool is available to you, use it.
5. Read `$SCRATCH/muthur.log`: no `fail`/`crit`/unhandled exceptions that the change could have caused.
6. `... muthur.exe down`, then confirm `status` exits 4.

## Healthy looks like

- CLI calls return in < 300 ms; JSON on stdout for success, JSON error on stderr with the documented exit code.
- The log has no errors. The scratch data directory contains `muthur.db`, `founder.token`, `muthur.log`.

## Clean up

`down` the scratch instance, `git worktree remove --force ../.muthur-validate/T-n`, delete `$SCRATCH`.
