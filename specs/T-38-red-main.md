# T-38 — main is red: T-25's spec guard rejects fixtures T-31 and T-14 landed with

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`main` is green again. Three server tests fail on `main` at `980b4bb`, consistently, with the server suite
run alone — not the load-dependent flake T-22 fixed.

```
TaskAttendedTests.Clearing_lifts_the_flag_and_the_conductor_staffs_it_again          422
TaskAttendedTests.The_conductor_skips_the_attended_task_and_staffs_the_one_beside_it 422
DoctorRoleTests.A_role_that_lapsed_while_a_task_waits_on_it_names_the_count          422
```

## Cause

T-25 landed `TaskService.RequireSpecBelongsToTask`, which refuses a spec path that does not exist on disk
inside the project's checkout:

```csharp
if (!File.Exists(full))
    throw Fail.Rule("spec_missing", $"No file at '{relative}' in {task.Project.RepoPath}. ...");
```

**That rule is correct and does not change.** It is the whole point of T-25.

T-25 also added `TestRepo.WriteSpec(taskId)`, which writes `specs/<id>.md` with a matching heading, and
updated the fixtures it could see — `ConductorTests`, `LifecycleTests`, `MessagingTests` — to use it. It
could not see `DoctorRoleTests` (T-14) or `TaskAttendedTests` (T-31), which were on other branches at the
time. Those two still pass a hardcoded path to a file that does not exist.

Every task involved was validated green in isolation. Main is red only because the guard and the fixtures
that predate it are now in one tree.

## Non-goals

- **Do not change anything under `src/`.** The guard is right. `git diff --stat src/` must print nothing.
- Do not relax, special-case or add an escape hatch to `RequireSpecBelongsToTask`.
- Do not change `TestRepo.WriteSpec`, or the three files T-25 already converted.
- Do not rewrite the affected tests' assertions. Only the fixture line that supplies the spec path changes.

## Design

Two call sites, both replacing a hardcoded path with a real file:

1. `tests/Muthur.Server.Tests/TaskAttendedTests.cs:55`, in `ValidatingTaskAsync`:

   ```csharp
   (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest($"specs/{task.Id}.md")))
   ```
   becomes
   ```csharp
   (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id))))
   ```

   The task id varies across that class's tests, so the id must be passed — `WriteSpec()`'s default of
   `T-1` would reintroduce the same failure for any task that is not `T-1`, and `WriteSpec` writes a heading
   naming the id, which the guard also checks.

2. `tests/Muthur.Server.Tests/DoctorRoleTests.cs:63`:

   ```csharp
   new SetSpecRequest("specs/T-1.md")
   ```
   becomes `new SetSpecRequest(_repo.WriteSpec(<the id that task actually has>))`. Read the surrounding
   test to find the id rather than assuming `T-1`; if it is `T-1`, `_repo.WriteSpec()` is fine. The field
   holding the `TestRepo` may be named differently in that class — use whatever it is.

No commit of the spec file is needed: the guard checks `File.Exists`, not git. The three converted fixtures
already rely on that.

## Units of work

### Unit A — the whole change
- **Files:** modified `tests/Muthur.Server.Tests/TaskAttendedTests.cs`,
  `tests/Muthur.Server.Tests/DoctorRoleTests.cs`
- **Does:** the two replacements above.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean; `dotnet test` green across the whole solution.
  - The three named tests pass when the server suite is run **alone**, which is how they fail today.
  - `git diff --stat src/` prints nothing.
  - `grep -rn '"spec", new SetSpecRequest' tests/` shows no remaining hardcoded path outside
    `SpecGuardTests.cs`, which passes literal paths deliberately because it is testing the guard itself.
    Report what that grep prints.

## Verification

```
dotnet build
dotnet test
dotnet test tests/Muthur.Server.Tests/Muthur.Server.Tests.csproj    # alone: this is how they fail today
```

## Out of scope / follow-ups

- This is a **semantic** collision: the branches involved merge cleanly, and `git merge-tree` exits 0 for
  every pair. T-16's board indicator would have shown nothing, correctly, right up until main went red. That
  limit belongs in T-16's record — a clean merge means "these will merge", not "these will pass" — and it is
  the first concrete case for the revisit the founder scheduled.

## Proof (2026-09-18)

```
dotnet test tests/Muthur.Server.Tests  (alone)   Failed: 0, Passed: 196
dotnet test  (whole solution)                    Failed: 0, Passed: 3939
dotnet build                                     0 warnings, 0 errors
git diff --stat src/                             empty
```

The server suite **alone** is the one that matters: that is how these three failed, so a green run there is
the evidence, not the full-solution run. The implementer also ran the three named tests under an explicit
filter — 3 passed — to rule out ordering luck.

`grep -rn '"spec", new SetSpecRequest' tests/` leaves no hardcoded path outside `SpecGuardTests.cs`, which
passes literals deliberately because it is the guard's own test — including `specs/typo.md`, which is
absent on purpose so the `spec_missing` refusal has something to refuse.

One accepted deviation, and it is a strengthening: in `DoctorRoleTests` the spec allowed a bare
`_repo.WriteSpec()` once the id turned out to be `T-1`. The implementer passed `task.Id` instead — by
definition the id that task actually has, it cannot drift if that class gains a test or ids renumber, and
it makes the two call sites read identically.

### A sharp edge left in place, deliberately

`TestRepo.WriteSpec(string taskId = "T-1")` has a default. A future fixture that calls `WriteSpec()` for a
task that is not `T-1` gets a call that looks right and fails only through the heading check — which is
half of what happened here. Making the parameter required would force the question at every call site.
That is a design change to shared test infrastructure and it is not this task's business; raised by the
implementer, recorded here, not actioned.
