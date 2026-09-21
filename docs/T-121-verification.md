# T-121 implementation verification

The resumed owner session continued `task/T-121-worker-admission` from product commit `d5ad15c36cb33633f753e041cd4e659ba7a82d47`. The branch contained the completed admission implementation beyond the ledger's last implementation note. Founder request #53 authorized this already-admitted session to finish directly, without implementation children. Independent validation and landing remain conductor responsibilities.

Review covered the frozen spec, all implementation changes, shared scheduling and daily accounting, replay and original-owner recovery, CLI admission before setup, sequential fallback, Windows job containment, and the synthetic verification script. Existing ceilings, daily budgets, account limits, reviewer eligibility and probe APIs are preserved. T-108 remains dependent on this repair and a verified supported installed route; this verification is not evidence of real routing quality.

The resumed build (`scripts/check-t121.ps1 -Check build`) passed with zero warnings and zero errors. The existing isolated AOT installation identifies its CLI as `0.1.0+d5ad15c36cb33633f753e041cd4e659ba7a82d47`. Running `scripts/verify-t121.ps1` against that installation on 2026-09-21 passed all 41 assertions with zero cleanup failures. Evidence: `artifacts/t121-resumed-installed-evidence.json` in the implementation worktree.

Installed assertions cover legacy specs, missing-task refusal before worktree creation, two synthetic fallback attempts with separate reservations, confirmed process cleanup and release, shared worker/probe capacity, daily and account denial, input conflict, non-executing replay, hub restart retention, explicit idempotent recovery, and original CLI death. Forced CLI death terminated its synthetic worker through job containment while preserving the durable reservation. The scratch hub, repository and synthetic processes were cleaned by the verifier.

The initial resumed full-suite run exposed a timeout in the existing capability-probe fixture's setup `git commit` (exit 124). A bounded full-suite rerun uses `scripts/check-t121.ps1 -Check test -Serialized`, preserving all assertions and deadlines while running one project at a time with four processors exposed to the test runtime. This is a local test scheduling option, not a MUTHUR capacity or budget change.

Final full-suite result and cleanup evidence are recorded below before submission.
