# T-7 — Ingest: inbound items, GitHub issues polling with cursors, push API, claim/convert/dismiss

> Specialist-tier task: designed and built by the orchestrator tier directly. Design: `docs/PLAN.md` (M7).

## Design (as built)

- **Pull, never webhooks.** The hub is a local process that is off for hours. Each project lists sources
  (`muthur project set <key> --ingest github:owner/repo`); the hub polls them on start and every
  `Muthur:IngestIntervalSeconds` (180) and stores a cursor per source, so whatever happened while it was off is caught up.
- A failing source keeps its cursor; the failure shows in `muthur inbound sources` (`lastError`) and is recorded once
  (`ingest.failing` … `ingest.recovered`), not on every pass.
- `github:` reads open issues through the GitHub CLI (`gh api`), which already holds the user's authentication. Pull requests are skipped.
  Other sources are one class each (`IInboundSource`); Azure DevOps work items is the next adapter.
- **Push API** for everything else: `muthur inbound add "<title>" --source email --external-id <id> …` (idempotent on source + external id).
- Items are `I-n`: `inbound list|show|claim [--as-task]|convert|dismiss --reason`. Claim is a race with one winner (exit 3 for the loser);
  a claimed item is handled by its claimer or the founder. Converting creates a backlog task that links back to the source.
- When the `comms-oncall` role exists, the hub messages it about new items — so an on-call agent waiting on `msg inbox --wait` wakes up.

## Verification

```
dotnet build && dotnet test      # IngestTests covers cursors, catch-up, de-duplication, failure/recovery, claim race, convert, dismiss, parsing
```

Against a scratch instance:
- `inbound add "Crash" --source email --external-id m1 --as-agent a1` twice → one item `I-1`.
- Race `inbound claim I-1` from two agents → one exit 0, one exit 3. `inbound claim I-1 --as-task` (winner) → `task list` shows the task in backlog with the source link in its body.
- `inbound dismiss I-2` without `--reason` is a usage error; with a blank reason → exit 2.
- Define role `comms-oncall`, take it as `a2`, start `msg inbox --wait 60` in the background, `inbound add …` as `a1` → the wait returns at once naming the new `I-n`.
- `project set demo --ingest github:<some public repo with open issues> --founder`, then `inbound poll --founder`:
  with `gh` authenticated, its open issues arrive as inbound items and `inbound sources` shows a cursor; without it,
  `inbound sources` shows the `gh` error in `lastError`, nothing is lost, and the hub log has a warning, not an exception.
- Stop the hub, restart it: no duplicates appear after the start-up poll.
