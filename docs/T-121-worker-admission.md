# Full-worker admission

`worker run` requires `--task`, even for older specs without `capabilities:`. Upgrade the CLI and hub together: an unavailable admission endpoint refuses dispatch. No worktree, adapter settings or worker scratch is created before a new admission grant.

Full workers, probes, orchestrators, validators and the overseer share the conductor's scheduling lock and session ceiling. Each actual child reserves its own slot; its parent remains counted. Each reservation also consumes one attempt in the task's existing rolling daily orchestrator bucket. Fallback candidates reserve separately. Release restores capacity only; setup failures, start failures and cancellation do not refund the attempt. Run reports do not charge another attempt. Existing budget reset events retain their meanings.

The hub records `worker.admitted` and `worker.released` transactionally. A run ID binds the original caller, task, candidate and SHA-256 input digest. Identical replay returns the original reservation with `mayExecute: false`; changed input returns `worker_run_conflict`. Neither a retry nor a lost response gives permission to launch. Probe run IDs remain a separate namespace and the probe API is unchanged.

The CLI freezes its prompt, assignment, allowed commands and requirements before admission. Executable absence and known capability mismatch skip without reservation. For a new worktree, capability inspection first uses the source checkout at the pinned revision and then verifies the created checkout after admission. Unknown or changed identity fails closed. The second check can consume the already-reserved daily attempt, but cannot launch a model using mismatched evidence.

Workers and setup subprocesses use Windows jobs. Processes start suspended, receive a kill-on-close job without breakaway, and only then resume. Only explicitly selected standard handles are inherited. The launcher waits for an empty job, including descendants, before releasing. Timeout and cancellation have a separate bounded cleanup interval. Unsupported platforms refuse execution. No hub identity reaches a worker.

If cleanup is uncertain or the release request fails, dispatch stops without fallback. The error and attempt report include the reservation ID. Hub restart, CLI death, claim expiry and task transitions never reclaim a reservation automatically. Inspect and recover explicitly:

```powershell
muthur worker reservations
# After independently establishing that the entire process tree is gone:
muthur worker release <reservation-id> --cleanup-confirmed
```

Only the original owner or Founder can release, including after task reassignment. Repeated confirmed release is idempotent. Founder can list all reservations; agents see only their own. Reservation records never contain credentials.

Verification: `dotnet build`, `dotnet test -v n`, then an isolated install and `scripts/verify-t121.ps1 -InstallPath <absolute-install-path>`. The installed verifier creates only synthetic model shims, a scratch repository and a loopback hub; its evidence is not a model capability pilot. Founder #53 authorizes T-121's already-admitted owner to implement directly without children, with separate conductor-staffed validation. It does not authorize a live installation or change any operational limits.
