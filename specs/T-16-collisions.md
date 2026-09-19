# T-16 — Two tasks in flight on the same files

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, nothing prevents two tasks from running on the same files — that is the decision, made on
purpose — and two things exist that do not exist today: a conflicting land leaves a **structured, countable
record** in the ledger, and the board **shows** when two in-flight tasks are on a collision course, computed
by trying the merge rather than guessing from filenames.

## The decision, and the measurement behind it

Founder request #7, answered 2026-09-18. The choice was **option 3, explicitly time-boxed** — do nothing to
prevent collisions, make the bounce cheap, and revisit with a rule once the conductor really runs several
orchestrators at once:

> Bare Option 3 risks being read later as "this was settled"; the fourth says what was settled and under
> what conditions. […] For the revisit to be possible, the collision rate has to be recorded. Today a merge
> conflict bounces the task back to its owner and the fact leaves no trace anyone can count — which is
> exactly why you had to reconstruct history with `git merge-tree` to answer this at all.

### The numbers

Measured on this repository on 2026-09-18, recorded here so the next person does not redo the work and so a
revisit argues from data rather than intuition:

| Population | Pairs | Share ≥1 file | Actually conflict |
|---|---|---|---|
| 8 tasks in flight at the time | 28 | 11 (39%) | **0** |
| 19 tasks already landed | 171 | 58 (33%) | not measured (history rewritten by each land) |
| 19 landed, at *source-area* granularity | 171 | 132 (77%) | — |

Files shared by in-flight tasks without conflicting: `src/Muthur.Server/wwwroot/app.css` (four tasks),
`tests/Muthur.Server.Tests/ConductorTests.cs` (three), `tests/Muthur.Server.Tests/HubFactory.cs` (two),
`src/Muthur.Contracts/MuthurJsonContext.cs` (two), `src/Muthur.Contracts/Routes.cs` (two).

**Sharing a file is a poor predictor of conflicting.** Declared paths would have serialized 11 pairs of work
to prevent zero conflicts; at area granularity it would have blocked 77% of all pairs. That is why options 1
and 2 were rejected.

### The method

```
git diff --name-only main...<branch>              # what each in-flight branch touches
git merge-tree --write-tree --name-only <a> <b>   # exit 1 = conflict, exit 0 = clean
```

The detector was validated before being trusted: a deliberately conflicting pair exits 1, a clean pair
exits 0. Do not skip that check if you re-run this.

### The caveat, which is part of the decision

The sample is two orchestrators who each knew what the other was doing, on a repository whose hot files are
small and append-shaped (a CSS rule appended, a `[JsonSerializable]` line added, a test class gaining a
method). Five or ten conductor-staffed orchestrators coordinating through nothing may collide at a rate
this sample cannot predict. **That is what the recording in Unit A is for.** The revisit is expected, not
hypothetical.

## Non-goals

- **Nothing blocks or defers staffing.** No declared paths, no observed paths, no conductor check. If you
  find yourself adding a reason not to staff a task, you have misread the decision.
- Do not change how a conflicting land behaves: the task already returns to its owner `in_progress` with a
  fresh lease, and `task.land_failed` is already recorded. Only the *payload* gains structure.
- Do not change `LandOutcome`, the 409/exit-3 contract, or `ConflictMessage`'s prose. Agents read that
  message today.
- The board indicator is advisory. It must never prevent an action, gate a control, or change a task's
  state.
- Do not add collision checks to `muthur doctor` (T-14/T-27) or to the conductor.

## Design

### Unit A — a conflicting land is countable

Today `LifecycleService` records `task.land_failed` with `{ code, message }`, where `message` is prose with
the conflicting files embedded and **capped at 12**. A rate is not queryable from that.

In `src/Muthur.Server/Services/GitLander.cs`:

1. Add two members to `LandResult`:

   ```csharp
   /// <summary>Files that conflicted, on a <see cref="LandOutcome.Conflict"/> result. Uncapped.</summary>
   IReadOnlyList<string>? Files = null,
   /// <summary>Task ids landed on the target since this branch diverged — the candidates it collided with. Best effort.</summary>
   IReadOnlyList<string>? LandedSince = null
   ```

   Both are optional trailing parameters on the existing positional record, so no existing construction site
   changes.

