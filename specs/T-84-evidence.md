# T-84 implementation evidence

Task branch: task/T-84-spec-containment. Implementation commit 5391ae1; verifier-only correction 33246ce. Frozen design and measured-platform correction are committed alongside them.

## Independent checks
- `dotnet build --nologo`: exit 0, zero warnings/errors.
- Full `dotnet test --no-build --no-restore --blame-hang-timeout 2m --blame-hang-dump-type none --logger "console;verbosity=minimal"`: exit 0. Core 3736, Launch 54, CLI 270, Server 666: 4726 passed, zero failed/skipped.
- `pwsh scripts/install.ps1 -Destination ./artifacts/t84-installed`: exit 0; Native AOT CLI and server published from 5b04675.
- `pwsh scripts/verify-t84.ps1 -CliPath ./artifacts/t84-installed/muthur.exe`: exit 0, 49 assertions. Ordinary/inward/linked-root acceptance, outward and target-ancestor junction refusal, unchanged attachment/attended reason/task state/ledger events, and cleanup all passed.
- `git diff --check main...HEAD`: passed.
- Product and test source are byte-identical to the independently built/tested 5b04675 tree; later changes concern only specification and installed-verifier null-property handling.

The first installed run correctly rejected the outward junction but the script failed reading an omitted null JSON field under StrictMode. An implementer corrected optional-property reads without weakening assertions; the second run passed. Both runs stopped their scratch hub and removed scratch data. No task-owned Muthur.Server/testhost/dotnet process was found in the final process audit.

The implementer ran on the implementer tier (codex/gpt-6-astra), not the disabled local-implementer tier. Launcher committed both implementation outputs; every diff was reviewed by the orchestrator. Initial worker stopped without changes because its generated contract required an explicitly named base in the launch note; the relaunch supplied it. Unit B attempted redundant build/tests and met sandbox NuGet.Config denial; the orchestrator's independent full build/tests had already passed and the correction changed no product code.

Windows junction coverage was mandatory and passed. The implementer reported file-symlink privilege unavailable for its optional fixture. No Linux/macOS runtime results are claimed. Hard links and concurrent filesystem replacement between resolution and read remain explicit non-goals.

Logs and reports are retained in C:/WorkSrc/MUTHUR/.work/T-84-evidence. Scratch runtime directories and delegated worktrees are removed before submission. The conductor owns validation and landing after this session exits.
