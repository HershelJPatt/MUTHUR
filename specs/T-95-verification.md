# T-95 implementation verification

Candidate product revision: `f3bdcd2dcc71c7df78fa9247f231ea92ee3535d6` (T-95 implementation plus conflict-free integration of main through T-7/T-89). Subsequent changes in this submission correct the verification script and record this evidence; product source is unchanged.

## Automated checks

- Build: zero warnings/errors. Scratch `MUTHUR_HOME` and non-live `MUTHUR_URL` were set for build, test and installation commands.
- Full suite before main integration: 4,809 passed, zero failed/skipped (Core 3,742; Launch 65; CLI 295; Server 707). Wall time 595.907 seconds; preceding incremental build 2.744 seconds. Logs: `artifacts/test-final.log`, `artifacts/build-final.log`, `artifacts/verification-timing.json` in the implementation worktree.
- After integration: build passed with zero warnings/errors; all Launch tests 65/65, all CLI tests 298/298; Census, DoctorIngestOutbound, Reliability and ThroughputEvidence server tests 105 passed/one platform-specific skip. Logs: `artifacts/integrated-*.log`. No overlapping product-source conflict occurred.
- The first run exposed read-only Git object cleanup in the new fixture, then fixed and re-run. The installed verifier also needed stderr-result capture and persistent HTTP connection reuse; those failures were in the verifier, and their artifacts were retained separately. No failed run is presented as a pass.

## Installed product, no paid inference

Published the pinned candidate with `scripts/install.ps1 -Destination artifacts/t95-install -Ref f3bdcd2dcc71c7df78fa9247f231ea92ee3535d6`. Native AOT CLI and server publish passed in 55.026 seconds.

`scripts/verify-throughput.ps1` passed against that installed CLI and a unique temporary hub. Evidence directory: `artifacts/t95-installed-d7696c51a8b54bac910111ab09f8e622` in `C:/WorkSrc/MUTHUR/.worktrees/throughput-audit-20260920`.

Observed:

- Worker received named base/default branches, full commit and spec object IDs, and verified process-local Git trust while Git was instructed to simulate a different owner. No worker hub identity reached the stand-in process. Launcher committed its output. End-to-end worker fixture took 5.229 seconds.
- An intentional exit 7 remained a failed result despite a final `STATUS: done` message. Its receipt retained failure kind, exit code, run identity and Git provenance. Distinct attempts used distinct output paths.
- The conductor launched an owner, that owner claimed and froze the task, asked two questions, exited while blocked and left token-bound exit evidence. One answer left the task blocked. After the second answer, a new conductor session received both decisions and parked the task behind its preserved prerequisite. No `conductor.no_verdict` event was charged.
- Final-answer-to-resumed-work delay was **15.580 seconds**, with the scratch conductor explicitly configured at its 15-second minimum. Live configuration remains 60 seconds and two sessions; this result establishes next-pass recovery, not a 15-second live SLA or a sustained tasks/hour increase.
- The scratch server and its process tree were stopped and its unique temporary home/repository removed. The live hub retained instance `25c38c198fe74d69bcd83a1b94e90dfa` and the same two-session ceiling throughout development verification.

## Baseline and rollout

`scripts/measure-throughput.mjs` replayed the archived audit ledger through sequence 4876 and reproduced 16 landings/7.004 hours (2.284/hour), 28 non-utility worker runs, 19 failed runs and 112.917 failed worker-minutes. The claim-to-verdict mean reproduced 12.434 minutes. Baseline output: `artifacts/throughput-baseline.json`.

T-97 now durably preserves founder request #33's required real Claude Agent-tool behavioral measurement after T-67; its explicit T-67 dependency remains. T-87's documentation dependency will be reconciled at rollout, without changing permission files or waiving T-67/T-88 gates.

Independent validator: inspect the frozen T-95 spec and diff from main, run the full suite on the submitted head with isolated home/URL, and replay the installed deterministic verifier as needed. Preserve the ordinary independent verdict and landing gates. Deployment remains pending that verdict. Use `docs/throughput-operations.md` for cutover and comparable 24/72-hour measurement; this evidence does not claim a demonstrated 10× business throughput gain.