2. Populate them in **both** conflict paths (`MergeInCheckoutAsync` and the no-checkout path around
   line 102). `Files` is the same list `ConflictMessage` already receives, split and trimmed, **not** capped.

3. `LandedSince` is computed best-effort, in the conflict paths only:

   ```
   git merge-base <branch> <target>
   git log --format=%s <mergeBase>..<target>
   ```

   From each subject matching `^Land (T-\d+):` take the captured id. Order newest-first as `git log` gives
   them. If either git call fails, `LandedSince` is an empty list — it is evidence, not a contract, and a
   land must never fail because this could not be computed. `Land <id>: <title>` is MUTHUR's own merge
   subject, written by `MergeAsync`; a subject that does not match is simply skipped.

In `src/Muthur.Server/Services/LifecycleService.cs`, the `case LandOutcome.Conflict:` arm records:

```csharp
m.Record("task.land_failed", current.Id, new
{
    code = result.Code,
    result.Message,
    branch = current.Branch,
    target = current.Project!.DefaultBranch,
    files = result.Files ?? [],
    landedSince = result.LandedSince ?? [],
});
```

Nothing else in that arm changes. `task.land_refused` is untouched.

### Unit B — the board shows a collision course

New file `src/Muthur.Server/Services/CollisionService.cs`.

```csharp
/// <summary>Two in-flight tasks whose branches no longer merge into each other.</summary>
public sealed record Collision(string TaskA, string TaskB, IReadOnlyList<string> Files);
```

`CollisionService(ITaskLander lander, IProcessRunner processes, Ledger ledger, TimeProvider clock)`
exposes one method:

(This signature first also took `MuthurOptions`. Every bound in the service is a constant, so the parameter
was unread and `CS9113` failed the build — warnings are errors. Corrected; add it back the day a bound
becomes configurable.)

```csharp
/// <summary>Pairs of in-flight tasks that would conflict, recomputed at most once per cache window.</summary>
public Task<IReadOnlyList<Collision>> CurrentAsync(CancellationToken ct = default);
```

Behaviour, exactly:

- **Which tasks.** Tasks in `in_progress`, `validating` **or `validated`** with a non-empty `Branch`,
  grouped by project. Only pairs within the same project are compared. Read through `Ledger.ReadAsync`,
  never `MuthurDb` directly from a component.

  (`validated` was missing from the first version of this spec and was found in the end-to-end run, where
  both colliding tasks sat in `validated` and were therefore never compared. It is the **highest-stakes**
  state of the three: a validated task is queued to land, so two conflicting ones are not a future risk but
  a bounce that is about to happen — which is exactly the moment the board should say so. `blocked` stays
  out on purpose: it waits on a human and rarely has a branch yet.)
- **The test.** For each pair, in that project's `RepoPath`:
  `git merge-tree --write-tree --name-only <branchA> <branchB>`. **Exit 1 means they conflict**; the
  conflicting paths are the lines of stdout that name files. Exit 0 means clean. **Any other exit code means
  git could not answer** — a missing ref, unrelated histories, a git too old for `--name-only`, or
  `IProcessRunner`'s own 124 (timeout) and 127 (could not start) — and the pair is skipped, showing nothing.
  A branch that does not exist is skipped, not an error.

  (This first said "exit non-zero means they conflict", contradicting the Method section above, which said
  `exit 1 = conflict`. Unit B's specialist caught it and built the precise reading. The loose one is
  dangerous, not merely imprecise: on a hub where `git` is not on PATH every pair would exit 127 and the
  board would paint a red `collides:` pill on every in-flight task — exactly the false positive this whole
  task exists to avoid. `GitLander` already tests `ExitCode == 1` in the same place.)
