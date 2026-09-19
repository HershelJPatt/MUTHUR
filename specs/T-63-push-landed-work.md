# T-63 — Landed work never leaves the machine: the hub merges but does not push

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a project the hub merges into is also a project the hub pushes. A background pass notices that
a merge-mode project's default branch is ahead of `origin` and pushes it — no force, ever — so work that
validated and landed reaches the remote without a person typing `git push`. A push that cannot go through is
not a line in a log: it is written on the project, told to the founder once, and shown as a failing doctor
check until it heals.

## Context

- `src/Muthur.Server/Services/GitLander.cs` holds every git invocation that writes to a default branch.
  `OpenPullRequestAsync` (line ~139) is the one place that already pushes, and `push_failed` is already its
  refusal code. `GitAsync` is the private helper every other call goes through.
- `src/Muthur.Server/Services/IngestService.cs` is the pattern to copy, closely:
  - `IngestService.PollAsync` does the outside-world work **outside** the ledger write lock, then records the
    outcome in a mutation, and guards itself with a `SemaphoreSlim` so two passes never overlap.
  - `SaveCursorAsync` records `ingest.failing` only on the transition into failure and `ingest.recovered` only
    on the transition out. That "once, on the edge" rule is exactly what this task needs, for the same reason.
  - `IngestWorker` at the bottom of the same file is the `BackgroundService` shape: pass, log, `Task.Delay` on
    the injected `TimeProvider`, first pass immediate.
- `src/Muthur.Server/Services/LeaseSweeper.cs` is the other hosted-service example and the one whose 30-second
  interval this task matches.
- `src/Muthur.Server/Services/DoctorIngestCheck.cs` shows a doctor check built from state the hub already
  stored: a `CheckDto(category, subject, status, detail, lastSuccess)` per subject, no probing.
- `src/Muthur.Server/Services/LifecycleService.cs` (~line 292) shows how the hub tells the founder something
  once: `MessageService.PostFromHub(m, Recipient.Founder, null, "<text>", taskId)` inside a mutation. An
  unread founder message is counted by `FounderAttention` and is what "reaches Needs You" means here.
- Background services are **off** in tests (`HubFactory` sets `Muthur:BackgroundServices=false`), so tests drive
  the pass by resolving the service and calling it, the way `TaskLeaseTests.Sweep_returns_lapsed_tasks_to_the_backlog`
  and `RoleTests.Sweep_frees_lapsed_holds_and_records_it` do.
- `LifecycleTests.Pr_mode_pushes_the_branch_and_opens_a_pull_request_instead_of_merging` shows how a test gets a
  real remote: a second `TestRepo` with `receive.denyCurrentBranch=ignore`, added as `origin`. Real git, no fake.
- Rules of the codebase that bite here: time comes from the injected `TimeProvider`; warnings are errors; every
  state change goes through `Ledger.MutateAsync` and records an event in the same transaction; nothing outside
  `Muthur.Launch` shells out except through `IProcessRunner`.

## Non-goals

- **The pull-request path.** `OpenPullRequestAsync` already pushes the task branch; it is not touched, and
  PR-mode projects are deliberately excluded from the new pass — whether their default branch moves is a
  human's business, not the hub's.
- **The deny lists.** `git push*` stays denied for every launched session. Nothing in
  `ValidatorSessionLauncher` or `OrchestratorSessionLauncher` changes.
- **A new role or tier.** No `release-oncall`, no new agent kind.
- **A new CLI command or contract.** No file under `src/Muthur.Contracts` or `src/Muthur.Cli` changes. Push
  health is visible through `muthur doctor`, the ledger, and the founder's inbox — all of which already exist.
- **Pulling, fetching, rebasing, or resolving a diverged remote.** If the remote moved, the hub reports it and
  stops. A human decides what to do about it.
- **Any use of `--force` or `--force-with-lease`.** Not a smaller version of it either.

## Design

### The decisions this task was asked to make, and the answers

1. **A landed-but-unpushed task is `done`.** The merge has already happened when a push would run, so refusing
   the land would be a lie about the default branch. `muthur task land` keeps its current behavior exactly:
   merge, record `task.landed`, task `done`. Nothing in `LifecycleService.LandAsync` or `GitLander.LandAsync`
   changes.
