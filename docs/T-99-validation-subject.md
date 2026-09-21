# T-99 validation subjects

Every implementation submission opens a new round, identified by a GUID. The durable subject records the
full implementation commit, exact committed spec text's UTF-8 SHA-256, canonical repository path, project ID,
sorted distinct required roles, and the committed build/test commands. Claim responses and task detail JSON
include `currentSubject`; claims and verdicts record the full subject in the append-only ledger.

Check commands are read from the implementation SHA at submission, claim, verdict and landing. Changes to
the working tree or unrelated default-branch configuration cannot replace them. Project identity, repository
path, default branch, landing mode, required roles and role brief hashes come from the live ledger and must
still match. OS, process architecture, runtime and the available host assembly revision are descriptive
metadata and do not invalidate a round.

## Migration and recovery

The migration adds nullable subject references and a nullable frozen spec digest. Historical verdicts and
events are not backfilled or attributed to inferred subjects. Done/cancelled tasks remain readable in their
original states. Legacy provenance and evidence are `unknown`; it does not mean the task was incorrectly
validated. Unknown rounds are excluded from automatic validation staffing and pending queues.

An owner or founder recovers a validating, validated or in-progress task with:

```text
muthur task revalidate T-n --reason "Explain why a new review is needed"
muthur task spec T-n specs/T-n.md --branch task/T-n-work
muthur task implemented T-n --branch task/T-n-work
```

Recovery clears claims and the current subject, preserves previous verdicts and ledger history, returns the
task to in progress and renews its owner lease. Reattach historical specs whose digest is unknown explicitly.
Reattachment while validating or validated remains disallowed. A changed committed spec produces
`spec_changed` until its owner explicitly attaches the intended frozen content.

The independent validator then retains the claim's round ID:

```text
muthur validate claim T-n --as win-validator
git worktree add --detach .worktrees/validate-T-n <currentSubject.implementationSha>
muthur validate pass T-n --as win-validator --subject <currentSubject.id> --evidence-file report.txt
```

Inspect the exact SHA, frozen spec digest and captured checks and briefs. Never fetch a replacement round ID
at verdict time: a report for an old round cannot authorize a new one, even when the validator reclaimed it.

## Client compatibility and evidence

Old clients must upgrade and explicitly provide `subjectId` for every verdict, including failure and blocked.
The API returns `validation_subject_required` (422; CLI exit 2) when it is absent. Claims may omit it to take
the current round. `stale_validation_subject` is 409/exit 3 for superseded or closed rounds.
Policy drift returns `validation_subject_changed` with recovery instructions. Branch movement refuses
landing with `implementation_changed`; legacy landing returns `validation_provenance_unknown`.

CLI `pass`/`yes`, `fail`/`no`, and `blocked` accept `--subject`. `--evidence` is inline report text;
`--evidence-file` reads a local UTF-8 report. Existing inline `--note` remains available. These report inputs
are mutually exclusive. No evidence URL is fetched. Reports need at least 20 trimmed characters describing
the check, observation and an artifact/reference or reproduction command. Blank reports and acknowledgments
such as “ok”, “pass”, “yes”, or “done” produce `evidence_required` (422/exit 2) for every outcome.
The minimum prompts useful reporting; it does not prove correctness.

Task detail, API and CLI JSON expose `current`, `stale` or `unknown` provenance and
`missing`, `reported`, `stale` or `unknown` evidence labels. `reported` means a report was supplied, never
that the hub independently proved it. Refusal events omit the submitted report body.

## Landing and concurrency

The branch head must equal the approved SHA before integration. Every merge input and implementation parent
then uses that SHA, including if the branch moves after the check. Plumbing captures the target SHA and uses
compare-and-swap to move it. Checkout merges retain dirty-checkout refusal and conflict abort behavior.
PR mode creates `refs/heads/muthur-approved/T-n/<subject-id>`, pushes an explicit SHA refspec without force,
and opens that pinned branch with subject identity in the body.

Final subject validation, git integration and outcome recording share a single `Ledger.MutateAsync` writer
critical section and the landing semaphore. This deliberately delays other writers during bounded git work;
the first slice favors a single repository invariant over writer throughput. Helpers use the supplied database
context and do not reenter the ledger. Git and SQLite are not a distributed transaction: operational crashes
still require inspecting the recorded state and exact git ancestry before recovery.

Race tests pause the injected git runner after the branch-head comparison, then move the branch before
allowing checkout and plumbing integration to proceed. The writer test calls competing owner recovery,
resubmission, spec attachment and policy services directly while that barrier is held, so each has reached
the ledger wait before integration resumes; incomplete HTTP requests alone do not establish that ordering.

## Reproducible observation

Count ledger events in a fixed sequence window, not overlapping throughput counters. On a read-only copy of
the ledger, bind `:first_seq` and `:last_seq` to the captured cohort window:

```sql
SELECT type, count(*) AS attempts
FROM events
WHERE seq > :first_seq AND seq <= :last_seq
  AND type IN ('task.implemented', 'validation.claimed', 'validation.passed',
               'validation.failed', 'validation.blocked', 'validation.verdict_refused',
               'validation.subject_invalidated', 'task.revalidation_requested',
               'task.landed', 'task.pr_opened', 'task.land_refused', 'task.land_failed')
GROUP BY type ORDER BY type;
```

For refusal causes group `validation.verdict_refused` by `json_extract(payload_json, '$.code')`.
Implementation/claim/verdict/land events contain `subject.id`; refusals and invalidations use `subjectId`.
One round can have multiple refused attempts; counts are attempts, not a claim of distinct defects prevented.

## Rollout and pilot checklist

- Review every diff, run `dotnet build` and `dotnet test -v n`, retain logs and report counts and warnings.
- Run `scripts/clean-test-temp.ps1` with its documented age/ownership guards and report cleanup failures.
- Install the reviewed full SHA with `scripts/install.ps1 -Destination <absolute scratch install> -Ref <sha>`.
- Run `scripts/verify-T-99.ps1 -InstallPath <absolute scratch install>`. It uses installed CLI/HTTP only,
  a unique home, loopback port, local git fixture and distinct owner/validator; no real remote or model calls.
  It also pins configuration-section URL, data directory and SQLite connection overrides to that scratch
  fixture, restores the previous environment, and bounds process termination waits.
- Review the JSON evidence for missing outcomes, revision, round IDs, sequence window and event counts.
- Upgrade validator clients and kit source instructions together; do not silently assign IDs for old verdicts.
- After installation, observe the next eligible real task. Until then the live pilot is **missing**, not complete.
  The orchestrator must link an observation follow-up if no eligible pilot is available at submission.
- Verify scratch hub and children stopped and restore environment before leaving the session.