- **Cost control**, because a dashboard must never be able to hold the hub open:
  - Results are cached for **60 seconds** on the injected `TimeProvider`. A call inside that window returns
    the cached list without running git.
  - A `SemaphoreSlim(1,1)` guards recomputation: concurrent callers get the current cached value rather
    than queueing behind a second computation.
  - At most **12 in-flight branches** are considered (66 pairs): ordered by task id, tasks past the twelfth
    are not compared. The cap is global, not per project, so the ceiling holds however many projects exist.

    (This first read "beyond that, return the empty list", which taken literally switches the indicator off
    at thirteen in-flight tasks — when the organization is busiest, and plausibly soon, since eight were in
    flight when the measurement was taken. Unit B's specialist read it as a stable `LIMIT 12` and said so;
    that is the intent, and the "order by task id so the selection is stable" sentence only means anything
    under that reading.)
  - The whole computation is bounded by a **10 second** budget; when it expires, return what was computed
    so far. Each git call gets `TimeSpan.FromSeconds(5)` through `IProcessRunner`, matching `GitLander`'s
    existing use of a timeout.
  - On any failure, return the previous cached value if there is one, otherwise empty, **and stamp the
    cache with that answer**. A collision panel that cannot compute shows nothing; it never shows an error
    and never throws into a render.

    (The stamping was added by Unit B's specialist and is not optional: without it, a repeatable failure —
    a failing database read, say — is retried on *every* render, which is a dashboard holding the hub open.
    That is the one thing this design exists to prevent.)

Register it in `Startup.AddMuthur` as a singleton, beside `ConductorService`.

**The indicator.** In `src/Muthur.Server/Components/Shared/TaskCard.razor`, add an optional parameter
`[Parameter] public IReadOnlyList<string>? CollidesWith { get; set; }`. When it is non-empty, render after
the existing pills:

```razor
<span class="pill pill-collision" title="Branches conflict: rebase before landing">collides: @string.Join(", ", CollidesWith)</span>
```

In `src/Muthur.Server/Components/Panels/BoardPanel.razor`, load collisions in `LoadAsync` alongside the
tasks and pass each card the ids it collides with. `BoardPanel` already inherits `LivePanel`; do not change
its `RefreshInterval`. Because `CurrentAsync` is cached, a burst of ledger events costs at most one git run
per minute.

In `src/Muthur.Server/wwwroot/app.css`, after the other `pill-*` rules:

```css
.pill-collision { color: var(--red); border-color: rgba(229, 96, 90, .4); background: var(--red-bg); }
```

Red, unlike T-5's amber `pill-ungated`: an ungated project is a choice, a collision is work about to be
redone.

## Units of work

The two units share no file and neither depends on the other.

### Unit A — a conflicting land is countable
- **Files:** modified `src/Muthur.Server/Services/GitLander.cs`,
  `src/Muthur.Server/Services/LifecycleService.cs`; tests in
  `tests/Muthur.Server.Tests/LandConflictTests.cs` (new)
- **Does:** the Unit A section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean, `dotnet test` green.
  - A test that lands a task whose branch really conflicts (build it with `TestRepo`, which already makes
    real temp git repositories) and asserts the `task.land_failed` event payload carries `branch`, `target`,
    a `files` array containing the conflicting path, and a `landedSince` array containing the id of the task
    that landed first.
  - A test that a land conflict where `landedSince` cannot be computed still records the event and still
    returns 409 — the evidence is best-effort, the behaviour is not.
  - The existing land tests still pass unchanged; `ConflictMessage`'s prose is byte-identical.

### Unit B — the board shows a collision course
- **Files:** created `src/Muthur.Server/Services/CollisionService.cs`; modified
  `src/Muthur.Server/Infrastructure/Startup.cs`,
  `src/Muthur.Server/Components/Shared/TaskCard.razor`,
  `src/Muthur.Server/Components/Panels/BoardPanel.razor`,
  `src/Muthur.Server/wwwroot/app.css`; tests in
  `tests/Muthur.Server.Tests/CollisionTests.cs` (new)
- **Does:** the Unit B section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean, `dotnet test` green.
  - Two in-flight tasks on branches that really conflict (via `TestRepo`): `CurrentAsync` returns one
    `Collision` naming both task ids and the conflicting file, and the card markup rendered with those ids
    carries `pill-collision`.

    **The "`/` renders `pill-collision`" half of this cannot be asserted in a test, and a validator must not
    treat it as covered.** `BoardPanel` renders its cards inside `<Virtualize>`, which emits no items during
    a prerender because it sizes its window from a JS measurement the test harness never makes — so `GET /`
    contains neither the pill nor the task ids at all. (No existing test asserts card content on `/`
    either, which is consistent.) Assert the two ends instead: `CurrentAsync`'s result, and `TaskCard`
    rendered through `HtmlRenderer` with the ids from that real `Collision`. Loading `GET /` in both the
    colliding and non-colliding tests still exercises `BoardPanel.LoadAsync` → `CollisionService` and the
    id-mapping for real, since wrong wiring would 500. **Whether the pill is genuinely on screen is proven
    only by step 1 of the end-to-end Verification below.**
  - Two in-flight tasks that touch the *same file* without conflicting return **no** collision. This is the
    assertion that matters most — it is the whole finding this task is built on.
  - A second call inside the cache window runs no further git commands. Prove it by counting invocations on
    a fake `IProcessRunner`, not by timing.
  - Tasks in `backlog` or `done`, and tasks with no branch, are never compared.
  - No inline `style`; `pill-collision` is defined in `app.css`.