2. **A sweeper, not an inline push.** The push lives in a background pass on the `LeaseSweeper`/`IngestWorker`
   pattern, not inside `land`. It heals by itself after a network failure without waiting for another task to
   land, it covers a default branch that moved by any route, and it keeps `land`'s meaning ("the merge
   happened") honest and unchanged. The cost — `land` returns before the commit is on the remote — is paid
   back within one interval, and the pass runs immediately at hub startup, so commits stranded by a hub that
   was off are pushed as soon as it comes up.
3. **Never silent.** A failed push sets `Project.LastPushError`, records `project.push_failing` once, and posts
   one message to the founder. It stays a failing `muthur doctor` check until a push succeeds, which records
   `project.push_recovered` and posts one more message.
4. **A project with no remote is normal.** No `origin` means the pass does nothing, writes nothing, and records
   nothing. Silence is the correct output for a local-only repository.
5. **Never force, and never even ask.** The push is a plain `git push`, so a non-fast-forward is rejected by git
   and reported as a failure. It also runs with terminal and credential prompting disabled, so a remote that
   wants an answer fails fast instead of hanging an unattended hub.

### Schema — `Project` gains four columns

In `src/Muthur.Core/Entities/Entities.cs`, on `Project`, after `IngestSources`:

```csharp
/// <summary>The default-branch commit last confirmed on the remote, or null when none ever has been.</summary>
public string? LastPushedCommit { get; set; }
/// <summary>When <see cref="LastPushedCommit"/> reached the remote.</summary>
public DateTimeOffset? LastPushAt { get; set; }
/// <summary>When a push was last attempted, successfully or not. Paces the retry after a failure.</summary>
public DateTimeOffset? LastPushAttemptAt { get; set; }
/// <summary>Why the last attempt failed, or null when the last one worked. Non-null is a failing doctor check.</summary>
public string? LastPushError { get; set; }
```

All four are nullable, need no explicit EF configuration, and take a migration:

```
dotnet ef migrations add ProjectPushState -p src/Muthur.Data -s src/Muthur.Data -o Migrations
```

**Amendment 1 — generate the migration on a branch that is up to date with `main`.** A migration records
the one before it, and the model snapshot describes the whole chain, so a migration generated on a stale
base lands with the wrong predecessor and a snapshot that disagrees with the migrations beside it. The task
branch was rebased onto `main` before this unit started, and it must stay that way: if `main` gains another
migration while this task is in flight, rebase again and regenerate `ProjectPushState` rather than merging
the two chains by hand.

`ProjectDto` does **not** gain these. Nothing that crosses HTTP changes.

### `ITaskLander` gains a push

In `GitLander.cs`, beside `LandResult`:

```csharp
public enum PushOutcome
{
    /// <summary>The default branch reached the remote.</summary>
    Pushed,
    /// <summary>Nothing to do: no remote, no branch, or the remote already has this commit.</summary>
    Skipped,
    /// <summary>The remote refused it or could not be reached. Nothing was lost; a later pass tries again.</summary>
    Refused,
}

public sealed record PushResult(PushOutcome Outcome, string? Commit = null, string? Message = null);
```

and on `ITaskLander`:

```csharp
/// <summary>
/// Sends the project's default branch to `origin`, fast-forward only. A project with no remote, or one whose
/// remote already has the commit, is <see cref="PushOutcome.Skipped"/> — not a failure and not worth a word.
/// </summary>
Task<PushResult> PushDefaultBranchAsync(Project project, CancellationToken ct = default);
```

`GitLander.PushDefaultBranchAsync`, in order, all git run in `project.RepoPath`:

1. `Directory.Exists(project.RepoPath)` is false → `new PushResult(PushOutcome.Skipped)`. A missing repository
   is `DoctorRepoCheck`'s to shout about; this method does not duplicate it.
2. `git remote get-url origin` not `Ok` → `Skipped`. This is the local-only project, and it is not an error.
3. `git rev-parse --verify --quiet refs/heads/<DefaultBranch>` not `Ok` → `Skipped`.
   Its trimmed stdout is `local`.
