# Repeatable local verification

Use an installed CLI. No hub connection or model call is needed by `verify`:

```powershell
muthur verify run --repo C:\src\MUTHUR --ref <full-commit> --spec specs/T-107.md --recipe muthur --output C:\evidence\new-run --cache C:\evidence\cache
muthur verify cleanup --output C:\evidence\new-run
```

`--repo`, `--ref`, `--spec`, `--recipe`, `--output` and `--cache` are required. Output must be new. Output and cache must not intersect each other, Git metadata, or source; ignored `artifacts` children are allowed. Writable roots and their ancestors cannot be symlinks/reparse points. `--task`, `--parent-run` and GUID `--subject` are caller-provided references, not hub-verified approvals. Every run receives a new GUID. `--timeout-minutes` defaults to 60 and permits 1–60. `--no-cache` neither restores nor publishes an entry.

Exit codes are 0 success, 2 invalid input, 1 execution/cleanup failure, 124 timeout and 130 cancellation. Machine JSON reports run ID, status, evidence path, cache outcome, exit code and an optional error, including required-option parse failures. A persisted running bundle is never success. Tests, installed smoke and cleanup must succeed before any new cache success is published. Independent validator and task-specific checks are still required; this evidence is not an approval and is not automatically attached to T-99 subjects.

## Committed recipe

`muthur.project.json.verification` describes the only supported recipe. Missing fields, extra fields, changed commands and unknown versions are refused. Existing top-level `build` and `test` values are unchanged.

| Field | Version 1 value and meaning |
| --- | --- |
| `version`, `name` | `1`, `muthur` |
| `inputs` | `["**"]`: the entire captured Git tree, frozen spec and recipe |
| `buildConfiguration` | `Debug`, the fixed `dotnet build` default |
| `preparationConfiguration` | `Release`, fixed by the pinned installer |
| `timeoutMinutes` | 1–60; can only shorten the command budget |
| `preparation` | `pwsh -NoProfile -File scripts/install.ps1 -Destination {install}` |
| `build` | `dotnet build` |
| `test` | `dotnet test -v n` |
| `up`, `status`, `down` | `{install}/muthur.exe` with the corresponding single argument |
| `readinessPath` | `/`; the server has no `/health` endpoint |
| `outputs` | `muthur.exe`, `server/Muthur.Server.dll`, `kit` |

Commands contain a `file` string and `arguments` array. `{install}` is the only placeholder and expands to this run's installation directory. Arguments are never evaluated as shell text. No configured traversal, arbitrary commands, ambient build overrides or fallback recipes are supported. The current installer emits provenance to stdout rather than a provenance file; its preparation log is preserved.