## Verification

```
dotnet build
dotnet test
```

End to end, against a scratch hub — never the live one. Install to `./artifacts/t16`, use a scratch
`MUTHUR_HOME` and `MUTHUR_URL`, create a project on a temp repository, then:

1. Two tasks, two branches that edit the same line of the same file, both `implemented`. Open `/` — both
   cards show a red `collides:` pill naming the other.
2. Land the first. It succeeds. Land the second: exit 3, `merge_conflict`, and the task returns to its owner.
3. `muthur events --limit 50` shows `task.land_failed` whose payload carries `branch`, `target`, a `files`
   array with the conflicting path, and `landedSince` containing the first task's id.
4. Two tasks that touch the same file without conflicting show **no** pill, and both land cleanly.

Step 4 is the one a validator should not skip: an indicator that fires on shared files rather than real
conflicts would be worse than none, because it would recreate by suggestion the serialization this task
exists to avoid.

## Out of scope / follow-ups

- **The revisit.** Once the conductor staffs several orchestrators at once, query `task.land_failed` for the
  collision rate and decide whether a rule is now worth its cost. That is a new task, and it is expected.

  **Read `landedSince` knowing its bias**, found by Unit A's implementer while building it. It is derived
  from merge subjects matching `^Land (T-\d+):`, which is the subject `MergeAsync` writes — so it only sees
  tasks landed by MUTHUR in `Merge` mode. A project on `LandMode.Pr` lands through a human merge on the
  forge, whose subject will not match, and `landedSince` there will be empty even when a collision really
  happened. `files`, `branch` and `target` are unaffected, so the *rate* stays sound; it is the *pairing*
  that is blind to PR-mode projects. Inherent to the method frozen above, not a defect in the build, and
  worth knowing before the revisit reads a low number for a PR-mode repository as an absence of collisions.
- `git merge-tree --write-tree` writes loose objects into the project's object store. Harmless and collected
  by `git gc`, but worth knowing before someone is surprised by it in a repository under inspection.
- A collision between a task branch and the default branch (rather than between two task branches) is not
  shown. It is the case Unit A already records on land, and showing it would mean testing every in-flight
  branch against a target that moves.

  **This is not hypothetical: it happened during this task.** T-22 passed validation, and its land bounced
  against `main` because T-30 had landed first and refactored the same file. The board would not have warned
  about it, because the conflict was with the target rather than with another in-flight branch. Whether that
  is worth showing — and what it would cost to test every branch against a moving target — is the first
  thing the revisit should weigh, ahead of any rule about prevention.

- **The indicator cannot see a semantic collision, and one broke `main` the same afternoon.** This is the
  most important limit of the instrument and it was found by the instrument's own author being wrong about
  what it measures.

  T-25 landed a rule requiring a task's spec file to exist on disk. T-31 and T-14 landed fixtures that pass
  a path to a file that does not exist. All three were validated green in isolation; `git merge-tree` exits
  0 for every pair, at every moment before and after each landed; T-16's board would have shown nothing,
  correctly, right up until `main` went red with three failing tests (T-38).

  So the measurement this task rests on — 28 in-flight pairs, 11 sharing a file, zero conflicting — was
  measuring **textual** conflict, and the class that actually broke the build that day was invisible to it.
  `corner` put the correction precisely: *a clean `merge-tree` means "these will merge", not "these will
  pass".*

  The honest upgrade is to dry-run the merge and then run the tests against the merged tree. That is far too
  expensive per pair on a board refresh — N-squared merges and N-squared test runs — but it is cheap at
  **land** time, where it is one merge and one test run, and where a red result is exactly what a land
  should refuse. That is the concrete thing the founder's scheduled revisit should weigh, and it changes the
  question from "do these branches conflict" to "does one task's new rule invalidate another's assumptions".

