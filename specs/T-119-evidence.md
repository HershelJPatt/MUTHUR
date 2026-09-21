# T-119 verification evidence

## Scope and decisions
Claimed as orchestrator-t-119. Task show contained no question/answer decisions or prerequisites. T-103 depends on this task. Frozen spec: specs/T-119.md. Product behavior, shared admission, retained reservation, permission gates and limits are unchanged. Implementation diff is confined to CapabilityProbeTests.cs.

## Baseline
Started from main cd8325fc0986acd77d54b16de6d0b41d4f735cf4 with TEMP/TMP/GIT_CEILING_DIRECTORIES=C:/WorkSrc/MUTHUR/.t119. Two uncertain-cleanup cases passed, followed by all 27 CapabilityProbeTests cases. No capability-probe directories remained. The historical lock holder remains unknown; no flake rate or live-harness recovery is claimed.

## Implementation and review
Implementer-tier dispatch used codex/gpt-6-astra and produced a8850e609defe58bce3231ffc06f57047418769f, committed by the launcher. Every changed line was reviewed. The fixture now tracks all real runner calls, refuses deletion on uncertain real ownership, observes retained production scratch, explicitly removes recorded owned worktrees through Git, and strictly deletes/asserts the fixture root. Two new cases exercise test-owned cleanup while the fake reservation remains retained. No retry or swallowed cleanup failures were added.

The first worker could not create the external short TEMP directory. The corrected continuation used a short worktree-local TEMP root, but its build was denied access to the user's NuGet.Config. It produced no source changes and ran no tests. Permissions were not expanded. Orchestrator independently ran the required checks using its authorized environment. Both worker worktrees were removed after preserving their reports/build logs in artifacts/T-119.

Integrated main 2e51fd4e835a59dc82c506d55ec9b0a20d575075 before final verification. T-101/T-102 had landed during implementation; no conflict occurred. The final diff against that main consists only of this task's spec/evidence and probe fixture.

## Completed checks
- dotnet build --disable-build-servers: exit 0, zero warnings/errors.
- Complete CapabilityProbeTests class: 29 passed, zero failed/skipped.
- Three fixed repetitions of Uncertain_cleanup/Test_owned_cleanup filter: 5/5 passed each (includes the existing reservation-runner case), no retries in the fixed repetition run.
- Initial parallel full suite: Core 3742, XmlDocCheck 64, CLI 321, Server 914 plus one explicit skip passed; Launch 138 passed and one prose-case Git commit setup timeout (exit 124). No cleanup assertion failure. Failure logs are preserved; the cause is not attributed. A separately recorded serial-project full run passed: Core 3742, XmlDocCheck 64, CLI 321, Launch 139, Server 914 plus one existing skip; total 5180 passed, zero failed, one skipped. No test or production timeout was changed. The command uses -m:1, as recorded in the revised frozen spec, and no publish/worker verification ran concurrently.
- Pinned install from a151768 succeeded. Installed verification: passed, 44 CLI/HTTP commands, zero cleanup failures.

Commands used artifacts/T-119/verify.ps1 with bounded process waits, automatic heartbeats and a short isolated TEMP root. Full output, TRX, worker reports and summary attempts are under artifacts/T-119 or test-project TestResults. Local utility summaries were attempted before large-log inspection; some timed out or were refused by the inference lock. Exact result lines were verified directly. Summary output is advisory.


## Cleanup and handoff
The final process audit found no task-owned test/scratch-hub processes or retained capability-probe directories. The checked isolated TEMP root C:/WorkSrc/MUTHUR/.t119 was removed. Cleanup evidence is artifacts/T-119/cleanup.txt. Both worker worktrees were already removed. Task worktree and ignored evidence remain available for validators.

The installed commit a151768 and final test source are identical; later commits change only task spec/evidence. git diff --check passes. Submit task/T-119-probe-cleanup for independent conductor validation; the orchestrator exits after submission and does not wait for or perform landing.
