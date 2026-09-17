# T-5 — Validator feedback: UTF-8 CLI output, synchronous `down`, guarded install, brief fixes

> Specialist-tier task: designed and built by the orchestrator tier directly. This records what was decided and how to verify it.

## Goal

Fix what the first real validation run (win-1 on T-1) found wrong with the tooling around validation.

## Design

- `muthur.exe` sets `Console.OutputEncoding` to UTF-8 (no BOM) before writing anything, so piped JSON is valid UTF-8 on Windows.
- `muthur down` returns only after the server stopped answering (polls `status` up to 10 s; `stop_timeout` error otherwise).
- `scripts/install.ps1` refuses to replace a hub that is *running from the destination directory* unless `-RestartRunning`
  is passed; with it, the hub is stopped, replaced and started again. Installing to any other directory is unaffected.
- `kit/briefs/muthur-win-validator.md`, `kit/core/validate.md`, `kit/claude/skills/muthur-validate.md`: never export
  the scratch variables (prefix per command), always pass `-Destination`, worktrees under `.worktrees/`, how to race
  claims, browser check mandatory for live-behavior specs (otherwise blocked, not pass), prove the live hub is untouched.

## Verification

```
dotnet build && dotnet test
powershell -NoProfile -File scripts/install.ps1 -Destination ./artifacts/validate
```

Against a scratch instance (`MUTHUR_HOME`/`MUTHUR_URL` prefixed per command):
- `agent heartbeat --summary "café · ok"` piped through `xxd` shows `c3a9` and `c2b7` (UTF-8), not OEM bytes.
- `down; status` in one shell line: `status` exits 4 every time (run it 5 times).
- `powershell -File scripts/install.ps1` **with no arguments while the live hub runs** fails with
  "A hub is running from …" and the live hub keeps answering `status`. (Do not pass `-RestartRunning`.)
