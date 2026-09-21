# T-113 — Unit B final-review corrections

Frozen follow-up to specs/T-113.md. Only build this unit when explicitly dispatched after Unit A. Task branch task/T-113-probe-admission; default main. Preserve all admission, capacity and budget behavior.

## Problem
The callback contract requires bounded execution but currently cannot report failed process cleanup without either returning (which unconditionally frees the slot) or never returning. Provide an explicit fail-closed outcome so a bounded callback can exit while retaining uncertain capacity.

## Exact changes
- In src/Muthur.Launch/ProbeReservationRunner.cs, add public sealed ProbeCleanupUncertainException deriving Exception, with constructor (string message, Exception? innerException = null).
- A callback throwing this exception means process cleanup is unconfirmed. RunAsync must NOT call ReleaseAsync. Instead throw an InvalidOperationException whose message contains the reservation ID and says it is retained pending confirmed process cleanup, with the original ProbeCleanupUncertainException as InnerException. Preserve the reservation so normal explicit owner/Founder recovery remains possible. Do not loop or retry. Ordinary callback exceptions still mean cleanup completed under the callback contract and still run normal finally-release.
- Check caller cancellation inside the release-protected try before invoking callback. A cancellation arriving after a successful admission must release without invoking callback. This does not change false-grant replay behavior: replay neither invokes nor releases.
- Update docs/T-113-probe-admission.md and XML callback documentation: replace the instruction to never return on uncertain cleanup with the typed outcome above. Hard-crash/unknown state remains fail-closed; no auto-reclaim.
- Add deterministic Launch tests: uncertainty causes zero release calls, carries the reservation ID and original exception; cancellation after successful admission causes zero callbacks and exactly one release with an uncancelled independent token. Preserve existing replay, cancellation, ordinary failure, and aggregate release-failure tests.
- Do not make unrelated changes. Build/test attempts must be honest if the worker sandbox cannot read user NuGet.Config. Do not spend repeated attempts bypassing that sandbox; the orchestrator can independently verify in the authorized environment.

## Verification
Worker: dotnet build; dotnet test -v n. Orchestrator: independent build/full suite and installed scratch API smoke per original frozen spec, then review final diff. No hub commands for worker. Commit only this unit's changes; do not merge/push.
