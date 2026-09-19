# T-50 — `muthur task spec` finds a spec committed on a task branch

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, the ordinary procedure-following case works: an orchestrator commits the frozen spec on its
task branch, runs `muthur task spec T-n specs/T-n-<slug>.md` from its worktree, and the hub attaches it.
Today that fails with `spec_missing` — "Commit the spec on the task branch first" — which asks for the thing
that was just done and sends the caller looking for a mistake they did not make.

The workaround in use is to copy the file into the shared main checkout, attach it, and delete it. That
writes a stray untracked file into a checkout a dozen agents share, where somebody else's `git add -A`
picks it up. It should not be load-bearing.

## Context

- `src/Muthur.Server/Services/TaskService.cs` — `SetSpecAsync` (line 124) and
  `RequireSpecBelongsToTask` (line 240). The guard resolves the path against
  `task.Project.RepoPath` and tests `File.Exists`, so it sees only the main checkout's working tree.
  `FirstHeadingTaskId` then opens that file to check the heading names this task.
- `src/Muthur.Server/Services/GitLander.cs` — `ITaskLander`, whose only implementation is `GitLander` over
  `IProcessRunner`. `BranchHeadAsync` is the shape to copy for a new read.
- `src/Muthur.Cli/Infrastructure/FileProvenance.cs` — already shells out to git from the CLI to describe a
  file's ref and commit, and is already used by `role define --brief-file`. `Describe(path).Ref` is exactly
  the branch the caller is standing on.
- `src/Muthur.Contracts/Tasks.cs` — `SetSpecRequest`; everything crossing HTTP is registered in
  `MuthurJsonContext` and the CLI is Native AOT.
- `tests/Muthur.Server.Tests/` — `TestRepo` builds real temp git repositories, so a branch-only file is
  something a test can actually create.

Constraints that are not obvious:

- `TaskService` does not take a lander today; it gains one.
- Anything that talks to git sits behind an interface so it can be faked.
- Warnings are errors.

## Non-goals

- **Enumerating worktrees** (`git worktree list`), T-50's other suggested direction. It is rejected: it would
  accept an uncommitted file sitting in somebody's scratch worktree, which is weaker than what the guard
  promises today. A spec is a frozen record; "committed on a branch" is the property worth checking, and it
  is the one the orchestrate procedure already produces.
- Changing the heading check, the inside-the-repository check, or when a spec may be attached.
- Reading the spec's *content* for anything other than the first heading. The hub still stores a path.

## Design

### The branch is a hint, and the hub looks in every place the spec could honestly be

`SetSpecRequest` gains a second, optional member:

```csharp
public sealed record SetSpecRequest(string Path, string? Branch = null);
```

`RequireSpecBelongsToTask` becomes async and resolves the spec's **content** from the first of these sources
that has the file, in order:

1. `request.Branch`, when given.
2. `task.Branch`, when set and not already tried — a bounced-back task re-attaching its spec.
3. The working tree at `RepoPath` — today's behaviour, and the founder's case when the spec is on `main`.

`Branch` is a hint, never an assertion: a source that does not have the file is passed over, not fatal. That
is what makes it safe for the CLI to fill in automatically, and it means no caller is worse off than today.

The inside-the-repository check still runs first, on the path, exactly as now — a branch read must not become
a way to name `../` out of the repo.

### Reading a file out of a branch

`ITaskLander` gains one method, implemented in `GitLander` beside `BranchHeadAsync`:

```csharp
/// <summary>The file's contents at the tip of <paramref name="branch"/>, or null when either is absent.</summary>
Task<string?> ReadFileAsync(Project project, string branch, string path, CancellationToken ct = default);
```

`git show <branch>:<path>` (equivalently `cat-file -p`), run in `project.RepoPath`. A non-zero exit — no such
branch, no such path on it — is `null`, not an error: "not there" is an answer, and the caller has other
places to look. The path is passed with forward slashes, which is what git wants on every platform.

### The messages say where it looked

`spec_missing` stops telling the caller to do what they just did, and names the places tried:

- Nothing anywhere, and a branch was in play:
  `No file at '<path>' in <repo>: not on branch '<branch>', and not in the working tree. If the spec is committed on a different branch, pass --branch <that branch>.`
- Nothing anywhere, no branch in play:
  `No file at '<path>' in <repo>. If the spec is committed on a task branch, pass --branch task/T-n-<slug>.`

`spec_outside_repository`, `spec_id_mismatch` and `spec_unreadable` are unchanged in code and text. The
heading check runs on whatever content was resolved, so a spec read out of a branch is checked exactly as a
working-tree one is.

### The CLI fills the branch in

