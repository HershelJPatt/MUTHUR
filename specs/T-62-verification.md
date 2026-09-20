# T-62 resumed verification — 2026-09-20

Reviewed branch `task/T-62-brief-file-rules`, implementation commit `7192b717627710cc9de1150f168f138f8f079c69`.

The branch was ahead of the task ledger: frozen spec `be67262`, Unit A `b675fa7`, and Unit B `7192b71` already existed. Resumption retained and reviewed this work; no product code was written or rebuilt by a new implementer.

## Independent verification

- `dotnet build`: exit 0, zero warnings and errors.
- `dotnet test --no-build -v n`: exit 0; Core 3,736, Launch 41, CLI 212, Server 548; total 4,537 passed.
- Test hub cleanup: 454 removed, zero kept, zero removal failures.
- `git diff --check main...HEAD`: clean.
- FileProvenanceTests, SpecGuardTests, and DashboardConsoleRolesTests remain unmodified relative to the common base with main.
- Reviewed every product and test diff against the frozen spec, including first-match refusal, exception codes, cancellation propagation, dependency registration, and unchanged console markup.

## Installed CLI and scratch hub

Published Native AOT CLI and server using `scripts/install.ps1 -Destination ./artifacts/t62-resume`, with scratch MUTHUR_HOME and MUTHUR_URL. Install exited 0.

On an isolated repository and hub at `http://127.0.0.1:17662`:

1. Edited committed brief: `role define --brief-file ... --founder` exited 2 with `brief_file_dirty`.
2. Clean committed brief: definition exited 0 and printed Git provenance.
3. `role brief t62-validator --raw` returned the exact expected brief text.
4. HTTP GET `/console` returned 200 without `An unhandled error`.
5. Scratch hub stopped in a finally block.

The console dirty-file, containment, and first-match behavior is exercised directly through its registered BriefFileReader in the full passing suite, as required by the frozen unattended verification spec. No browser interaction is claimed.

Main has advanced independently; this verification applies to the named task implementation. Independent validation and the normal task landing gate still apply.