The ref is captured once; execution uses a detached checkout with that exact clean HEAD. The run owns its home, profile, .NET CLI home, APPDATA, LOCALAPPDATA and temporary directories and uses a fresh loopback port. Child environments are rebuilt from the explicit toolchain allowlist in `VerificationEnvironment`; hub tokens, agents and arbitrary cloud credentials are not inherited. `PROCESSOR_ARCHITECTURE` is inherited and hashed as toolchain identity because the [.NET SDK 10.0.204 Windows installer initializer](https://raw.githubusercontent.com/dotnet/sdk/v10.0.204/src/Cli/dotnet/Installer/Windows/InstallerBase.cs) requires it. `ProgramData` is inherited when present and hashed as toolchain identity because Visual Studio installation discovery needs this machine-wide non-secret path. No credentials or configuration are copied, and access permissions are unchanged. Prerequisite failures are reported, not repaired by broadening permissions.

Real Windows commands enter a run-owned kill-on-close Job Object during process creation, before suspended child code can run. Detached servers and descendants remain contained even after intermediate parents exit. Ownership and containment identity are persisted before command launch; cancellation stops the job and waits for it to empty. A hard-killed supervisor closes the non-inherited job handle, terminating its descendants.

## Cache and evidence

Only installation files are reused. Build and the entire test suite run before every lookup, including hits. Every restored file is hashed, then copied to the fresh installation and verified again. Missing, extra, modified, malformed, case-colliding and reparse-point entries are rejected. Homes, credentials, databases, logs, NuGet stores and scratch are never cache payloads. Per-key exclusive file handles serialize readers/writers/eviction and release on process death. Staging is private and records its producing process identity. Publication uses a directory rename after successful smoke and cleanup. Eviction retains three completed entries by completion age and skips busy entries.

The key covers repository/common-Git identity, full commit/tree, spec and recipe digests, schema/runner identity, OS/architecture, executable hashes/version probes, hashed allowlisted environment values and Native AOT toolchain content identities. Run-owned paths and the loopback URL use stable policy placeholders in identity and probe output, so unique scratch directories do not cause misses. Inherited toolchain values remain hashed. SDK, Visual Studio VC tools, Windows SDK and Native AOT compiler packages must be inspectable. Set `NUGET_PACKAGES` to the existing local package store when reusable native compiler identity is required; it is an allowed toolchain input, never a cache payload. Unknown identity disables reuse with a reason; fresh checks still execute. Conservative misses are intentional. Hashes detect accidental corruption, not malicious same-user rewriting. The cache and ownership metadata assume a trusted local user.

`evidence.json` is atomically replaced after stages. It records complete commands, timestamps, duration, relative working directories, environment policy, per-stage exit/status/log references, input manifest, cache producer, installed-file hashes, separate test-start/test-completion flags and cleanup results. Credential-shaped log values are redacted. `spec.bin` preserves the committed bytes copied directly from the single matching regular entry in a Git ZIP archive, without text decoding or archive path extraction; the temporary `spec.zip` is deleted. `ownership.json` records run paths, loopback URL and PID/start/executable identities. Successful evidence is written only after cleanup and cache finalization.

To recover from corruption, rerun normally for rejection/rebuild, use `--no-cache`, or remove a cache directory when no verification process is using it. Never remove active lock files. For an interrupted run use `verify cleanup`; it refuses an active runner, verifies recorded roots and process identities, preserves evidence and marks a running bundle interrupted. Valid contained runs recover through their recorded job identity, including interruption before a server PID is recorded or after an intermediate parent exits. Missing or ambiguous ownership refuses deletion with instructions. Owned scratch deletion first checks the complete tree for reparse points, then clears only the read-only attribute on regular files, including nested Git objects left by cancelled tests. Cleanup observes cancellation and is idempotent after successful recovery.

## Installed pilot and required gates

After independent full build/test and a pinned installation, run:

```powershell
pwsh -NoProfile -File scripts/verify-T-107.ps1 -InstallPath <absolute-install> -Repository <repo> -Ref <full-sha> -OutputRoot <new-absolute-directory>
```

The pilot performs three real recipe runs: miss, verified hit with fresh full tests/smoke, and deliberately corrupted artifact rejection/rebuild. It also starts two concurrent installed containment fixtures with observable barriers and detached children. Cancelling one must stop its child while the other remains alive; hard-killing the second supervisor must stop its child. Both then undergo idempotent interrupted cleanup. Automated tests exercise cancellation using named-pipe barriers, without sleep-based synchronization. The script preserves evidence and writes cohort `T-107-preparation-v1`, UTC window, sample count, revisions, setup/preparation durations, outcomes, defects, execution model-call count and missing real-world data to `pilot.json`. These are observations, not a controlled speedup claim. Summarize large logs with the installed `muthur utility summarize` before reading/citing them.

Worker build/test access failures do not waive the independent orchestrator gates: `dotnet build`, `dotnet test -v n`, test-temp cleanup, pinned installation and this pilot must all pass before submission. The worker never starts a development CLI against a live hub.

## Post-landing follow-up

Required follow-up: [record the landed installed revision and first live-use measurement](#post-landing-follow-up), linked from the conductor's landing report. Until those observations exist, the pilot is synthetic local verification; it makes no claim about live-use latency, adoption or production reliability.