4. `git rev-parse --verify --quiet refs/remotes/origin/<DefaultBranch>` — if `Ok` and its trimmed stdout equals
   `local` → `new PushResult(PushOutcome.Skipped, Commit: local)`. Nothing is ahead, so no network call happens
   on a quiet hub. A missing or stale tracking ref costs at most one redundant push, which git answers with
   "Everything up-to-date".
5. The push, which is the one call that does not go through `GitAsync` because it needs its environment:

```csharp
var push = await processes.RunAsync("git",
    ["-c", "credential.interactive=false", "push", "origin", $"refs/heads/{target}:refs/heads/{target}"],
    project.RepoPath, timeout: GitTimeout, ct: ct,
    environment: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
```

   `Ok` → `new PushResult(PushOutcome.Pushed, Commit: local)`.
   Not `Ok` → `new PushResult(PushOutcome.Refused, Commit: local, Message: push.Message)`.

No other argument. No `--force`, no `--force-with-lease`, no `--set-upstream`, no refspec other than the one above.

### `PushService` and `PushWorker`

New file `src/Muthur.Server/Services/PushService.cs`, holding both types, the way `IngestService.cs` holds
`IngestService` and `IngestWorker`.

```csharp
/// <summary>What one pass did. Errors are messages already safe to show the founder.</summary>
public sealed record PushPass(int Attempted, int Pushed, IReadOnlyList<string> Errors);

public sealed class PushService(Ledger ledger, ITaskLander lander, TimeProvider clock, ILogger<PushService> logger)
{
    /// <summary>How long a project that failed to push is left alone before the next attempt.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    public async Task<PushPass> PushAsync(CancellationToken ct = default)
}
```

One pass:

- Guard the whole method with a private `SemaphoreSlim _pushing = new(1, 1)` in a `try`/`finally`, as
  `IngestService` guards `PollAsync`.
- Read the candidates with `ledger.ReadAsync`: projects where `LandMode == LandMode.Merge`, ordered by `Key`.
- Take `now` from `clock.GetUtcNow()` once, before the loop.
- For each project, in order:
  - **Backoff:** if `LastPushError is not null` and `LastPushAttemptAt is { } last` and `now - last < RetryAfter`,
    skip it entirely — no git, no mutation, not counted in `Attempted`.
  - Otherwise `Attempted` counts it, and `await lander.PushDefaultBranchAsync(project, ct)` runs **outside**
    any mutation, like ingest's fetch.
  - `Skipped` **and** the stored `LastPushError` is null → no mutation at all. A quiet hub writes nothing.
  - Anything else → one `ledger.MutateAsync(Caller.System, …)` that re-loads the project by id from `m.Db`
    (the read above was untracked) and then:
    - sets `LastPushAttemptAt = m.Now`;
    - on `Pushed`: if `LastPushedCommit != result.Commit`, records
      `m.Record("project.pushed", payload: new { project = p.Key, branch = p.DefaultBranch, commit = result.Commit })`;
      then sets `LastPushedCommit = result.Commit` and `LastPushAt = m.Now`;
    - on `Pushed` or `Skipped`, when `LastPushError` was not null: records
      `m.Record("project.push_recovered", payload: new { project = p.Key, branch = p.DefaultBranch })`, posts the
      recovery message below, and clears `LastPushError` to null;
    - on `Refused`: when `LastPushError` was null, records
      `m.Record("project.push_failing", payload: new { project = p.Key, branch = p.DefaultBranch, error = result.Message })`
      and posts the failure message below; then sets `LastPushError = result.Message` either way (a second,
      different failure updates the text without a second event or a second message).
  - On `Pushed`, increment `Pushed` and `logger.LogInformation`. On `Refused`, add
    `$"{project.Key}: {result.Message}"` to `Errors` and `logger.LogWarning`.
- Let an exception out of `PushDefaultBranchAsync` end the pass; `PushWorker` logs it. Do not catch broadly
  inside the loop — every expected failure already arrives as `Refused`.

The two founder messages, posted with `MessageService.PostFromHub(m, Recipient.Founder, null, …, taskId: null)`,
worded exactly:

