# Offline workflow benchmark

Run from the repository root with PowerShell 7 and the repository's .NET SDK and cached NuGet packages:

```powershell
pwsh ./scripts/benchmark-workflow.ps1 -Baseline 89dc34c -Candidate HEAD -Output ./artifacts/workflow-compare
pwsh ./scripts/benchmark-workflow.ps1 -Baseline 89dc34c -Candidate HEAD -Output ./artifacts/workflow-negative -Scenario false-green -Trials 2
dotnet test tests/Muthur.Server.Tests --filter Category=WorkflowBenchmark
pwsh ./tests/Muthur.Server.Tests/Benchmark/RunnerCleanup.Tests.ps1
```

The baseline is pinned to `89dc34c0ed8e1208ef75529de8f493aed7306fe6` (post-T-95). Output must name a new directory. Trials defaults to one and accepts 1–10. A normal full `dotnet test` runs each scenario once. The runner resolves both revisions before executing work, archives each revision into its own scratch directory, and overlays only the benchmark C# files, scenario manifest and the test project's manifest content entry. Product source stays at the selected revision. Cached dependencies are required: package sources are cleared, and missing packages make the revision unmeasured/error. The benchmark makes no network calls, starts no live hub and never invokes the muthur CLI.

The suite uses real in-process HTTP hubs and disposable git repositories, injected clocks and fake session launchers. Scripted implementation and validation are deterministic inputs. They do not measure agent judgment, model quality, stochastic reliability, tokens or price.

| Historical scenario | Incident | Observable outcome |
|---|---|---|
| stale-spec | T-50 | Missing/wrong-task specs rejected; committed task-branch spec attached; correct result validated and landed |
| interrupted-owner | T-95/task lease | Owner expires, sweep releases task, replacement completes the same task once |
| late-verdict | Validation claims | First claim expires; current holder's claim rejects stale verdict; current verdict lands |
| dependency-wait | T-95 | Dependent blocked until prerequisite lands; ten fake blocked minutes recorded separately |
| conflicting-branches | Land conflict | Conflict preserves main and prevents completion; corrected rebase requires new validation before landing |
| false-green | Historical wrong implementation | Wrong result is approved and lands; external product grader detects it; zero product-success credit |
| no-verdict-loop | Conductor no-verdict retries | Three observed staffing attempts/launcher invocations, cap holds, 29-minute cooldown holds, one escalation |

Historical and forward cohorts are distinct. Version 1 contains seven historical cases; the forward cohort is empty. Add a case by specifying a stable ID, cohort, source incident, initial state, scripted events, expected outcomes, failure conditions and zero-cost resource bounds in `benchmarks/workflow/scenarios.v1.json`, implementing its script and independent evidence checks, and extending focused grader tests. Version/hash changes invalidate comparisons with older suites. Do not silently recategorize historical outcomes as forward evidence.

Schema version 1 writes `comparison.json`/`.md`, per-revision `aggregate.json`/`report.md`, and `<scenario>/<trial>/trial.json`, `http.json`, `task-events.json`, `git.json` and the graded `result.txt` when present. TRX, build/test logs and revision resolution logs remain outside scratch cleanup. Trial reports contain suite SHA-256, pinned product and driver revisions, run ID, cohort, trial index, sample count, UTC window, process stopwatch seconds, fake phase intervals and blocked fake seconds. Driver content hashes identify the exact overlaid bytes, including an uncommitted launcher-owned submission; the runner takes one checked snapshot for both revisions and carries its combined hash into each trial and aggregate. Direct test execution labels unavailable pinned identities explicitly; use the runner for revision comparison.

The positive completion grader requires a Done task, independent validator approval, required scenario observations and git evidence with `main:result.txt` exactly `42` after removing at most one terminal LF or CRLF. Spaces and multiple newlines are significant. Missing evidence fails closed. `false-green` separately reports `productCorrect=false`, `falseApproval=true`, `detected=true`; detection passes that scenario but never contributes to successful product completion. `no-verdict-loop` tests bounded orchestration while the task remains Validating, and also contributes zero product successes.

Counts name separate observations: staffing attempts come from real `conductor.staffing` events; scripted launcher invocations come from the fake launcher's observed calls; worker runs count scripted implementation submissions. Actual OS starts means agent/harness starts (zero), excluding git, build and test infrastructure. Registrations never count as OS starts. Human interventions, paid sessions and model calls are zero by construction. Tokens/cost are null with an unavailable reason. Aggregate wall durations show minimum, maximum and mean across samples; fake intervals/task-hours are never summed into wall time. Reports make no speedup claim.