`muthur task spec` gains `--branch <branch>`: "The branch the spec is committed on (default: the branch this
checkout is on)."

When `--branch` is absent, the CLI sends `FileProvenance.Describe(path)?.Ref`, which is the ref of the
repository holding the path — that is, the task branch, because the orchestrator runs this from its worktree.
A detached HEAD reports `HEAD`, which is not a branch name and is sent as null. Outside a repository
`Describe` returns null, and null is sent. Nothing about this can fail the command: the value is a hint.

## Units of work

### Unit A — the lander reads a blob
- **Files:** `src/Muthur.Server/Services/GitLander.cs`
- **Does:** `ReadFileAsync` on `ITaskLander` and `GitLander`, per Design.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean.

### Unit B — the guard resolves from a branch
- **Files:** `src/Muthur.Server/Services/TaskService.cs`, `src/Muthur.Contracts/Tasks.cs`
- **Does:** `SetSpecRequest.Branch`; `TaskService` takes an `ITaskLander`; `RequireSpecBelongsToTask` async
  with the resolution order and messages above; `FirstHeadingTaskId` reads resolved content rather than
  opening a path.
- **Depends on:** Unit A.
- **Acceptance:** Unit D's tests pass.

### Unit C — the CLI sends the branch
- **Files:** `src/Muthur.Cli/Commands/TaskCommands.cs`
- **Does:** `--branch`, defaulting to `FileProvenance.Describe(path)?.Ref`, `HEAD` treated as none.
- **Depends on:** Unit B.
- **Acceptance:** `dotnet build` clean; CLI tests green.

### Unit D — the tests
- **Files:** `tests/Muthur.Server.Tests/`
- **Does:**
  - A spec committed **only** on a task branch attaches when the branch is passed, and the stored `specPath`
    is the relative path.
  - The same spec attaches with no branch passed once the task has a branch of its own.
  - A spec in the working tree still attaches with no branch, and with a branch that does not have it — the
    hint is passed over, not fatal.
  - A heading naming another task is refused with `spec_id_mismatch` when read **out of a branch**, proving
    the check did not become source-dependent.
  - `spec_missing` names the branch it tried and no longer says "Commit the spec on the task branch first".
  - `../` outside the repository is still `spec_outside_repository` even with a branch passed.
- **Depends on:** Units A–C.
- **Acceptance:** each test fails with Unit B reverted.

## Verification

Headless; no browser.

```
dotnet build
dotnet test
```

Then, on an installed build against a scratch hub — never the live one — the case this task exists for:

```
git worktree add ../scratch-T-n -b task/T-n-x ; commit specs/T-n-x.md on it
muthur task spec T-n specs/T-n-x.md     # run from the worktree, no --branch
```

Passing is exit 0 and a `specPath` of `specs/T-n-x.md`, with the file never present in the main checkout.

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3708 Core, 23 Launch, 60 Cli, 275 Server — all green.
Four of the six new tests fail with the resolution reverted to working-tree-only, which is the whole
behaviour this task changes; the other two guard rules that must survive it.

### The case this task exists for, on an installed build

Build `9f998bc` installed to `artifacts/t50`, a scratch `MUTHUR_HOME` and a scratch project repository —
never the live hub. A project, a claimed task, and the spec committed **only** on a task branch in a
worktree, exactly as `kit/core/orchestrate.md` tells an orchestrator to work:

```
git worktree add ../proj-wt -b task/T-1-thing main
printf '# T-1 — Build the thing\n' > ../proj-wt/specs/T-1-thing.md ; git add -A ; git commit
test -f <main checkout>/specs/T-1-thing.md   ->  no

cd ../proj-wt
muthur task spec T-1 specs/T-1-thing.md      ->  exit 0, "specPath":"specs/T-1-thing.md"
```

No `--branch`, no copying anything into the shared checkout, and the file is genuinely not in it. The CLI
sent `task/T-1-thing` because that is the branch the worktree is standing on.

And when the spec really is nowhere, the message now says where it looked instead of asking for what the
caller has already done:

```
muthur task spec T-1 specs/typo.md
{"code":"spec_missing","message":"No file at 'specs/typo.md' in <repo>: not on branch 'task/T-1-thing',
 and not in the working tree. If the spec is committed on a different branch, pass --branch <that branch>."}
```

The failure this replaces was reproduced against the live hub one iteration earlier, attaching T-45's spec:
`No file at 'specs/T-45-validation-staffing.md' in C:\WorkSrc\MUTHUR. Commit the spec on the task branch first.`
— said of a spec that was committed on its task branch at the time.

### Note for whoever lands this

This task's own spec cannot be attached with `muthur task spec` until this change is running on the live
hub, for precisely the reason the task describes. That is the bug, not a gap in the work.
