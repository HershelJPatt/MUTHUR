# T-90 — Deterministic collision cache concurrency coverage

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, stop and report; do not improvise.

## Goal

Make the cache/semaphore tests independent of real Git execution during the measured pass, while retaining real-Git collision integration coverage and making its underlying process results visible in failure evidence. Production collision semantics and timeouts remain unchanged.

## Context

Base: main 83d82e0d35a1f70100ddd0f602d3de0c3891f4ee. The task branch is task/T-90-collision-cache. Read CLAUDE.md and muthur.project.json. CollisionTests currently gates its first process call, then delegates every call to ProcessRunner. CollisionService intentionally interprets merge-tree exit codes other than 1 as no collision. Branch lookup can also return no branch after a process failure. Thus a correct semaphore test can fail because its post-release external process fails. The original failed TRX has no ProcessResult, so its precise cause is not established.

Retained diagnostic evidence in C:/WorkSrc/MUTHUR/.work/overseer-0919/collision-process-trace.jsonl contains successful rev-parse and merge-tree results from subsequent diagnostic runs. A clean merge-tree took 4573 ms against a 5-second timeout; conflict calls returned exit 1 with shared.txt. This supports separating external-process behavior from cache behavior, but does not prove a timeout caused the original failure. Preserve this distinction in the implementation report.

## Non-goals

No production changes, new settings, API changes, retries, longer production timeouts, relaxed collision assertions, sleeps, test-suite serialization, or changes to global test infrastructure. No claim on another task. No push, default-branch merge, hub commands, or further delegation by the implementer.

## Design

1. Keep real TestRepo/HubFactory setup and existing real-Git integration cases: conflicting validating branches and card rendering, conflicting validated branches, same-file clean merge, and candidate filtering. Real-Git integration must still use ProcessRunner with the original timeout supplied by CollisionService.
2. Replace the real-backed gate with a test-local scripted IProcessRunner. Use it only for cache/semaphore and explicit process-result contract tests. Construct GitLander with that same runner, so rev-parse during the measured service pass is also scripted. Recognize the exact expected git rev-parse calls for the two fixture branches and return successful nonempty heads. Recognize the exact merge-tree arguments and return a configured ProcessResult, defaulting to exit 1 and output consisting of a tree oid, shared.txt, a blank line and the conflict message. Record calls, arguments and supplied timeouts; record unexpected calls and assert none outside CollisionService (which catches exceptions). Never fall back to real execution.
3. Gate merge-tree specifically with TaskCompletionSource using RunContinuationsAsynchronously. Expose entry and release; support arming a fresh gate for a later refresh. All waits have a 30-second hang-detector bound; observe cancellation in the gated runner. Every test releases the gate in finally and drains its active pass before fixture disposal, even if an assertion fails.
4. Convert A_second_call_inside_the_cache_window_runs_no_further_git_commands to use the scripted runner. Assert exactly one collision including both task ids and shared.txt; a fresh call returns the identical list and adds zero process calls; advancing the fake clock 61 seconds triggers exactly one further pass with the same collision (two branch reads and one merge-tree per pass).
5. Convert A_caller_that_arrives_mid_pass_takes_the_cached_answer_instead_of_queueing_behind_it to the scripted runner. Wait for merge-tree entry, assert the owning pass is incomplete, and assert a second call completes before release with an empty initial cache and no additional runner calls. Release and assert exactly the expected collision. A subsequent fresh call returns the identical completed list without process calls.
6. Add a warm-cache concurrency case: compute and retain a collision snapshot, advance the fake clock 61 seconds, arm the gate and start a refresh configured to return a clean merge. While blocked, the concurrent call returns the identical old collision list without another process call. After release the refresh is empty and is the new cached reference. This proves stale-cache fallback as well as cold-cache fallback.
7. Add a deterministic theory over merge-tree exit codes 0, 124 and 127: successful scripted branch lookups, stdout containing conflict-shaped output, but zero collisions because only exit 1 is evidence. Assert merge-tree was actually called once and received the unchanged 5-second timeout. This is a controlled demonstration of failure-to-empty semantics, not evidence that the original run timed out.
8. Extend the existing real counting runner to capture and write test-local xUnit output for every completed call: executable, arguments, working directory, elapsed milliseconds, requested timeout and full ProcessResult (exit code/stdout/stderr). Log thrown exceptions before rethrowing. Use ITestOutputHelper; no shared files, static tracing or secrets/environment dumps. Preserve assertions; diagnostics supplement them.

## Units of work

### Unit A — Deterministic tests and process diagnostics
- **Files:** tests/Muthur.Server.Tests/CollisionTests.cs only.
- **Does:** all design items above; no production edits.
- **Depends on:** frozen spec committed on task/T-90-collision-cache, based on main at the hash above. Verify actual git history before editing.
- **Acceptance:** build and focused CollisionTests pass; report exact commands, results, changed files, commit/branch, diagnostic interpretation, and any remaining concerns. Commit your work in your own worktree branch.

## Verification

From the relevant task/implementer worktree, unattended:

```powershell
dotnet build
dotnet test tests/Muthur.Server.Tests --no-build --filter FullyQualifiedName~CollisionTests --logger "trx;LogFileName=collision.trx" --results-directory artifacts/T-90/focused -v normal
```

The orchestrator additionally runs the complete solution once on the integrated branch:

```powershell
dotnet test --no-build --logger "trx;LogFilePrefix=T-90" --results-directory artifacts/T-90/full -v normal
```

All projects and tests must pass with no skips introduced. Inspect real-Git output in the focused TRX: conflict exit 1, clean exit 0, requested merge-tree timeout 5 seconds. Review source to confirm no real runner is reachable during cache tests' measured pass and no production file changed. A failing run is retained and investigated, never retried blindly. Validators run the same headless commands and review the cold/warm/cache assertions. Retain evidence under artifacts/T-90; clean task-owned temporary worktrees after landing, leaving unrelated files and worktrees alone.

## Out of scope / follow-ups

The original failure's exact process result cannot be recovered from a TRX that did not capture it. Do not claim historical certainty or change production behavior on that assumption.