An incompatible revision, failed build/test, missing trial/evidence, unknown scenario or interruption exits nonzero and leaves an incomplete/error report. Only identical suite version/hash, cohort, selection and trial counts are comparable. `-Mode real` resolves the requested revisions, writes a visibly unmeasured report explaining that no approved admitted real-harness route exists and budget is zero, then exits nonzero without starting any harness.

Every child process has a ten-minute bound (with a bounded termination wait); only processes created by the invocation can be terminated. Children get isolated MUTHUR_HOME, loopback MUTHUR_URL, temporary directories and cleared provider credentials/agent identity. The runner's `finally` verifies the unique scratch path and owner marker before removal. If the runner itself is forcibly killed, `comparison.json` remains incomplete and the uniquely named scratch directory may remain: inspect its owner marker against the run ID, confirm its recorded children have exited, and remove only that directory. Never reuse or overwrite an interrupted output directory. Fault switches `-Fault after-scratch` and `-Fault during-child` exercise failure and cancellation deterministically without synchronization sleeps. The cleanup test also checks real mode, unknown scenarios and preservation of an existing output directory.

Installed-product provenance and post-landing observation are orchestrator follow-ups; this benchmark starts no live hub and does not install or push anything.

## First deterministic verification

Executed on 2026-09-20 in the assigned Unit A worktree, with baseline `89dc34c0ed8e1208ef75529de8f493aed7306fe6` and candidate `a3dea76c1826ce4e57ee4188bf3c7dadbc5bb0f7`. Driver HEAD was the candidate commit; the benchmark files were the uncommitted launcher-owned submission, identified by retained content hashes. These are observed scripted outcomes, not real-agent measurements.

The first successful full comparison used `-Baseline 89dc34c -Candidate HEAD -Output artifacts/T-102/compare-longpaths`, run `6018338744f64a739c9f7c06c34a3054`. Suite hash: `7a4a9ab46878bfdcb8454dce52caa69f41274b4664fb3883512e4559f0cf9c48`.

| Revision | Trials | Product successes | Negative detections | Staffing attempts | Scripted launcher calls | Scripted worker runs |
|---|---:|---:|---:|---:|---:|---:|
| Baseline | 7 | 5 | 1 | 3 | 3 | 9 |
| Candidate | 7 | 5 | 1 | 3 | 3 | 9 |

The targeted `false-green -Trials 2` comparison in `artifacts/T-102/negative`, run `59ce3d03533d44eea3bfbab6005e3311`, recorded two detections and zero product successes on each revision. All four trial records have product correctness false. Both commands exited 0 and verified scratch cleanup. Actual agent OS starts, human interventions, paid sessions and model calls were zero. The cleanup test in `artifacts/T-102/final-cleanup` passed all five checks (failure, cancellation, real mode, unknown scenario and existing-output preservation).

After the final grader/report checks were added, `artifacts/T-102/final-compare` repeated the full comparison successfully with the same counts and verified cleanup: run `73a574f16590402fb203034207aafdd8`, driver content hash `0a791890b0fadbafed01d1e471895469d65c2cc61a35d5bdda3b1d274b7cd2f7`.

Final worktree verification exited 0: `dotnet build --disable-build-servers -p:RestoreConfigFile=artifacts/T-102/appdata/NuGet/NuGet.Config` produced zero warnings/errors; `dotnet test --no-restore --disable-build-servers --logger trx --results-directory artifacts/T-102/final-results` passed 4,970 tests, failed zero, and retained the one existing platform-specific CensusCommandRunner skip. Logs are `artifacts/T-102/final-build.log` and `final-test.log`; TRX files are under `final-results`. All 13 benchmark entry/grader/report tests passed. A scan of the 18 retained full-comparison and negative-control trial files found no credential fields or bearer tokens.

Initial local attempts exposed sandbox-only verification constraints: the user NuGet configuration and link traversal through the default AppData temp directory were inaccessible. Verification used a worktree-local NuGet configuration with cleared sources, a local TEMP/TMP directory, a git discovery ceiling at that directory, isolated MUTHUR_HOME/URL and disabled build-server reuse. No test or product gate was weakened. The first archived attempt also exposed Windows git path-length handling; disposable benchmark repos now enable `core.longpaths` locally. Earlier failed logs remain under `artifacts/T-102` alongside successful evidence.
