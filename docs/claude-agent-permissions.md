# Claude Agent-tool delegation depends on harness permission mode

Native Claude Agent-tool delegation remains supported. Its ability to build, test and commit depends on
the initiating harness permission mode and approval availability. T-87's original "delegation is dead"
headline is an explicitly rejected historical conclusion, not the current diagnosis.

## Historical evidence

Founder request #28, dated 2026-09-19 (ledger 2250), reports five prior real Agent-tool units: three for
T-59, one for T-60 and one for T-69. They built and ran full test suites; the founder independently reran
the integrated suites with matching counts. The decision supplied no numerical test counts. T-69 also
installed the CLI and used a scratch hub. Several prior reports recorded initial NuGet sandbox denials
followed by tool-approved successful retries. These are founder-reported historical evidence, not new
measurements made by this documentation task.

T-57 historically reported that read-only git worked while dotnet and mutating git were denied in that
unattended session. That outcome does not establish the cause or outcome for every session, or that all
prompt requests always deny. An unattended denial is an observed configuration outcome; it does not
prove universal native delegation failure. Conversely, read-only success and static settings inspection
cannot establish build, test or commit capability.

## Separate permission paths

Native Agent-tool permission inheritance belongs to the initiating harness. MUTHUR does not set, record
or observe that native permission mode and ships no allowlist for native Agent-tool subagents.
`WorkerCommands.DefaultAllowed` belongs to separately launched workers. Worker-run allowlists and
adapter rendering are a separate path from native Agent-tool provisioning; worker success does not
prove native behavior.

T-67 covers shell rendering and rails. It does not grant a native Agent-tool allowlist, and this document
does not claim that it has landed. Post-T-67 native behavior is not yet measured. Founder request #33,
dated 2026-09-20 (ledger 3928), permits this documentation to land ahead of runtime measurement; it
authorizes no permission-provisioning policy.

When encountering a permission gap, record the actual commands attempted and their allow/deny and
approval outcomes. Report blocked verification honestly and preserve artifacts. Do not widen
permissions to make a unit pass. Existing native isolation, base/SHA/clean-tree guards and worker
launcher guidance remain in force.

## Deferred measurement: T-97

T-97 is the durable follow-up under parent T-87. Its explicit ledger dependency on T-67 has already been
verified by the orchestrator. After T-67 lands, T-97 must run one real Claude Agent-tool unit in the
founder's normal permission mode and record:

- Installed revision, harness and version, permission mode, and allocated worktree.
- Actual attempted commands, allow/deny outcomes, and approval outcomes.
- Build and test counts, commit behavior, and preserved artifacts.
- Limitations and independent verification.

Preserve the historical baseline separately from that new measurement. Codex, worker-run and static
source results cannot substitute for it; this documentation unit's build/test results are not Claude
Agent-tool measurement evidence. T-67's own required Claude probe is not waived by T-87 or T-97.

Bring any allowlist provisioning proposal to the founder with this evidence via
`muthur ask --kind human`. No agent or hub may expand permissions on its own authority; the permission
policy decision remains the founder's.