- **A `blocked` task that carries a branch is invisible to the indicator.** Flagged by Unit B's specialist
  rather than left to be discovered. The exclusion is deliberate and mostly safe, for a reason worth
  recording: a task bounced back from a failed land goes to `in_progress`, not `blocked`, so the bounce path
  has no blind spot. The gap is only a task that reached `in_progress` with a branch and then hit a founder
  request. Known edge, not a surprise.

## Proof (2026-09-18)

```
dotnet build   0 warnings, 0 errors
dotnet test    19 + 9 + 3692 + 156, all passing
```

**Unit A, end to end on a scratch hub.** Two tasks on branches conflicting on one line, first landed, second
bounced:

```
exit 3  merge_conflict  'task/B-beta' no longer merges cleanly into 'main' (conflicts: shared.txt)

task.land_failed payload:
  branch:       "task/B-beta"
  target:       "main"
  files:        ["shared.txt"]
  landedSince:  ["T-1"]
```

That is the pairing the founder asked to be countable, produced by a real conflict rather than a fixture.

**Unit B, end to end, as far as a terminal can prove it.** Both tasks in `validated` on a scratch hub,
`GET /` returns 200, and the project repository's loose-object count grows across the request — which only
`merge-tree --write-tree` does. So `BoardPanel` → `CollisionService` → git really runs, against the state
that the first version of this spec was skipping.

**What is *not* proven, and a validator must not assume it is:** that the pill appears on screen.
`BoardPanel` renders inside `<Virtualize>`, which emits nothing during a prerender, so `GET /` contains
neither `pill-collision` nor the task ids. Confirmed with curl. The markup is covered by rendering
`TaskCard` through `HtmlRenderer` with ids from a real `Collision`, and the wiring by the object-count
evidence above — but *on-screen* needs a browser, which this orchestrator does not have. **This is the
second task in a row whose verification needs eyes on a page** (T-14 was the first, and its restaffing loop
is what T-30 and T-31 exist for). Worth flagging `muthur task attended` on it.

**Confirmation the indicator does not fire on shared files**, which is the whole argument of this task:
`Two_tasks_on_the_same_file_that_still_merge_are_not_a_collision` builds a 60-line file and two branches
editing lines 2 and 58, asserts with `git diff --name-only main...<branch>` that both really touch it, then
asserts no collision and no `pill-collision` on `/`.

Unit B's specialist did not take a passing test as proof of the `validated` fix: it reverted the filter to
the two original states, confirmed the new test fails with `The collection was empty` and nothing else in
the class does, then restored it. The test is precisely load-bearing for that regression.

## State at handover (2026-09-19)

**Feature-complete and building clean. Eleven tests fail, for a known and precedented reason that is not
this task's defect.** Branch `task/T-16-collisions`, merged up to `main` as of this note, pushed.

### What is done

Both units, integrated and reviewed:

- **Unit A** — a conflicting land is countable. `LandResult` gained `Files` (uncapped) and `LandedSince`
  (best-effort task ids landed on the target since the branch diverged), populated in both conflict paths of
  `GitLander`; `LifecycleService` records `branch`, `target`, `files` and `landedSince` in
  `task.land_failed`. `ConflictMessage`'s prose is byte-identical and still caps its list at 12.
- **Unit B** — `CollisionService` finds pairs of in-flight tasks whose branches genuinely fail to merge
  (`git merge-tree --write-tree --name-only`, **exit 1 only**), cached 60s on the injected clock,
  single-flight semaphore, 12-branch ceiling, 10s budget, failures cached so a repeatable failure costs one
  attempt a minute rather than one per render. `TaskCard` renders a red `pill-collision`.

Both were proven end to end on a scratch hub before main moved: a real bounce produced
`files: ["shared.txt"], landedSince: ["T-1"]`, and `GET /` grew the project repo's loose-object count,
which only `merge-tree --write-tree` does.

### Why eleven tests fail right now

All eleven fail with **HTTP 422**, and it is the T-38 defect exactly, one branch later.

T-25 landed `TaskService.RequireSpecBelongsToTask`, which refuses a spec path that does not exist on disk in
the project's checkout. `LandConflictTests` and `CollisionTests` were written before that guard and call the
`spec` action with a hardcoded path to a file nobody wrote. They were green when written; they are red now
because the guard is beside them.

