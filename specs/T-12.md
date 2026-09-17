# T-12 — Follow-ups from the T-6 / T-7 validation

> Specialist-tier task: small fixes from win-1's non-blocking observations.

## Design

- `inbound convert` / `inbound claim --as-task` accept `--project <key>` (API: `ConvertInboundRequest.Project`): in a hub with
  several projects an item pushed without a project could never be converted.
- An inbound item's `url` must be `http`/`https` (`invalid_url`): it is rendered as a link on the Operations page.
- `project add|set --ingest` refuses a source whose scheme has no adapter (`unknown_ingest_source`) instead of failing on every poll forever.
- `worker run` checks that the spec exists on the base ref **before** creating the worktree, so a refused run leaves no orphan worktree/branch.
- The launcher's commit message says `codex/default` when the catalog names no model.

## Verification

```
dotnet build && dotnet test
```

Scratch instance with two projects `alpha`, `beta`:
- `inbound add "x" --source email --external-id m1 --as-agent a1` (run outside any repo so no project is inferred), `inbound convert I-1` → exit 2 `project_required`;
  `inbound convert I-1 --project beta` → a backlog task in `beta`. Same for `inbound claim I-2 --as-task --project alpha`.
- `inbound add "x" --url "javascript:alert(1)" …` → exit 2 `invalid_url`; an `https://` URL is accepted.
- `project set alpha --ingest bogus:thing --founder` → exit 2 `unknown_ingest_source`; `--ingest github:o/r` is accepted.
- In a throwaway git repo: `worker run --spec specs/missing.md …` → exit 2 `spec_not_committed`, and afterwards
  `git worktree list` shows no new worktree and `git branch --list 'worker/*'` is empty.
