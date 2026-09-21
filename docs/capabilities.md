# Demonstrated assignment capabilities

Opt in on a standalone line in the committed frozen spec:

```text
capabilities: shell, build, test, worktree-base, commit
```

Multiple lines form a union. Unknown keys, empty items and malformed declarations refuse dispatch.
Other accepted keys are `headless-interaction`, `connector-interaction`, `platform:<name>` and
`native-agent-tools`. They require their own evidence. A headless render, executable discovery or
success on another route never proves interaction. Existing specs without declarations retain their
current routing, including `needs: browser` and attended validation.

`muthur capability inspect --spec specs/T-n.md --base task/T-n --default-branch main --harness codex --launch-path worker-run`
reads the pinned committed spec and cache without mutation or model sessions. Inspection uses the
first matching implementer catalog candidate for worker-run and mastermind candidate for conductor
paths. Its identity includes that candidate's model/reasoning and the effective command configuration.
`worker-run`, `conductor-validator`, `conductor-orchestrator` and `native-subagent` never cross-satisfy.

Observations live under the selected `MUTHUR_HOME/capabilities`, in atomic per-identity JSON files.
Machine, harness/version, canonical repository root, base revision, launch path and configuration must
match ordinally. The configuration digest covers executable/prefix, command lists, model/reasoning,
Git environment and adapter-owned settings content hashes. Secret values are hashed in memory, never
stored as evidence. Only the launcher-appended `safe.directory` for its allocated worktree is represented
by a stable slot; inherited trust and other Git overrides stay exact. Settings are identified by scope
and content, so allocating an equivalent temporary worktree does not invent a configuration change.
The probe refuses if the disposable worktree's effective identity differs from the requested route.

Maximum lifetime is 24 hours; temporary failures last five minutes. Expired, missing, malformed,
unreadable or future-dated evidence refuses explicit requirements. Unknown adapters and unreadable
identity inputs do not reuse previous successes. Doctor reads counts and unknown coverage; even
`doctor --probe` never starts a model capability probe.

`muthur capability probe` takes the inspect options plus `--timeout-seconds` (default 90, maximum 120).
Production admission currently returns `capability_probe_admission_unavailable` for worker-run and
unsupported for other paths, with zero model starts. A follow-up to T-100 must connect the shared
worker account, catalog, budget and capacity reservation before enabling production admission.
No retries, concurrency increase, permission expansion or account provisioning are authorized here.

The bounded engine behind the admission interface is exercised with admitted fake runners. It runs
one fixed shell script in a disposable worktree pinned to the requested revision. A fresh nonce and
per-step receipts plus resulting artifacts/HEAD are required. The offline MSBuild fixture has explicit
Build and Test targets using SDK tasks, without package downloads or product code. The commit step
only commits a generated file in its own disposable repository. This proves shell/tool execution and
the SDK route, not that any arbitrary project's build or tests will work. Prose and process exit 0 alone
prove nothing. Admission leases must reap all descendants before release; worktree cleanup runs in
finally. Probe duration and starts are distinct from full sessions. A full session avoided is counted
only for a rejected full launch, never a successful probe or full session.

Recovery: inspect the exact identity and diagnostics, remove only its selected stale JSON record, then
request an explicit authorized re-probe after production admission exists. Do not delete the whole
cache, add permissions, switch accounts or claim availability from documentation. Until admission is
integrated, fixture-seeded observations are simulated evidence only and belong only in a scratch home.

T-94 owns the future browser integration seam. T-67/T-97 still require real native Agent-tool evidence
on the native-subagent path; an installed real Claude native pilot is unavailable in this slice. No
native, browser or platform capability is marked available by the bounded fixture.
