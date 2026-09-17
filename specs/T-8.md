# T-8 — `down` only stops the hub of its own installation

> Specialist-tier task: designed and built by the orchestrator tier directly. This records what was decided and how to verify it.

## Goal

Twice in one day the organization's live hub was stopped by a build-under-test CLI that was run without its scratch
environment prefix: `down` reads the founder token from the default `MUTHUR_HOME` and the default URL is the live hub.
A slip of the keyboard must not be able to do that.

## Design

- `GET /api/v1/status` reports `serverDirectory`: the directory the server binary runs from.
- `muthur down` first reads status. If a hub is running and its `serverDirectory` is not this CLI's bundled `server/`
  directory — or the hub is too old to report one — it refuses with `not_my_hub` (exit 2) and names both directories.
  `--any` overrides. A hub that is not running is still exit 0.
- `scripts/install.ps1` normalises `-Destination` before its running-hub check, so `...\Muthur\.\bin`, relative paths
  and trailing slashes cannot bypass the guard.
- CLI-generated errors use the relaxed JSON encoder (`'` instead of `'`), like server errors.
- `ProjectService.FindAsync` orders before `Take(2)` (EF warning in the log).

## Verification

```
dotnet build && dotnet test
powershell -NoProfile -File scripts/install.ps1 -Destination ./artifacts/validate
```

**Never test this against the live hub.** Use two scratch installations:

```bash
powershell -NoProfile -File scripts/install.ps1 -Destination ./artifacts/other      # a second, "foreign" installation
S=$(mktemp -d); T="MUTHUR_HOME=$S MUTHUR_URL=http://127.0.0.1:7462"
env $T ./artifacts/other/muthur.exe up
env $T ./artifacts/validate/muthur.exe down            # exit 2, not_my_hub, message names both directories
env $T ./artifacts/other/muthur.exe status             # exit 0: still running
env $T ./artifacts/validate/muthur.exe down --any      # exit 0
env $T ./artifacts/other/muthur.exe status             # exit 4
env $T ./artifacts/validate/muthur.exe up && env $T ./artifacts/validate/muthur.exe down   # own hub: exit 0
```

- While the scratch hub runs from `./artifacts/other`: `powershell -NoProfile -File scripts/install.ps1 -Destination ./artifacts/./other`
  (odd spelling, **without** `-RestartRunning`) refuses with "A hub is running from …" and the scratch hub keeps running. Never aim this at the live directory.

## Round 2 (after win-1's failed validation)

The guard was skipped when the CLI had no bundled server — exactly the CLI `dotnet build` leaves in every worktree
(`src/Muthur.Cli/bin/Debug/net10.0/muthur.exe`) and `dotnet run --project src/Muthur.Cli`. Now `down` must positively
establish that the running hub is its own; anything else refuses: no bundled server, a status it cannot read, an older hub.

```bash
env $T ./artifacts/other/muthur.exe up
env $T src/Muthur.Cli/bin/Debug/net10.0/muthur.exe down      # exit 2 not_my_hub ("has no bundled server"); hub still up
env $T src/Muthur.Cli/bin/Debug/net10.0/muthur.exe down --any   # exit 0
```