**The guard is correct and must not be relaxed.** This is the same semantic collision T-38 fixed for
`TaskAttendedTests` and `DoctorRoleTests` — and it is, incidentally, the exact failure class this task's own
spec records as invisible to its board indicator.

### The fix, which is mechanical and has a precedent

Do what T-38 did, in `tests/Muthur.Server.Tests/LandConflictTests.cs` and
`tests/Muthur.Server.Tests/CollisionTests.cs`: replace each hardcoded spec path with
`_repo.WriteSpec(task.Id)`. `TestRepo.WriteSpec` already exists and writes `specs/<id>.md` with a heading
naming that task — **pass the id**, because the guard checks the heading too and `WriteSpec()`'s `T-1`
default would only half-fix it. See `git show <T-38's land commit> -- tests/` for the two-line shape.

Nothing under `src/` should change.

### One thing already done for you

Main replaced `ITaskLander.BranchExistsAsync` with `BranchHeadAsync` while this branch was out.
`CollisionService`'s single call site is adapted (`... is not null`) and the build is clean. That was a
mechanical merge fix, not a design change.

### What is still unverifiable, and is not the fix above

The `pill-collision` has never been seen on a live `/` by anyone. `BoardPanel` renders inside `<Virtualize>`,
which emits nothing during a prerender, so no test can assert it and `curl` shows nothing. The markup is
covered via `HtmlRenderer` and the wiring by the loose-object evidence. **This task needs a browser-capable
validator**, the same as T-5 — and `muthur task attended` cannot be set because the running hub is still the
`54c455d` build and returns 404 for that route. Reinstalling the live hub is a founder action.

### What I would do next

1. Apply the `WriteSpec(task.Id)` fix to the two test files; expect all eleven to go green.
2. `dotnet build` and `dotnet test`; the rest of the suite was green before this merge.
3. `muthur task implemented T-16 --branch task/T-16-collisions`.
4. Flag it attended once the hub is upgraded, for the same reason as T-5.

## Pickup (2026-09-19, top-right)

The handover's fix was applied verbatim: `LandConflictTests` and `CollisionTests` now pass
`_repo.WriteSpec(task.Id)` to the `spec` action instead of a hardcoded `specs/{id}.md` that nobody wrote.
Nothing under `src/` changed. `main` was merged in first (T-36, clean).

`dotnet build`: clean, 0 warnings. `dotnet test`: Core 3708, Launch 23, Cli 55, Server 244 — all green.

### One flake, named rather than buried

On the first full-suite run, `A_caller_that_arrives_mid_pass_takes_the_cached_answer_instead_of_queueing_behind_it`
failed alone with `Assert.Single() Failure: The collection was empty`, and took 14s to do it. It passes in
isolation and passed on the immediately following full run.

The cause is real time, and it is the service's own design: `GitTimeout` is 5 seconds of wall clock per git
call, and that test is the only one that serializes a real `git merge-tree` behind a gate while 243 other
tests hammer the same machine. A git call that overruns 5s returns 124, `ConflictAsync` correctly reads
"anything but exit 1 is not evidence", and the pass reports no collision — exactly what the panel is
supposed to do when git will not answer in time. The production behaviour is right; the test inherits its
wall clock, so under enough load it can observe the miss.

It is not worth loosening `GitTimeout` for: that constant is the whole reason a slow repository cannot hold
a render. If this recurs, the honest fix is to let the test inject a longer per-call timeout into its own
`GatedProcessRunner` — a hang-detector budget, which the test rules permit — not to change the service.

## Rebase after the land bounce (2026-09-19, top-right)

`watch` validated this by hand at `1149f2c` with a real browser — all four spec steps, including step 4, the
one that matters most: two tasks touching the same file 35 lines apart show no pill. Their note on the
bounce was right in every particular:

> the only conflict is `src/Muthur.Server/wwwroot/app.css` … T-45 landed after you marked T-16 implemented
> and both add a pill rule to the same block … this is a rebase and a re-verify, not a rethink.

Main had moved 174 commits. Merged rather than rebased, which is what this branch's history already does —
15 commits with a merge already in them do not replay cleanly, and the alternative is resolving the same
conflicts fifteen times.

One conflict, two adjacent lines, no disagreement: `.pill-collision` (this task) and `.pill-ungated` (T-5).
Both kept.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 175 Cli, 394 Server — all green.
