# T-78 verification

Implementation reviewed: `d3f5c77` on `task/T-78-harness-case`.

Registration now trims harness whitespace without folding case. CLI help, RegisterAgentRequest XML documentation and README explain the rule. Existing stored rows and historical events are not migrated; authorized re-registration corrects previously folded identifiers. Doctor retains its existing ordinal matching and registration remains open to unknown nonblank harnesses.

The frozen spec was committed before delegation. The implementer-tier worker ran on codex/gpt-6-astra. Its first dispatch stopped without edits on repository ownership; the second used task-scoped safe.directory configuration and the full base SHA. The launcher committed its output (`committedByLauncher: true`). The orchestrator read every final diff, checked the shared registration path and mapping, and integrated only into the task branch.

Independent checks:
- `dotnet build --disable-build-servers`: passed, 0 warnings, 0 errors.
- `dotnet test --no-build --blame-hang-timeout 3m --logger trx`: passed all 4,763 tests, 0 failures, 0 skips. TRX counters independently verified: Core 3,736; Launch 54; CLI 272; Server 701. Server suite duration 8m18s.
- `pwsh -NoProfile -File scripts/install.ps1 -Destination ./artifacts/t78`: passed. Published provenance identifies task/T-78-harness-case at d3f5c77.
- `pwsh -NoProfile -File scripts/verify-T-78.ps1 -CliPath ./artifacts/t78/muthur.exe`: passed all 21 assertions, including help, whitespace trimming/case preservation, roster, matching kit/catalog doctor success, mismatch doctor failure, re-registration, and scratch shutdown/removal.
- `git diff --check`: passed.

The first independent test run used TEMP beneath the repository. Two pre-existing tests requiring a file outside every repository correctly found the enclosing repository instead of null provenance. The core (3,736) and server (701) suites passed. This verification setup error was corrected by moving TEMP to a dedicated directory outside the repository and rerunning the complete suite with separate TRX results; no product changes were made for it.

Scratch cleanup: owned worker and test processes exited; the external test scratch directory was removed after a graceful compiler-server shutdown released its analyzer DLLs. The scratch hub was also independently confirmed absent. Temporary installation, task checkout and orchestrator logs are removed before submission. Worker worktrees and integrated worker branches have already been removed. The installed-CLI script verified its owned hub PID/data directory, stopped it in finally and removed its unique scratch directory. No live hub was used for product checks.

The orchestrator hands the task to validation and exits; the conductor owns validation and landing. No push or default-branch merge was performed.

