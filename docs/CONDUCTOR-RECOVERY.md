# Conductor recovery

Account exhaustion now places the shared account out of rotation for at least one hour.
It never clears or shortens a longer known limit. While all mastermind accounts are
limited, the conductor starts no sessions and displays the earliest retry time.
This fallback does not guess a provider's reset timestamp from ambiguous prose.

`muthur task dependencies T-74 --after T-67 --reason "Needs the permission fix and real probe"`
parks a backlog task (or returns an owned in-progress task to backlog), preserving its
spec and branch. All prerequisites must be done before claims or staffing proceed.
Cycles and cross-project links are rejected. A cancelled prerequisite keeps work parked
until the dependency is explicitly changed. `--clear` removes prerequisites. Founder
requests retain their separate blocked state and human gate.

The launcher renews task and role leases every 30 seconds while its child remains
running. Process completion, timeout or cancellation stops renewal. Revoked identity
stops the child rather than extending authority. Existing session timeouts still apply.

Conductor-started orchestrators clean up before submitting work for validation, record
their final branch/head/checks report and exit. This frees capacity for validators.
Approved work owned by an exited conductor session is eligible for the existing landing
path immediately, without waiting for its heartbeat to become stale. A running session
still prevents automatic landing. Attended owners retain their prior stale-owner rule.
The dashboard shows approved work's age and why landing is waiting; conflicts still
return through the existing land/bounce path and validation requirements are unchanged.

Dependency completion resets the affected daily staffing budget. Account backoff and
dependencies prevent unproductive attempts before that budget is consumed. The ceiling
of two concurrent sessions is unchanged; the per-task/per-role daily guard remains three.
