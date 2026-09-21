# T-96 review and verification evidence

## Frozen-base compiler inventory

Base: `53329f64ae80dc3a8a72ef1ceb6a021d6ee0757d`; SDK: `10.0.204`. T-91 and T-92 are landed. HarnessService.ExhaustedAsync has one summary.

Command: `pwsh -NoProfile -File scripts/test-xml-documentation-policy.ps1 -OutputDirectory artifacts/T-96-baseline` (exit 0). Inventory failure is null. Baseline exit 0, documentation warnings-only exit 0, documentation with CS1591 suppressed exit 1 as expected. All eight probe exits match; only stranded documentation with generation enabled produces CS1587. Duplicate and false-prose probes pass in both modes.

The utility summary was advisory and truncated; the following table is derived directly from inventory counts, each cross-checked against the deduplicated diagnostic records. Counts are CS1570 / CS1573 / CS1587 / CS1591.

| Project | Baseline | Documentation | Documentation suppress CS1591 |
| --- | --- | --- | --- |
| src/Muthur.Cli/Muthur.Cli.csproj | 0 / 0 / 0 / 0 | 0 / 12 / 0 / 51 | 0 / 0 / 0 / 0 |
| src/Muthur.Contracts/Muthur.Contracts.csproj | 0 / 0 / 0 / 0 | 0 / 82 / 3 / 407 | 0 / 82 / 3 / 0 |
| src/Muthur.Core/Muthur.Core.csproj | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 179 | 0 / 0 / 0 / 0 |
| src/Muthur.Data/Muthur.Data.csproj | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 25 | 0 / 0 / 0 / 0 |
| src/Muthur.Launch/Muthur.Launch.csproj | 0 / 0 / 0 / 0 | 0 / 14 / 3 / 51 | 0 / 0 / 0 / 0 |
| src/Muthur.Server/Muthur.Server.csproj | 0 / 0 / 0 / 0 | 0 / 15 / 2 / 262 | 0 / 0 / 0 / 0 |
| tests/Muthur.Cli.Tests/Muthur.Cli.Tests.csproj | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 123 | 0 / 0 / 0 / 0 |
| tests/Muthur.Core.Tests/Muthur.Core.Tests.csproj | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 38 | 0 / 0 / 0 / 0 |
| tests/Muthur.Launch.Tests/Muthur.Launch.Tests.csproj | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 60 | 0 / 0 / 0 / 0 |
| tests/Muthur.Server.Tests/Muthur.Server.Tests.csproj | 0 / 0 / 0 / 0 | 0 / 1 / 0 / 623 | 0 / 0 / 0 / 0 |

Documentation warnings-only: CS1573=124, CS1587=8, CS1591=1819 (1951 total). Strict mode: CS1573=82, CS1587=3 (85 observed; dependent builds may stop). The historical T-91 policy table remains historical; this newer baseline has 46 more CS1591 warnings. No documentation settings changed.

Raw evidence retained under artifacts/T-96-baseline (inventory.json and per-command stdout/stderr). All stderr logs are empty. No scratch hub was started. The inventory runner removes its temporary fixture directory in finally.


## Final implementation review

Implementer: installed implementer tier, codex/gpt-6-astra, branch task/T-96-unit-a2, commit 5d4fd52. Launcher committed the output (`committedByLauncher: true`); the orchestrator read all nine changed files, including every checker, fixture, project and policy change. Integrated by fast-forward into task/T-96-xml-check. Initial dispatch stopped without edits for missing full base identity/repository trust; corrected dispatch succeeded.

The diff matches the frozen design: SDK Roslyn references; explicit tracked src/tests scope; exact generated/probe exclusions; all attached documentation trivia on each owner; malformed-group skip; direct unprefixed summary counting; second-summary position; independent partial declarations; conditional assignment union with explicit 12-symbol cap; deterministic Git-backed console and test-suite enforcement. No product comments, documentation-generation settings, package versions, compiler suppression or runtime services changed. The historical compiler evidence is retained.

The 64 objective cases include positive duplicate assertions (and an assertion that empty/mutated results fail those assertions), nested/escaped text, multiple trivia, attributes, block/multiline docs, ownership, partial declarations, malformed and stranded XML, semantic false prose, conditional branches, generated files, explicit versus ordinary fixture paths, tracked current content versus untracked content, Unicode/space paths, stable diagnostics, and infrastructure failures.

Independent orchestrator checks on commit 5d4fd52:
- Build: `dotnet build Muthur.slnx --disable-build-servers -m:1`, exit 0, zero warnings/errors.
- Focused tests: spec command, 64 passed, zero failed/skipped.
- Repository console: spec command, exit 0, no diagnostics.
- Real standalone console probes in an owned temporary Git repository: tracked duplicate exit 1 with exact `src/Probe.cs(2,5): error MUTHURXML001` diagnostic; generated duplicate exit 0; undocumented public API exit 0; deleted tracked file exit 2 with explicit infrastructure error. Probe removed in finally.
- Git whitespace check: clean.

Each independent long command uses an owned process, captured stdout/stderr, a 15-minute build/full-suite timeout (3 minutes focused), and deterministic minute heartbeats. No scratch/live hub was needed or started.

## Full-suite outcome and cleanup

Initial independent full run: 4932 passed, 1 failed, 1 platform skip. The sole failure was CensusCommandRunnerTests.Timeout_also_covers_descendants_holding_pipes_after_parent_exit(parentExits: True), an IOException deleting its fixture directory in Dispose at line 17. This is already tracked as T-98 (intermittent census descendant timeout cleanup lock); no unrelated code was changed. The original log is retained.

One bounded isolated recheck passed both parentExits variants (2/2). One bounded complete-suite recheck then passed: CLI 298, Core 3736, Launch 54, Server 781, XML check 64 = 4933 passed; zero failed; one existing non-Windows census platform skip. All commands exited 0 on this recheck. Final full-suite log: artifacts/T-96-full-tests-recheck.log. Utility summaries were checked against the actual result lines.

Both owned worker worktrees were removed after checking clean tracked state; worker logs were preserved. No task-owned dotnet/testhost/muthur processes remained after verification; the failed census fixture directory was also absent. The standalone console probe removed its temporary repository. No live hub was started or modified. Evidence-only changes after testing do not modify the verified product commit.

Before submission, all task artifacts are archived under C:/WorkSrc/MUTHUR/artifacts/T-96/ and the isolated task worktree is removed. The task branch and committed review/spec remain for validation and conductor landing; no push or default-branch merge is performed.
