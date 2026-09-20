# T-83 resumption and review evidence

Resumed 2026-09-20 as orchestrator-t-83. The previous exit report was stale: task/T-83-hardlink-guard at 1b5c9afe881ab90677fdaebceab1f87f8c68078f already contained the frozen spec, implementation, Amendment 1, tests, and an unimplemented Amendment 2. Request 40 was approved in event 4395. The first resumption heartbeat recorded the branch as authoritative.

## Review

Reviewed every product/test/verifier diff against base 6cf2fb3, including the launcher-committed worker output at 789fd7f. Existing write intent drives a full preflight; unsafe counts and inspection failures refuse before writes; native handles are disposed; platform selection is explicit; transaction forward/rollback mechanics are unchanged. Reviewed native declarations against the Microsoft BY_HANDLE_FILE_INFORMATION documentation, Linux statx UAPI, and Darwin stat header linked in the spec. No source text was copied.

The resumed UTF-8 implementer owns only Amendment 2. An initial launch made no edits because Git rejected the sandbox account's repository ownership; redispatch supplied the full base SHA and per-command safe.directory configuration. The reviewed correction explicitly selects UTF-8 for both child output streams.

## Executed checks

- Orchestrator build: succeeded, zero warnings/errors. Source implementation at 1b5c9af; subsequent changes affect only verifier/documentation.
- AOT install: scripts/install.ps1 to artifacts/t83-installed, succeeded.
- T-57 installed verifier: all 14 cases passed, including shipped kits and containment/link fixtures; cleanup confirmed.
- T-83 installed verifier: all 14 cases passed on Windows x64 using the reviewed UTF-8 script, including external/internal aliases, Unicode paths, write modes, .gitignore, project preservation, late refusal, and normal installation.
- Linux x64 native probe: Debian 12 in preinstalled mcr.microsoft.com/devcontainers/dotnet:1-8.0, network disabled, source mounted read-only, container auto-removed. Compiled the actual HardLinkInspector.cs; count 1, count 2 through both Unicode target and alias, and missing-path statx error 2 all passed. This is native inspector evidence, not Linux CLI end-to-end coverage.
- Windows Arm64, Linux Arm64, macOS x64/Arm64: unexecuted. Pure layout/platform-selection tests do not claim native execution.

The previous orchestrator's full test run used TEMP under the Git worktree and failed two existing provenance tests that require a file outside every repository (tests.log:5007 and :5611). Those tests passed in the prior isolated rerun. The resumed full suite uses a dedicated external TEMP/TMP; the frozen spec now explains this required setup.

Raw logs and timed runners are in artifacts/t83-orchestration. Large logs were summarized with muthur utility summarize before examining cited evidence; summaries were advisory and their claims were checked against the actual logs.

Resumed full suite: 4,777 passed, zero failed/skipped (Core 3,736; Launch 54; CLI 295; Server 692), exit 0 in 779 seconds. Verified exact totals in resume-tests.log rather than the advisory summary, which showed only the final server total. All three known T-83 orchestrator temp directories were removed after test completion; the offline Linux container is absent.

Final integration: worker commit be53f91551b57ee8c3d91b76b3081471734fd74a changed exactly two verifier encoding lines. committedByLauncher=true. Its status was blocked because the named base advanced from 1b5c9af to 2036af7 during its full checks; that advancement was solely the orchestrator's external-TEMP documentation clarification. The worker checkout remained on the exact dispatched base, and both its reported full suite and the orchestrator's full suite passed. Mastermind review verified the unrelated documentation change and integrated the two-line commit without conflict in 505732b. No repeated implementation attempt was needed for that status.

After integration, direct and captured T-83 installed-verifier runs each passed all 14 cases. PowerShell parsing had zero errors in the worker. No product code changed after the successful build/full-suite/AOT checks. Final diff whitespace check passed.

Cleanup before submission: all six stopped T-83 worker worktrees removed with committed branches retained; all known dedicated external/internal test temp roots removed; native probe container auto-removed; process inspection found no T-83 scratch children. Task worktree, installed artifact, and verification logs remain as reviewable evidence, not running services. Independent validation and landing are owned by the conductor after orchestrator exit.
