# T-113 verification evidence

Implementation reviewed and independently verified. Independent validation and landing remain the conductor's next phase.

## Revisions and review
- Baseline main: 89dc34c.
- Task branch: task/T-113-probe-admission.
- Unit A: bd9db3c70b55c1d63a88a47df960c32e7170c153. Worker timed out; committedByLauncher=true. Reviewed all 13 changed files rather than treating that report as success.
- Unit B: 938b0142d5ef6d3646ed2132eda3ff58c8e18534. committedByLauncher=true; reviewed all three changed files. Explicit uncertain-cleanup retention and cancellation after admission were required by final review.
- Scratch installed revision: 09045ba4e1fefb07d433be5e2248f3c77aee3929. Install output explicitly identifies task/T-113-probe-admission (09045ba). Later changes are verification evidence/scripts only.
- `git diff worker/T-113-cleanup 09045ba -- src tests` is empty: final product/test trees match the independently rebuilt worker commit.
- There were no newer task decisions at claim. T-100 explicitly depends on independent T-113 admission delivery. Its unfinished capability engine was not imported.

## Independent checks
- Baseline build: PASS, 0 warnings/errors.
- Unit A `dotnet build`: PASS, 0 warnings/errors.
- Full `dotnet test -v n` on Unit A tree: PASS, 4,989 passed, 1 platform skip, 0 failed (4,990 total). Project totals: 3,742, 64, 73, 300, and 811. The skipped existing test is CensusCommandRunnerTests.Unsupported_platform_is_rejected_before_launching_a_fixture on this supported platform.
- Final Unit B product tree `dotnet build`: PASS, 0 warnings/errors. All Launch tests rerun after the isolated runner correction: 76/76 passed, including three added cases. Server, Contracts, CLI and their tests have no changes after the full-suite tree. Counts overlap; do not add the Launch rerun to the full-suite total.
- `git diff --check`: PASS.
- Installed Native AOT CLI/server publication: PASS to artifacts/T-113-installed; no live installation modified.
- Installed scratch API smoke: PASS, three fixture admissions, zero actual probe process starts, zero remaining reservations. Exercised same-run replay, full-capacity refusal, idempotent release, restart persistence, account-limit refusal, and durable daily-budget exhaustion.
- Scratch hub shutdown verified by installed CLI status returning not_running at http://127.0.0.1:17413.
- Full-suite cleanup report: 617 test hub directories removed, 0 retained for errors, 0 cleanup failures. Worker processes exited; no scratch server left running.

Logs remain in the task worktree: artifacts-T-113-build-A.log, artifacts-T-113-test-A.log, artifacts-T-113-install.log, artifacts-T-113-smoke.log; final build/Launch logs are in .worktrees/worker-T-113-cleanup. Large logs were sent through muthur utility summarize; cited outcomes were checked against original result lines. The local summary's speculation that zero actual probe starts was a failure is incorrect: the frozen spec explicitly requires fixture-only verification here.

## Reproduction
Run `dotnet build` and `dotnet test -v n`. Publish with `pwsh ./scripts/install.ps1 -Destination ./artifacts/T-113-installed`, then `pwsh ./specs/T-113-installed-smoke.ps1`. The smoke script sets distinct scratch home/URL, uses installed binaries, configures a fixture catalog only in scratch, and shuts the hub down in finally. Never point it at the live home.

## Actual-use measurement and T-100 handoff
- Window: this T-113 orchestration session, 2026-09-21 03:34–04:18 UTC.
- Cohort: T-113 deterministic verification and isolated installed API fixtures.
- Actual authorized capability-probe process starts: 0. Fixture reservations: 3. No speedup or real-use success claimed.
- Missing: a live installed T-113 revision and the first actual authorized capability-probe observation. T-100 is the existing linked integration/observation follow-up, not a reverse prerequisite.
- T-100 must use ProbeReservationRunner with ProbeAdmissionClient, honor MayExecute=false, and report ProbeCleanupUncertainException when process cleanup is unconfirmed. Unknown cleanup retains capacity; elapsed time/restart never automatically releases it. Its first actual authorized observation must name installed revision, window, cohort, sample count and missing data.

## Resumption review (2026-09-21)
- Resumed the preserved task branch at 09045ba; both implementation units were already integrated. Reviewed every changed product/test file, the final frozen spec and the saved installation evidence. No product changes were needed.
- Reconciled current T-100 task details: it depends on T-113, explicitly requests independent seam delivery, and retains production integration and actual-use measurement. No reverse prerequisite or newer founder decision applies.
- Repeated `dotnet build`: PASS, zero warnings/errors. The initial sandbox attempt could not read the existing user NuGet.Config; the approved elevated run succeeded.
- Repeated installed smoke against the preserved 09045ba binaries: PASS, three fixture admissions, zero actual probe starts, zero active reservations. The reproducible script now uses a fresh scratch home each run and explicitly checks the one-time execution grant.
- Verified scratch shutdown with installed CLI status (`not_running`) and no listener on port 17413. The repeated fixture run is a separate sample, not additional actual-use evidence.
- Local summarization was attempted before reviewing long saved evidence; the utility reported its inference lock unavailable, so targeted original result lines were verified without loading full logs.
- Final resumed `dotnet test -v n`: PASS, 4,992 passed, one existing platform skip, zero failed (4,993 total). Project totals: 3,742, 64, 76, 300 and 811. Elapsed 14:01.77; zero build warnings/errors. Cleanup: 617 removed, zero retained, zero removal failures. These totals replace the earlier pre-Unit-B full-suite totals rather than adding to them.
- Completed test log was summarized successfully; the advisory summary covered only the final project's totals. Original per-project result lines establish the complete totals above. SDK prune-data fallback messages were not test failures.
- Resume evidence logs: artifacts-T-113-resume-build.log, artifacts-T-113-resume-tests.log and artifacts-T-113-resume-smoke.log. Verification window extended through 2026-09-21 04:35 UTC. Product/test trees remain identical to the installed 09045ba revision; only evidence and its reproduction script were added.
