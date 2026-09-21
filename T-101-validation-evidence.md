# T-101 owner verification evidence

Tested product revision: `80b1b3b16ee25aaf2325a3207446c8c6981aae3e`, branch `task/T-101-checkpoints`, frozen spec revisions 1–4 in `specs/T-101.md`. Any subsequent evidence-only commit changes this report, not the tested product. Independent task validation remains required; this report is owner verification, not a validator verdict.

## Resumption and review

The replacement owner claimed T-101 and read its complete task record and ledger before writing. Branch `a28d5a3` was ahead of the ledger. Interrupted worker output was preserved exactly at `493afbf`, reviewed, and integrated instead of discarded. Later implementer output fixed Windows reserved-branch case aliases (`e7a1bdf`) and reused successful immutable Git probes only within one request (`80b1b3b`). The owner authored specs/review evidence, not product code. The initial resumed worker had sandbox build failures; the alias worker timed out and its reviewed output was committed by the launcher. Those worker outcomes are not presented as passing verification. The final implementer build and 52 targeted tests passed; its output was also committed by the launcher and independently reviewed.

Owner review covered every product/test/script diff: transaction/event atomicity, owner/CAS/attempt guards, full Git object/path validation, current dependency proofs, independent-unit preservation, bounded advisory context, legacy task behavior, and unchanged task validation gates. Branch HEADs are never cached. A new checkpoint creates a new proof helper and rechecks artifacts; failed/timed-out/cancelled probes are not reused.

## Completed installed checks

- `dotnet build --nologo`: passed, zero warnings/errors.
- `pwsh -NoProfile -File scripts/install.ps1 -Destination ./artifacts/t101-final-install`: passed; installation log pins `80b1b3b`.
- `pwsh -NoProfile -File scripts/verify-work-unit-checkpoints.ps1 -CliPath ./artifacts/t101-final-install/muthur.exe -Evidence ./artifacts/t101-final-evidence`: passed.
- `pwsh -NoProfile -File scripts/verify-throughput.ps1 -CliPath ./artifacts/t101-final-install/muthur.exe -Evidence ./artifacts/t101-final-throughput-evidence`: passed.
- `git diff --check 1b36ca3..80b1b3b`: passed.

Owner command logs are retained under `C:/WorkSrc/MUTHUR/.work/T-101/final-owner-*.log`; deterministic invocation wrapper is `run-final-owner-check.ps1` in that directory. All build/test/install commands used scratch MUTHUR_HOME/MUTHUR_URL, and the installed fixtures chose distinct scratch homes/repos/ports. The live installation was not modified.

## Controlled handoff measurement

Cohort: one synthetic three-unit task, one controlled owner exit and hub restart, on product revision `80b1b3b`; sample count 1. Replacement-owner timing window: 2026-09-21 05:38:14.3725663Z to 05:38:38.1280451Z. The fixture defines useful action as completed reconciliation of both reusable outputs, starting before replacement registration/claim. Observed elapsed time: 23.7547788 seconds. Reused units: A and B (same attempt IDs/output commits); only C was started by the replacement. Unnecessarily rerun units: 0. Repeated verification attestations: 0. Source ledger confirms exactly three starts and three verification events, with one start by owner-after. Reconciliation does recheck Git proof; zero repeated attestations does not mean zero proof reads.

Actual fixture process starts: two hubs and two owner processes. The checkpoint fixture launches no model workers and makes no staffing attempts or inference calls. Git/CLI helper processes are separately recorded in `processes.json`, `before-processes.json` and `after-processes.json`; do not add those counts to owner/hub counts as throughput outcomes. This is not a controlled speedup comparison and no throughput speedup is claimed. Waiting/staffing economics and a live installed handoff are not measured here.

Installed CLI SHA-256: `00EAB881A3C662958800A503809B84E8780BAC6606A4632574E1CB21DF298B93`.

Complete source evidence is retained in `artifacts/t101-final-evidence`: stopped SQLite ledger, Git repository, before/reused/after graphs, handoff ledger, transition JSON and CLI outputs, full events, race responses, timing, process records and `result.json`. Concurrent CAS produced one HTTP 200 and one 409 checkpoint_conflict, and exactly one mutation. The fixture additionally passed commit-before-report recovery, merge-before-integration-record recovery, stale attempt rejection, dependency invalidation with independent preservation, lost evidence, missing branch, divergent base, stale spec and mandatory independent validation after implementation. Final checkpoint-fixture hub/owner PIDs 36264, 67608, 48384 and 47824 were all confirmed stopped.

The earlier candidate `493afbf` failed the checkpoint fixture at unit C report: `artifacts/t101-evidence/after-14-checkpoint.json`, `hub-2.stdout.txt` (HTTP 499 after 30029.3552ms), and `.work/T-101/owner-fixture.log`. Those artifacts remain failure evidence. The request-local cache correction retained client deadlines and proof requirements; the fresh final fixture passed. The earlier baseline full suite passed 4993 tests, skipped one existing platform-specific census case, and reported 623 removed test hubs, zero retained/failed cleanup; that baseline is not substituted for the final suite.

## Preserved boundaries and follow-up

T-95 is landed. T-88 founder decision #41 keeps protected agent-definition application attended; no protected definition, permission, account, budget or outbound changes were made. T-67/#32 and T-97 behavioral-probe requirements remain unchanged. Real installed handoff pilot T-114 remains dependent on T-101 and is not completed by this synthetic fixture. The conductor performs independent validation and landing after submission and owner exit; nothing was pushed or merged into the default branch.

## Final full-suite and cleanup result

`dotnet test --no-build --logger "console;verbosity=normal"` passed on tested product revision `80b1b3b`: 5009 passed, 0 failed, 1 skipped. Assembly pass counts: Core 3742, CLI 304, Launch 65, XmlDocCheck 64, Server 834. The sole skip is the existing platform-specific `Muthur.Server.Tests.CensusCommandRunnerTests.Unsupported_platform_is_rejected_before_launching_a_fixture`. Server suite elapsed 11.8140 minutes. Source: `.work/T-101/final-owner-test.log`.

Final suite cleanup: 627 removed test hubs, 0 kept, 0 could not be removed. Process inspection after installed fixtures also found no surviving T-101 implementer or installed-fixture server processes. All check commands have exited. Full check logs, failed-run evidence and final source artifacts are retained for review; no scratch service requires manual cleanup.

Final full-suite log SHA-256: 14677233BAD7B3D4E95A631877AA809E5F1A96FA625F04A03F6305D2BC379E61.

