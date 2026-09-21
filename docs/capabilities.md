# Demonstrated assignment capabilities

Opt in with a standalone line in the committed frozen spec:

```text
capabilities: shell, build, test, worktree-base, commit
```

Multiple lines union. Unknown keys and malformed lists refuse dispatch. Other keys are
`headless-interaction`, `connector-interaction`, `platform:<name>` and `native-agent-tools`.
These require independent evidence. Specs without declarations preserve existing routing,
including `needs: browser`; this is incremental opt-in.

`muthur capability inspect --spec specs/T-n.md --base task/T-n --default-branch main --harness codex --launch-path worker-run`
reads the committed spec and exact candidate identity without mutation or model sessions.
There must be exactly one matching catalog candidate. Inspect requires the checkout's effective
project/settings inputs to match the pinned base. An unknown identity is a refusal, never permission
to reuse an earlier success. First-slice conductor/native identity prediction is unsupported.
`worker-run`, `conductor-validator`, `conductor-orchestrator` and `native-subagent` never cross-satisfy.

Identity v2 covers machine, effective Windows SID or Unix UID, OS/architecture, canonical common
repository, base, resolved harness/version, permission mode, generated adapter settings, model/reasoning,
command rules, effective Git configuration, and resolved dotnet/pwsh/git versions and paths.
Configuration values are persisted only as digests. Relevant inherited PATH, PATHEXT, home/profile,
APPDATA, CODEX_HOME, CLAUDE_CONFIG_DIR, DOTNET_ROOT/DOTNET_ROOT_X64, DOTNET_CLI_HOME,
MSBuildSDKsPath, NuGet and TEMP/TMP inputs and project/ancestor toolchain settings are hashed.
Only launcher-added safe.directory and known adapter-generated workspace/output/scratch slots are
normalized. User paths, ancestor settings and permission rules remain exact. A Windows principal
or ambient configuration difference can make a probe worktree unknown; never widen permissions to
make it match. Missing, unreadable, oversized or malformed identity inputs fail closed.

Observations live under the selected `MUTHUR_HOME/capabilities` as atomic per-identity JSON files.
No cross-home cache exists. Maximum lifetime is 24 hours, or five minutes for transient failures.
Unknown, stale, unavailable and temporarily-failing evidence all refuse explicit requirements.
`muthur doctor` and `muthur doctor --offline` both report cached observation/stale/unknown counts
and unobserved coverage for capability checks without starting capability model probes.
`--offline` also skips checks that touch the network. Cache observations are not authoritative task state.

`muthur capability probe` takes the inspect options plus required `--task T-n` and optional
`--timeout-seconds` (default 90, range 1..120). T-113 shared admission checks the exact catalog candidate,
account, current mastermind ownership (or Founder), task daily budget and process ceiling. One request
permits at most one model start with no retries. Other launch paths reject before admission. Inspect
needs no reservation. Admission refusals retain their diagnostics; do not change accounts, budgets,
permissions or concurrency to work around them.

The admitted engine creates a disposable worktree pinned to the base and an immutable package-free
MSBuild fixture. The measured harness must execute individually visible shell commands, including
`dotnet build <fixture.proj> --no-restore` and `dotnet test <fixture.proj> --no-restore`.
Build writes a fresh nonce; VSTest asserts that output before writing its distinct nonce output.
Each exact native command and its exit receipt must be in the SAME shell-tool invocation. A later
shell cannot recover LASTEXITCODE. Prose, msbuild substitutions and exit zero alone prove nothing.
Receipts, immutable input hashes, resulting outputs and isolated commit HEAD are checked separately.
Commit affects only a generated file in a disposable repository, never the task/default branch.
This proves SDK command execution and a deterministic fixture assertion, not a framework test suite
or arbitrary product correctness. Partial failure does not manufacture another step's success.

Setup and execution share the selected budget, with separately bounded cleanup. Process exit and
worktree cleanup must finish before reservation release or cache publication. If cleanup is uncertain,
the reservation is retained: confirm process and worktree cleanup, then have the original caller or
Founder release that specific reservation through the existing admission API. Do not claim fake tests
prove hard-crash or arbitrary detached-descendant cleanup. Probe starts/overhead are separate from full
starts; only a rejected full launch counts as a full session avoided.

Recovery: inspect the exact identity and diagnostics, remove only its selected stale JSON record,
then explicitly request an authorized re-probe. Never delete the entire cache or seed live successes.
`scripts/Test-T100Capabilities.ps1 -Install <absolute-install-path> -Revision <candidate-head>` is an
orchestrator-only installed scratch check. It creates a private home, loopback hub and simulated harness;
fixture evidence is never a real-use observation. Workers write but do not execute this hub script.
The orchestrator separately records the authorized installed observation, window/cohort/sample/revision,
probe overhead and unavailable routes. No live-hub automated tests or paid fixture calls are permitted.

T-94 owns browser integration. T-67/T-97 still require actual native Agent-tool evidence; the real native
Claude pilot remains unavailable. No browser, connector, platform or native capability is established
by this fixture or by a host-only command experiment.