- Failing:
  `$"Landed work on '{p.Key}' is not reaching the remote. Pushing '{p.DefaultBranch}' from {p.RepoPath} failed: {error} The merge is safe on the local branch and the hub keeps trying; nothing reaches anyone else until it goes through."`
- Recovered:
  `$"'{p.Key}' is reaching the remote again: '{p.DefaultBranch}' is pushed."`

`PushWorker`, at the bottom of the same file, copying `LeaseSweeper` exactly in shape:

```csharp
/// <summary>Sends each merge-mode project's default branch to its remote. The first pass is the catch-up for whatever landed while the hub was off.</summary>
public sealed class PushWorker(IServiceProvider services, TimeProvider clock, ILogger<PushWorker> logger) : BackgroundService
```

with `private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);`, a `while (!stoppingToken.IsCancellationRequested)` loop that calls `services.GetRequiredService<PushService>().PushAsync(stoppingToken)`,
logs at information when `Pushed > 0`, catches `Exception ex when (ex is not OperationCanceledException)` and
logs it as an error, then `await Task.Delay(Interval, clock, stoppingToken)`.

### Registration

In `src/Muthur.Server/Infrastructure/Startup.cs`: `builder.Services.AddSingleton<PushService>();` with the other
singletons (beside `IngestService` reads well), and `builder.Services.AddHostedService<PushWorker>();` inside the
existing `if (options.BackgroundServices)` block, after `LeaseSweeper`.

### `DoctorPushCheck`

New file `src/Muthur.Server/Services/DoctorPushCheck.cs`. Reads only what the hub stored — no git, no network,
so the dashboard's Doctor panel can render it without probing.

```csharp
/// <summary>Whether work that landed in a merge-mode project is actually reaching its remote.</summary>
public sealed class DoctorPushCheck(Ledger ledger) : IDoctorCheck
```

For each `LandMode.Merge` project ordered by `Key`, category `"push"`, subject `project.Key`:

- `LastPushError is { } error` → `CheckStatus.Fail`, detail
  `$"Last push of '{branch}' failed: {error}"`, `LastSuccess` = `LastPushAt`.
- else `LastPushedCommit is not null` → `CheckStatus.Ok`, detail `$"'{branch}' is on the remote."`,
  `LastSuccess` = `LastPushAt`.
- else no line at all for that project: nothing has ever been pushed and nothing has ever failed, which is what
  a local-only repository looks like and is not worth a founder's attention.

Register it in `Startup.cs` immediately after `DoctorRepoCheck`, which is where it reads in the report.
The Doctor panel already refreshes on `project.*` events, so no Blazor file changes.

### What must not change

`GitLander.LandAsync`, `MergeAsync`, `MergeInCheckoutAsync`, `MergeWithoutCheckoutAsync`,
`OpenPullRequestAsync`, `LifecycleService.LandAsync`, every existing test in `LifecycleTests`.

## Units of work

### Unit A — the hub knows how to push
- **Files:** `src/Muthur.Core/Entities/Entities.cs`, `src/Muthur.Server/Services/GitLander.cs`,
  `src/Muthur.Data/Migrations/*` (generated)
- **Does:** the four `Project` columns and their migration; `PushOutcome`, `PushResult`,
  `ITaskLander.PushDefaultBranchAsync` and its `GitLander` implementation, exactly as the Design section spells
  out, in the same file and style as the merge paths beside them.
- **Depends on:** nothing
- **Acceptance:**
  - `dotnet build` clean (warnings are errors).
  - `dotnet test` — every existing test still passes; this unit adds none, because the behavior is exercised
    end to end in Unit B rather than through a harness built to reach one method.
  - The generated migration adds exactly the four columns to `Projects` and nothing else; the model snapshot is
    part of the commit.

### Unit B — the pass, the founder, the doctor
- **Files:** `src/Muthur.Server/Services/PushService.cs` (new),
  `src/Muthur.Server/Services/DoctorPushCheck.cs` (new),
  `src/Muthur.Server/Infrastructure/Startup.cs`,
  `tests/Muthur.Server.Tests/PushTests.cs` (new)
