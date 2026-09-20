# T-64 provenance verification

## Decision and baseline

The frozen T-64 spec records founder request 37 (ledger event 4309): retain the shared strict refusal, treat T-64 as covered by T-62 after verifying current main and recording evidence, and use normal independent validation. No browser override is introduced. This report uses that recorded decision; it did not query a hub.

- Main verified locally: `e513f5aa3f0d6b7589e34b97c168ef18af69f55a`.
- T-62 head: `13d9d8ecbe625a51e089339621500763b1f13290`; `git merge-base --is-ancestor 13d9d8e e513f5aa3f0d6b7589e34b97c168ef18af69f55a` exited 0.
- Frozen local base: `task/T-64-provenance` at `6ee63dece44a6b78f5d0aad25199777d5ce31604`.
- Unit branch: `task/T-64-provenance-tests-v2`; initial and verification HEAD are the frozen base SHA. No reset, merge, rebase, or branch switch was needed. Launcher will commit the two new files.
- Worktree root and clean initial status were verified. The spec was verified as a committed blob and read through the named base after rechecking its SHA.
- `git diff e513f5aa3f0d6b7589e34b97c168ef18af69f55a HEAD -- src` was empty: the production implementation inspected here is the implementation on the named main.

## Coverage

| Requirement | Source | Test or inspection evidence |
|---|---|---|
| Server provenance can be faked without a new abstraction | `src/Muthur.Server/Services/BriefFileReader.cs` takes the existing `Muthur.Launch.IProcessRunner` and calls `RepoFile.IsDirtyAsync` | New `BriefFileReaderProcessTests` directly constructs the reader with a private queued fake; only real temporary files are created, with no git repository or git process |
| Modified brief refused before tracked-file lookup | `BriefFileReader.ReadAsync`, `RepoFile.IsDirtyAsync` | `A_modified_brief_is_refused_without_a_tracked_file_probe`: `brief_file_dirty`, `RuleViolation`, relative filename, exactly two probes |
| Untracked brief refused despite empty status | Same shared rule | `An_untracked_brief_is_refused_after_an_empty_status`: failed tracked lookup returns the same refusal |
| Clean tracked brief returns exact text | `BriefFileReader.ReadAsync` | `A_clean_tracked_brief_returns_the_exact_file_text`, including original line endings |
| Invalid input rejected before external calls | `BriefFileReader.ReadAsync` and `Match` | Four `Invalid_input_is_refused_before_any_process_call` cases: empty/whitespace input, empty roots, missing file; existing error codes and `RuleViolation` |
| Bounded literal git probes, caller cancellation token preserved | `RepoFile.GitAsync` and `IsDirtyAsync` | Every applicable fake probe asserts executable, exact argument sequence, file directory, ten-second timeout and supplied noncancelled token; filename contains brackets and a space. Recorded call counts and exhausted queues detect unexpected/missing calls outside the production exception fallback |
| Actual git semantics and containment retained | `RepoFile`, `BriefFileReader.Match` | Existing `RepoFileTests`, `BriefFileReaderTests`, and `FileProvenanceTests`: committed/modified/staged/untracked, outside-git, sibling isolation, containment and first-match refusal |
| Console reads before role definition and service resolves | `Console.razor` injects `Briefs` and awaits `ReadAsync` before `Roles.DefineAsync`; `Startup.cs` registers reader | Source inspection; existing `BriefFileReaderTests.The_console_page_still_renders_with_the_brief_reader_injected` performs HTTP GET `/console`; existing `DashboardConsoleRolesTests` exercise the console controls |
| CLI shares the rule; server does not reference CLI | `src/Muthur.Cli/Infrastructure/FileProvenance.cs` delegates to `RepoFile`; `Muthur.Server.csproj` references Data and Launch only | Source/project inspection and existing `FileProvenanceTests` |
| Production behavior unchanged | Unit A adds only this document and `tests/Muthur.Server.Tests/BriefFileReaderProcessTests.cs` | Final product diff and status checks |

These existing tests and production files are inherited from T-62. Their executions listed below are new T-64 verification, not assumed historical results. No historical T-62 manual CLI or browser verification is claimed.

## Checks run

Commands run from the unit worktree on 2026-09-20 with .NET SDK 10.0.204:

| Exact command | Result |
|---|---|
| `dotnet build` | Exit 0; 0 warnings, 0 errors |
| `dotnet test` | Exit 0; 4,747 passed, 0 failed, 0 skipped: Core 3,736; Launch 54; CLI 272; Server 685 |
| `dotnet test tests/Muthur.Server.Tests --no-build --filter "FullyQualifiedName~BriefFileReader|FullyQualifiedName~DashboardConsoleRoles" -v minimal` | Retry exit 0; 36 passed, 0 failed, 0 skipped, including all 7 new fake-process cases |
| `dotnet test tests/Muthur.Launch.Tests --no-build --filter FullyQualifiedName~RepoFile -v minimal` | Exit 0; 13 passed, 0 failed, 0 skipped |
| `dotnet test tests/Muthur.Cli.Tests --no-build --filter FullyQualifiedName~FileProvenance -v minimal` | Exit 0; 8 passed, 0 failed, 0 skipped |

The first sandboxed `dotnet build` failed during restore with ten access errors reading the user NuGet configuration (zero compiler warnings). The identical command passed with approved access to the local build environment. Test commands use that same approved access.

The first `dotnet test tests/Muthur.Server.Tests --no-build --filter "FullyQualifiedName~BriefFileReader|FullyQualifiedName~DashboardConsoleRoles" -v minimal` run overlapped the full suite and exited 1: 35 passed, 1 failed, 0 skipped. The unchanged `BriefFileReaderTests.A_dirty_brief_in_the_first_repository_does_not_fall_through_to_the_second` expected a refusal but received none. All seven new fake-process cases passed. This observation alone does not establish why the real-git probe failed to refuse; no product or test workaround was applied.

The full suite subsequently completed successfully, including the unchanged real-git test that failed in the overlapping filtered run. Repeating the exact server filter after all other verification commands from this unit had completed passed all 36 tests in 12 seconds, with no code changes. The initial failure remains recorded above rather than being treated as a proven timeout. Large command output is retained locally in `%TEMP%/T-64-build.log`, `T-64-test.log`, `T-64-server.log`, `T-64-server-retry.log`, `T-64-launch.log`, and `T-64-cli.log`.

Final `git diff --exit-code HEAD -- src` exited 0 with no product diff. Status lists only the two assigned new files; HEAD and the named base still resolve to the dispatch SHA. Build and test logs from successful runs contain no compiler warnings. Changes remain uncommitted for the launcher, as explicitly directed by the dispatch.

## Limits and cleanup

Strict refusal is retained for detected dirty/untracked files inside git. Outside-git paths and failed initial git probes preserve the existing best-effort behavior; this is not a universal guarantee that every readable brief has a commit. Existing git-failure and containment semantics are unchanged.

The fake tests establish process wiring and bounded execution, while existing real-git tests establish git semantics. They do not exercise browser interaction. The HTTP render test checks injection and rendering, not a browser submission; call ordering is also inspected in source. No browser, live hub, hub CLI, push, or main merge was used.

Each new test owns one unique temporary directory and deletes it in `Dispose`; cancellation token sources are disposed. The new fake starts no child process. After verification, no `muthur-brief-process-*` directories remained in the temporary directory, and every verification command had exited. No unrelated processes or shared test directories were cleaned. No new interface, route, schema, provenance display, or product change is required, and no Unit A acceptance requirement is knowingly uncovered.
