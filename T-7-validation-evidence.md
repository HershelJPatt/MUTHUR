# T-7 review and verification

Reviewed implementation head: `23b20229ba67df9b285814ed32c03a79a4894aa4` on `task/T-7-census`. This evidence-only commit changes no implementation or verification commands.

The resumed branch already contained the census vertical slice and frozen process-ownership amendment. Interrupted implementer changes were preserved at `111fb5c`; the orchestrator did not write product code. The launcher committed correction unit C at `23b2022`, and its complete diff was reviewed before fast-forward integration.

Review confirmed strict configuration/snapshot/cursor validation; ordinal stable incident identities; unchanged inbound snapshots for active findings; recurrence only after successful absence; atomic cursor/inbound/completion-event persistence; safe failure categories; existing doctor and triage behavior; no new scheduler or paid worker launch. Windows ownership begins with suspended creation and job assignment before resume, and native tests exercise cleanup after parent exit. Execution is intentionally Windows-only, as documented in the frozen amendment and README.

## Completed checks

- `dotnet build --disable-build-servers`: passed, zero warnings/errors.
- Broad `dotnet test --no-build -v n --disable-build-servers`: three non-server projects passed (3,736 + 54 + 272 = 4,062 tests). Its supervisor reached the 12-minute budget before the server project completed; this is not reported as a successful single full-solution run.
- `dotnet test tests/Muthur.Server.Tests/Muthur.Server.Tests.csproj --no-build -v n --disable-build-servers`: passed independently in 6m16s, 781 passed, 1 intentional platform skip. Together the four projects completed with **4,843 passed and 1 skipped**.
- Focused native runner check, `dotnet test tests/Muthur.Server.Tests/Muthur.Server.Tests.csproj --no-build --filter FullyQualifiedName~CensusCommandRunnerTests -v n`: passed, 10 passed, 1 non-Windows test skipped on Windows. Includes argument quoting, stdin EOF, environment scrubbing, output caps, timeout, cancellation, and exited-parent descendant cleanup.
- `pwsh -NoProfile -File scripts/install.ps1 -Destination ./artifacts/T-7-verify`: passed, published from `23b2022`.
- `pwsh -NoProfile -File scripts/verify-census.ps1 -InstallPath ./artifacts/T-7-verify`: passed, **all 93 assertions**. The committed script runs directly, with no alias or PATH workarounds. Proves deduplication, conversion, changed text, recurrence, failure atomicity, doctor recovery, completion events and no paid launches against installed binaries in a disposable hub.
- `git diff --check 6835d98 HEAD`: passed.

The installed verification exposed and the implementer corrected two script defects: the built-in `cli` alias shadowed its helper, and multiple `Get-Command pwsh` matches were converted into a single invalid executable name. No native runner change was needed for that diagnostic; a direct native startup probe succeeded.

## Cleanup and provenance

The successful server run reported `590 removed, 0 kept (logged an error), 0 could not be removed`. Three closed temporary test directories from interrupted T-7 runs were attributed through their content-root log markers, checked for containment and open handles, and removed. The disposable native diagnostic project was removed. Final process audit found no T-7 scratch hub, test host or census fixture process. No live census source was configured, no default-branch merge was performed, and nothing was pushed.

Detailed local evidence is under `C:/WorkSrc/MUTHUR/.work/T-7-resume-*.log`, `T-7-native-tests.log`, and `T-7-smoke-worker.json`. Large logs were routed through `muthur utility summarize`, with exact result lines checked separately; summaries are advisory. The interrupted duplicate implementer test run was stopped before the focused script correction was dispatched, preserving the existing implementation.
