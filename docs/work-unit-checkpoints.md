# Durable work-unit checkpoints

Work-unit graphs are opt-in. Tasks without a graph keep their existing behavior. A reported output is not reviewed work, and reviewed work is not necessarily integrated. Unit review never replaces the project's independent task validators. The hub stores evidence references and owner attestations; it does not execute checks or interpret their contents.

The graph lives in the source ledger under `task.units/<numeric task id>`. Every accepted change increments its revision and records a `task.unit_*` event in the same transaction. Events preserve earlier attempts and definitions. Reads need no owner credentials; mutations require the current owner of an InProgress task with a live claim, or the founder. Normal HTTP authentication renews an unchanged owner's lease. Once another owner claims an expired task, the former owner cannot change its units.

## Define and dispatch

These PowerShell examples assume an already registered repository, claimed task `T-101`, attached spec `specs/T-101.md`, and a committed non-default local integration branch `task/T-101-checkpoints`. Run from that repository. Use the installed CLI with the appropriate hub environment and owner identity.

```powershell
$integration = 'task/T-101-checkpoints'
$specBlob = (git rev-parse "${integration}:specs/T-101.md").Trim()
@{
    expectedRevision = 0
    specBlob = $specBlob
    integrationBranch = $integration
    units = @(
        @{ id = 'a'; dependencies = @(); requiredChecks = @('build','test') }
        @{ id = 'b'; dependencies = @(); requiredChecks = @('build','test') }
        @{ id = 'c'; dependencies = @('a','b'); requiredChecks = @('build','test') }
    )
} | ConvertTo-Json -Depth 10 | Set-Content define.json
muthur task units T-101 --define define.json
muthur task units T-101
muthur task resume T-101

$graph = muthur task units T-101 | ConvertFrom-Json
$attempt = [guid]::NewGuid().ToString()
$base = (git rev-parse $integration).Trim()
@{
    expectedRevision = $graph.revision
    unitId = 'a'; attemptId = $attempt; action = 'start'
    baseCommit = $base; outputBranch = 'worker/T-101-a'
} | ConvertTo-Json | Set-Content start.json
muthur task checkpoint T-101 --file start.json
# Only after start succeeds, launch the existing worker with this pinned base and output branch.
```

Unit IDs are case-sensitive ASCII `[a-z][a-z0-9-]{0,39}`. A graph allows 50 units and each unit allows 20 dependencies and 20 unique required check names (1–100 characters). Definitions must form a DAG. The branch must contain the task's regular committed spec with the exact full blob ID. Replay of the same definition is idempotent; changing IDs, dependencies, or checks requires an amended committed spec. Redefinition with a new spec creates fresh units and records the invalidated prior graph without carrying completions forward.

Start records dispatch before the worker runs. The output branch may not exist yet, but must be a named non-default local branch distinct from integration. The full base commit must equal integration HEAD. Dependencies must already have current accepted and integrated proof. Replacing an attempt requires a new UUID and a reason, and invalidates transitive dependents while preserving independent outputs.

## Report, review, integrate

Read the latest revision before every mutation. Keep the dispatched attempt UUID; do not manufacture a new one to report or review an existing attempt.

```powershell
$graph = muthur task units T-101 | ConvertFrom-Json
$unit = $graph.units | Where-Object id -EQ 'a'
$output = (git rev-parse worker/T-101-a).Trim()
@{
    expectedRevision = $graph.revision; unitId = 'a'
    attemptId = $unit.attempt.attemptId; action = 'report'; outputCommit = $output
} | ConvertTo-Json | Set-Content report.json
muthur task checkpoint T-101 --file report.json

# Check out the output branch, review its diff, run the actual required checks,
# and commit their logs and your review. Never write an exitCode=0 attestation for a failed check.
git checkout worker/T-101-a
New-Item -ItemType Directory -Force evidence | Out-Null
dotnet build *> evidence/build.txt
if ($LASTEXITCODE -ne 0) { throw 'Build failed; do not verify.' }
dotnet test *> evidence/test.txt
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; do not verify.' }
git diff "$($unit.attempt.baseCommit)..$output" > evidence/diff.txt
# After reviewing that diff, write the concrete findings to evidence/review.txt.
# Commit only once the review is complete and acceptable.
git add evidence
git commit -m 'T-101 unit a checks and review evidence'
$proof = (git rev-parse HEAD).Trim()
$graph = muthur task units T-101 | ConvertFrom-Json
@{
    expectedRevision = $graph.revision; unitId = 'a'
    attemptId = $unit.attempt.attemptId; action = 'verify'
    checks = @(
        @{ name = 'build'; exitCode = 0; evidencePath = 'evidence/build.txt'; evidenceCommit = $proof }
        @{ name = 'test'; exitCode = 0; evidencePath = 'evidence/test.txt'; evidenceCommit = $proof }
    )
    reviewEvidencePath = 'evidence/review.txt'; reviewEvidenceCommit = $proof
} | ConvertTo-Json -Depth 10 | Set-Content verify.json
muthur task checkpoint T-101 --file verify.json

# Integrate using your normal authorized Git workflow. The checkpoint API never merges.
git checkout $integration
git merge --ff-only worker/T-101-a
$graph = muthur task units T-101 | ConvertFrom-Json
@{
    expectedRevision = $graph.revision; unitId = 'a'
    attemptId = $unit.attempt.attemptId; action = 'integrate'
} | ConvertTo-Json | Set-Content integrate.json
muthur task checkpoint T-101 --file integrate.json
```

