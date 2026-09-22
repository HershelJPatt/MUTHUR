# Throughput repair rollout (T-95)

Keep the conductor at two sessions for the first comparison. Independent validation, required verification commands, human-only decisions and permission boundaries remain unchanged.

## What changes

- `worker run` supplies a checked named local base, full commit, default branch, committed spec blob and actual worktree. `--base HEAD` resolves to the caller's attached branch; detached or invalid assignments refuse before inference. The project/task registration supplies the default branch; `--default-branch` supplies it explicitly when needed. A moved base refuses dispatch. Workers still recheck the live base before editing.
- Launched repository processes receive exact process-local `safe.directory` trust. This is Git ownership recognition, not filesystem write permission. No global Git configuration is changed. Worker hub credentials remain scrubbed. Codex retains `workspace-write`; only the appended nonsecret trust fields are passed as explicit shell environment settings. Existing Git overrides stay in the environment. See [official shell environment configuration](https://learn.chatgpt.com/docs/config-file/config-advanced#shell-environment-policy).
- A blocked child records its exit. After every question is answered, its preserved work returns to the queue on the next conductor pass. A live or re-registered owner keeps its claim. Resumption receives the latest five answers with authors and timestamps, and explicit truncation notices where needed.
- Setting prerequisites records the return to backlog. Historical dependency events are replayed with their conditional state transition. Waiting on a prerequisite does not consume an unproductive-session strike.
- Each harness attempt has a fresh output directory. A failed process retains its exit reason even when it wrote a final message. Worker receipts add optional run ID, status, failure kind, exit code and base/head/spec object IDs; old receipts keep unknown values null.

## See the loop run without a model

`muthur sim run` (from an installed CLI) is the quick form of the stand-in harness below: a scratch hub on the
`sim` harness, seeded tasks, the conductor on, and the ledger streamed until every task has landed. One task asks
the founder a question and one is built wrong once, so the board shows Needs you and a validation bounce as well as
integration and a hub land. It prints per-task phase timings at the end and starts no model. `--pace` slows the
sessions so the board can be watched; `--auto-answer 0` leaves the question for a person; `--keep` leaves the hub up.

## Verify before rollout

Use a task-specific `MUTHUR_HOME` and `MUTHUR_URL` for every development command. Build and run the full suite, then install the committed candidate into a scratch destination with `scripts/install.ps1 -Destination <scratch-install> -Ref <full-sha>`.

Run `node --test scripts/measure-throughput.test.mjs`, then `pwsh scripts/verify-throughput.ps1 -CliPath <scratch-install>/muthur.exe -Evidence <artifact-directory>`. It uses an isolated hub and a deterministic stand-in harness: no paid inference. It checks dispatched provenance, Git ownership refusal/recovery, credential scrubbing, launcher commit, process failure visibility, old/new request flow, current decisions, immediate resumption and dependency wait classification. It captures the full scratch ledger and asserts that the measurement script counts and correctly times the actual recovery across conductor re-registration. It captures phase durations and stops only its own scratch server. Run `dotnet test` separately for historical replay, re-registration races, bounded packets and old/new receipt compatibility.

The test may need the normal installed .NET/NuGet access on Windows. Use the existing approval mechanism when the sandbox refuses it; do not change sandbox or sensitive-file permissions to make a test pass.

## Measure the same windows

`node scripts/measure-throughput.mjs --url http://127.0.0.1:7420 --since <UTC-time> --output <artifact.json>` reads a bounded ledger snapshot and writes a report. It makes only GET requests and records the captured sequence. Archived events can be replayed with `--events <events.json> --until <UTC-time>`. Node 22 or newer is required.

Capture deployment time, binary revision, conductor settings and this report immediately before and after rollout, then compare equal 24-hour and 72-hour windows. Report task mix alongside the counts. Landings are not weighted corporate outcomes; splitting tasks must not be counted as a productivity gain.

The September 20 audit window (15:24:15Z–22:24:31.079Z) contained 16 landings in 7.004 hours (2.284/hour), 28 reported non-utility worker runs, 19 unsuccessful runs and 112.917 unsuccessful worker-minutes. Validation produced 16 passes, zero failures and one blocked verdict. Mean claim-to-verdict was 12.434 minutes. These are the recent corrected baseline, not a claim that this repair already achieved that rate.

Acceptance signals after rollout:

1. Zero model calls lost to missing dispatch metadata; invalid inputs refuse before inference.
2. Answered tasks with recorded child-exit proof become eligible on the next pass (normally within the configured 60 seconds, plus available capacity), rather than waiting another 30-minute lease. Report queued/unfinished samples too.
3. No recycled final messages and no unproductive strikes for pending prerequisites.
4. Reduced failed worker-minutes per landing, with unchanged validation requirements and no increase in failed or blocked verdicts, reopened tasks or rollback rate.
5. Higher landings/hour sustained over comparable windows. Do not label a short installed test or a few landings as a demonstrated 10× gain.

The two-slot capacity is still a hard constraint. At 12.4 landings/hour it allows only 9.68 total session-minutes per task, below the observed validation time alone. If these repairs reduce waste while preserving quality, the next separate decision is capacity and verification-service design, supported by CPU/build contention, account limits, validation latency and cost measurements.
