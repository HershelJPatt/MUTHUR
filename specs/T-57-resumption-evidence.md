# T-57 resumption evidence — 2026-09-19

Resumed as `orchestrator-t-57` after reading the claim, task record and task ledger.
The ledger had a frozen-spec path but no implemented branch or verdict. The branch was
further along in specification detail: `task/T-57-containment` at `0d0907d` contained
two amendments and superseded `task/T-57-junction-containment`. No built implementation
was present. The first heartbeat recorded that distinction.

## Implementation and review

The implementer resumed the clean `task/T-57-a3-containment` worktree from `0d0907d`.
Its first build passed, but its CLI run exposed two defects in the frozen instructions:
seven existing invalid-path messages changed, and all 15 new cases failed in fixture
cleanup. Amendment 3, committed at `1ccce27`, preserves the existing spelling-rule
order and requires explicit link removal before recursive fixture deletion.

The implementer committed the corrected unit as `1d9b9c6`. It changes only
`KitCommands.cs` and adds `KitContainmentTests.cs`. All 212 existing CLI tests remain;
15 containment cases bring the CLI suite to 227. The orchestrator reviewed the entire
product diff and the test file, integrated the unit, and integrated current `main`
(`83d82e0`) into the task branch at `87a629a`. The existing manifest-test file is
byte-identical to `main`.

The worker's earlier server run was stopped after reporting 442 passed; that was not
a successful full run. The integrated full run below completed normally and supersedes it.

## Orchestrator verification

Build and tests ran from `C:/WorkSrc/MUTHUR/.worktrees/T-57b` with a unique OS temporary
directory outside any Git checkout, scratch `MUTHUR_HOME`, and
`MUTHUR_URL=http://127.0.0.1:17557`. A temp directory inside the checkout had interfered
with a worker provenance test; no test or product behavior was weakened to avoid it.

```text
dotnet build --nologo
  0 warnings, 0 errors

dotnet test --no-build --no-restore --blame-hang-timeout 2m
  --blame-hang-dump-type none --logger "console;verbosity=minimal"
  Launch:   28 passed, 0 failed, 0 skipped
  Core:   3736 passed, 0 failed, 0 skipped
  CLI:     227 passed, 0 failed, 0 skipped
  Server:  556 passed, 0 failed, 0 skipped
  Total:  4547 passed, 0 failed, 0 skipped; exit 0
```

The server suite took 6 minutes 53 seconds and remained active. Its hang detector did
not fire. Build/test logs are retained under `artifacts/t57-evidence/` in the task worktree.

## Installed Native AOT check

Published using the repository installation script into `artifacts/t57`, with scratch
hub environment values. The installer recorded branch `task/T-57-containment`, commit
`e4dca4a`. Subsequent changes are verification-script/documentation changes only.

The installed baseline accepted the outward-junction attack with exit 0 and created
one outside file. The checked-in `specs/T-57-verify.ps1` against the new installed CLI
produced:

| Case | Exit | New files outside |
| --- | ---: | ---: |
| outward junction | 2 | 0 |
| deeper under outward junction | 2 | 0 |
| chain of junctions | 2 | 0 |
| inward junction | 0 | 0 |
| ordinary destination | 0 | 0 |
| lexical traversal | 2 | 0 |
| source through outward junction | 2 | 0 |
| repository named through junction | 0 | 0 |
| file/directory collision through alias | 2 | 0 |
| identical destination through alias | 0 | 0 |
| dangling outward junction | 2 | 0 |

All refusals reported `invalid_manifest`, with the rule 22 messages checked exactly
for the relevant link cases. No crash or last-resort manifest-reader guard appeared.
Successful cases checked installed content; alias ordering retained last-one-wins.
All three shipped kits installed: claude 11 manifest files/13 reported files, codex
12/14, generic 12/14. Counts are derived from the manifests, not frozen numeric assertions.

The first verifier run exposed a PowerShell empty-stderr type issue on success. The
script now casts stderr to a string; the complete table and shipped-kit checks were
rerun successfully after that correction.

## Cleanup and limits

Every reproduction run removed its scratch tree and explicitly unlinked its junctions
first. The implementer removed all 18 fixtures from its initial failed cleanup run,
its private test directories, and its obsolete test process. The orchestrator removed
the integrated worker worktree, retained its committed branch history, confirmed the
final test host had exited, and removed the final isolated OS temporary tree. No
scratch hub was started. The live installation was not replaced.

These measurements establish Windows junction behavior. Linux/macOS execution is not
claimed. Hard links remain the already-filed T-83; spec-path containment remains T-84.
Concurrent filesystem changes between validation and writing remain outside this spec.

Independent validation and `muthur task land` are the remaining gates. This evidence
does not claim either a validation verdict or a landing.
