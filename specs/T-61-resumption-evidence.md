# T-61 resumption evidence — 2026-09-20

Owner: `orchestrator-t-61`. Product commit reviewed and verified: `ba48567`.

The ledger's last implementation was `f3b7ed6`, rejected because an exclusively locked
`harnesses.json` escaped `HarnessService.ReadCatalog`, removed every doctor agent row,
and also broke `/operations`. The task branch had already advanced to `1872fa6`, which
froze Amendment 2 / Unit B. Both the task tree and the prior Unit B worktree were clean;
the repair had not been implemented. This session continued that frozen amendment.

## Delegation and review

- Used `muthur worker run --tier implementer`, from the existing task branch.
- First worker stopped cleanly without edits for base clarification. The second was
  explicitly given `task/T-61-harness-typo@1872fa6` as its intended base, with `main`
  only the default comparison branch.
- Second worker used codex/gpt-6-astra and returned `ba48567`. Its report had
  `committedByLauncher: true`; the complete actual diff was reviewed independently.
- Exactly the five Unit B files changed. Catalog creation is inside the try;
  filesystem IO/access failures become `catalog_unreadable`; doctor retains its
  existing MuthurException catch; the three specified regressions are present.
- Fast-forwarded the task branch to the reviewed commit. No product code was written
  by the orchestrator. No push or merge into the default branch was performed.

## Independent owner verification

Executed by `artifacts/t61-resume/verify.ps1`, with deterministic process waits,
timeouts and heartbeats. Logs are under `artifacts/t61-resume/` in the task worktree.

- `dotnet build`: exit 0; zero warnings, zero errors.
- `dotnet test --no-build --logger 'trx;LogFilePrefix=t61' --results-directory artifacts/t61-resume/test-results`:
  exit 0; Launch 35, Core 3,736, CLI 213, Server 521. **4,505 passed, zero failed,
  zero skipped.** Counts verified directly from the four TRX files.
- `pwsh -NoProfile -File scripts/install.ps1 -Destination ./artifacts/t61`: exit 0;
  Native AOT install provenance explicitly identifies task branch commit `ba48567`.
- Installed CLI exercised against a fresh isolated hub at `http://127.0.0.1:17663`,
  with scratch `MUTHUR_HOME` set only in child process environments. Inherited agent,
  token, server and kit overrides were removed from those children.
- `corner/cladue` registration succeeds; baseline doctor exits 1, reports corner
  fail and straight/claude OK, lists `claude, codex, codex-oss, generic`, and has no
  source warning in the installed layout.
- Holding the catalog with `FileShare.None`: doctor exits 0, emits one catalog
  warning naming its path, preserves both agent rows, reports corner warn and
  straight OK, and emits no category=doctor failure.
- Under the same lock, `/operations` returns HTTP 200 and displays `could not be read`.
- Doctor/dashboard reads do not alter the scratch ledger.
- Releasing the lock restores corner's fail verdict without a restart.
- Scratch hub stopped successfully in `finally`; both session-created worker
  worktrees were clean and removed after their commits were integrated.

This is owner verification, not an independent validator verdict. The task must
pass its normal validation gate before `muthur task land`.