- **Does:** `PushService`, `PushWorker`, `DoctorPushCheck`, their registration, and the tests below.
- **Depends on:** Unit A
- **Acceptance:** `dotnet build` and `dotnet test` clean, with these tests in `PushTests.cs`, each using a real
  `TestRepo` as the remote (`receive.denyCurrentBranch=ignore`, added as `origin`) and calling
  `_hub.Services.GetRequiredService<PushService>().PushAsync()` directly, since background services are off in
  tests. No test sleeps; `_hub.Clock.Advance` is the only way time moves.

  1. `Landed_work_reaches_the_remote_on_the_next_pass` — land a task in a merge-mode project with an `origin`;
     before the pass the remote's `main` is behind; after one pass `remote.Git("rev-parse", "main")` equals the
     local `main`, the pass reports `Pushed == 1`, and a `project.pushed` event names the project and commit.
  2. `A_second_pass_with_nothing_ahead_does_nothing` — run the pass again: `Pushed == 0`, and there is still
     exactly one `project.pushed` event.
  3. `A_project_with_no_remote_is_left_alone` — no `origin`: the pass reports `Pushed == 0` and no errors, no
     `project.*` push event is recorded, and `muthur doctor` shows no `push` check for that project.
  4. `A_remote_that_moved_is_reported_and_never_forced` — advance the remote's `main` from a second clone so the
     push is rejected as non-fast-forward, then land and run a pass. The pass reports the project in `Errors`,
     one `project.push_failing` event is recorded, the founder has exactly one message naming the project, the
     remote's `main` is **unchanged**, and `muthur doctor` has a failing `push` check for the project.
  5. `A_failing_push_waits_out_the_backoff` — immediately after test 4's failure another pass reports
     `Attempted == 0`; after `Clock.Advance(PushService.RetryAfter)` a pass attempts it again, and the founder
     still has exactly one message about it (a repeated failure is not repeated news).
  6. `A_push_that_starts_working_again_says_so` — after test 4's failure, make the push possible again (merge
     the remote's `main` into the local one so the push fast-forwards), advance past the backoff, run a pass:
     the remote matches the local `main`, a `project.push_recovered` event is recorded, the founder has a second
     message, and the `push` doctor check is `ok`.
  7. `Pr_mode_projects_are_left_to_their_humans` — a `LandMode.Pr` project whose default branch is ahead of its
     remote: the pass reports `Attempted == 0` and the remote does not move.

## Verification

```
dotnet build
dotnet test
```

Both clean, from the repository root. No warnings (warnings are errors), no skipped tests.

End to end, on a scratch hub only — never against the live hub — with `MUTHUR_HOME` set to a scratch directory
and an installed CLI (`pwsh ./scripts/install.ps1 -Destination ./artifacts/t63`):

1. Make a throwaway git repository with a `main` branch, and a second one as its `origin`
   (`git init` the remote, `git config receive.denyCurrentBranch ignore`, `git remote add origin <path>`).
2. Register it as a merge-mode project, add a task, take it through claim → spec → implemented → validated, and
   `muthur task land` it. The task reports `done`.
3. Within about thirty seconds, without running any git command yourself, the remote's `main` matches the local
   `main`, and `muthur log` shows `project.pushed`.
4. Break it: point `origin` at a path that does not exist, land a second task, and within about a minute
   `muthur doctor` shows a failing `push` check for the project and `muthur msg inbox` as the founder has one
   message naming the project. It is not repeated on subsequent passes.
5. Fix `origin` back; within about five and a half minutes the branch is pushed, `muthur doctor` is `ok` again,
   and `project.push_recovered` is in the events.

Every command here runs with no browser, no GUI and no human.

## Out of scope / follow-ups

- A `muthur project push <key>` command to force a pass now rather than waiting out the backoff. The doctor
  check and the ledger make the state visible; making it promptable is a separate, small task.
- Remotes named something other than `origin`, and projects whose default branch tracks a differently-named
  remote branch. Every project this organization has uses `origin` and matching names.
- A diverged remote healing itself (fetch, rebase, re-merge). The hub reports and stops, on purpose.
- Push health on the Projects dashboard page, which today shows no ingest health either. If that page grows a
  health column, push belongs in it.