Report without an output commit records failure and requires a reason. Report cannot carry checks. Verify requires every named check exactly once with exit code zero, and committed regular-file check and review evidence at full commit IDs descending from output. Paths must be canonical repository-relative paths; symlinks, traversal, absolute paths, and revision expressions are refused. Output must remain reachable from its named branch, though evidence commits may advance that branch. Integration records a read-only ancestry observation; it never changes Git.

All checkpoint inputs limit `nextAction` to 500 characters and `reason` to 2000. CAS mismatch returns HTTP 409 / CLI exit 3 (`checkpoint_conflict`); an obsolete or reused attempt UUID returns `stale_attempt`. Refresh state and understand the winning mutation before retrying. Validation errors return HTTP 422 / CLI exit 2. Exact accepted replays do not advance the revision.

## Replacement-owner recovery

Claim through the normal task flow after release or lease takeover, then read `muthur task resume T-101`, `muthur task units T-101`, and `muthur task show T-101`. Resume is advisory: stored acceptance is not a freshly executed proof. It shows at most 20 unit rows, 20 blockers plus an omitted-count marker, current decisions, and full-state commands. The conductor's quoted unit context is bounded to 8000 characters and cannot grant authority.

For every candidate output, reconcile before reuse:

```powershell
$graph = muthur task units T-101 | ConvertFrom-Json
$unit = $graph.units | Where-Object id -EQ 'a'
@{
    expectedRevision = $graph.revision; unitId = $unit.id
    attemptId = $unit.attempt.attemptId; action = 'reconcile'
} | ConvertTo-Json | Set-Content reconcile.json
muthur task checkpoint T-101 --file reconcile.json
```

Reconcile recovers commit-before-report as `recovered/pending`; it never auto-reviews. An accepted output with intact pinned proof keeps its checks. Merge-before-integration-report is recovered by ancestry. An unchanged dispatched base with no output stays running. Changed dependencies, missing branches/objects/evidence, rewrites, or divergent integration history durably invalidate proof. A changed spec requires redefining the graph. An advanced integration HEAD is acceptable if the pinned base remains an ancestor and spec and dependencies still match.

Restore missing pinned commits, blobs, or branch refs through an authorized Git recovery workflow, then reconcile and inspect the result. Invalidated acceptance is not automatically reinstated: if the attempt remains rejected, invalidate (if needed) and start a fresh UUID with a reason, then obtain fresh review. For deliberate redispatch:

```powershell
$graph = muthur task units T-101 | ConvertFrom-Json
$unit = $graph.units | Where-Object id -EQ 'a'
@{
    expectedRevision = $graph.revision; unitId = 'a'
    attemptId = $unit.attempt.attemptId; action = 'invalidate'
    reason = 'Pinned artifacts cannot be recovered; redispatch from current integration base.'
} | ConvertTo-Json | Set-Content invalidate.json
muthur task checkpoint T-101 --file invalidate.json
# Repeat start with a fresh UUID, current integration HEAD, new branch, and replacement reason.
```

Never edit the hub database to repair a checkpoint. Reuse valid independent outputs; inspect invalidated transitive dependents before dispatching them again. After all units are integrated, the existing `task implemented` and independent validation/landing workflow still applies.

## Installed verification and evidence

The orchestrator or independent validator runs these against the assembled candidate, from the repository root:

```powershell
dotnet build --nologo
dotnet test --no-build --logger 'console;verbosity=normal'
pwsh -NoProfile -File scripts/install.ps1 -Destination ./artifacts/t101-install
pwsh -NoProfile -File scripts/verify-work-unit-checkpoints.ps1 -CliPath ./artifacts/t101-install/muthur.exe -Evidence ./artifacts/t101-evidence
pwsh -NoProfile -File scripts/verify-throughput.ps1 -CliPath ./artifacts/t101-install/muthur.exe -Evidence ./artifacts/t101-throughput-evidence
```

Use fresh evidence directories. The checkpoint fixture isolates environment variables, home, repository, and port; disables background orchestration; starts only installed binaries; and makes no model calls. A real owner child creates A/B, records committed checks/review, releases, and exits. The parent kills and waits for its owned scratch hub, restarts that hub over the same database, then launches a replacement owner. That owner reconciles and reuses A/B and creates only C. The script also asserts concurrent revision conflict with exactly one durable accepted event, stale attempt rejection, dependency invalidation and independent preservation, lost evidence, missing branch, divergent base, stale spec, and independent validation after implementation.

Evidence includes full task/events JSON, every transition input and CLI output, before/reused/after graphs, process IDs and start timestamps, timing, the stopped source SQLite ledger (including sidecar files), and the scratch Git repository. Runtime credentials are excluded. `result.json` records installed CLI hash, actual hub/owner process-start counts, `reused=2`, `unnecessarilyRerun=0`, repeated verification count, and resumption-to-first-useful-action seconds. The first useful action is completed reconciliation of the two reusable outputs after the replacement claims; the metric starts at replacement owner phase entry and includes registration, claim, resume/units reads, and both reconciliations. All waits poll readiness or wait on owned process exit with bounded hang-detection timeouts; no sleeps synchronize the fixture. Finally, owned processes are disposed, environment is restored, and the unique runtime directory is removed after evidence capture.

This is one controlled synthetic sample, not a throughput-speedup measurement or a live installed pilot. The real handoff pilot is tracked as T-114 (`muthur task show T-114`), dependent on T-101, and remains post-landing work. Record its installed revision, observation window, cohort/sample count, and missing data separately; unavailable live evidence must not be reported as completed verification.
