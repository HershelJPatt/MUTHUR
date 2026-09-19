# T-25 — specs/ filenames stop colliding with reused ledger ids

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, `specs/` holds every frozen spec this organization has ever written, under names that
cannot collide; the three specs already destroyed by a collision are back; and `muthur task spec` refuses a
file whose first heading names a different task, so the next collision is caught before the commit rather
than after the push.

## What is actually wrong, verified

`specs/` is keyed by task id and the ledger's ids have been reused. The ledger's done set is T-1, T-2, T-4,
T-11, T-12; the other id-named files under `specs/` are from a generation of work that predates this ledger,
and every one of those ids now belongs to a different live task.

The task body calls the overwrite a risk. **It has already happened three times, not twice, and it is on
`origin/main`.** Each of these files was replaced in place by a spec for a different piece of work:

| File | Original subject | Last commit holding it | Replaced by |
|---|---|---|---|
| `specs/T-4.md` | "Needs you" page: answer founder requests and talk to agents from the dashboard | `41e65ce` | Discord ingest |
| `specs/T-11.md` | Polish from validation reports | `64d8760` | The conductor staffs validation |
| `specs/T-12.md` | Follow-ups from the T-6 / T-7 validation | `72d5807` | The kit installed into MUTHUR itself |

All three originals are intact in git history at the commits named above. Nothing is unrecoverable, and
this task recovers them.

The mechanism is that `TaskService.SetSpecAsync` is weaker than the task body assumes. It does not check
that the file exists — it checks only that the path is relative:

```csharp
if (string.IsNullOrWhiteSpace(request.Path) || Path.IsPathRooted(request.Path))
    throw Fail.Rule("relative_path_required", "The spec path must be relative to the project repository.");
```

So `muthur task spec T-8 specs/T-8.md` attaches a spec about `down` to a task about `kit.json` and says
nothing, and `muthur task spec T-8 specs/typo.md` attaches a path to no file at all and also says nothing.

## The founder's decision

Founder request #3, answered 2026-09-18:

> **Option A.** Keep them, renamed in place to `<id>-<slug>.md` taken from their own first heading (e.g.
> `specs/T-9.md`, the outbound gate, becomes `specs/T-9-outbound-gate.md`). They stay beside the current
> specs, greppable, and the collision is gone. Cheap, reversible, and nothing needs a new convention.

Not open for reinterpretation. In particular: **nothing is deleted.**

## Context

- `src/Muthur.Server/Services/TaskService.cs`, `SetSpecAsync` — the whole of the change in Unit A.
- `src/Muthur.Core/Entities/Entities.cs` — `Project.RepoPath` is where a project's checkout lives; the spec
  path is relative to it.
- `src/Muthur.Server/Services/DoctorProjectCheck.cs` (landed with T-14) — the pattern for a service reading
  a file out of `project.RepoPath` and failing on it politely.
- `tests/Muthur.Server.Tests/` — `TestRepo` creates real temporary git repositories, which is what Unit A's
  tests need in order to have a `RepoPath` with files in it.

Constraints that are not obvious:

- Services throw `MuthurException` via `Fail.*` with a stable `code`; rule violation → 422/exit 2.
- Time comes from the injected `TimeProvider`.
- Warnings are errors.

## Non-goals

- Renaming `specs/T-1.md`, `specs/T-2.md`, `specs/T-4.md`, `specs/T-11.md`, `specs/T-12.md` or
  `specs/T-1-readme.md`. Those hold the **current** generation's specs, their ids are correct, and the live
  ledger's `specPath` still points at them — `muthur task show T-11` would dangle if they moved. They stay.
- Any change to how a spec is written, or to `specs/_TEMPLATE.md`.
- Making `muthur task spec` verify anything about a spec's *content* beyond its first heading.
- Preventing id reuse itself. Ids repeat because the ledger was rebuilt; whether that happens again is a
  founder's decision about their own ledger, not a rule this task can impose.

## Design

### Unit A — `muthur task spec` refuses a spec that is not this task's

`TaskService.SetSpecAsync` gains three checks, after the existing `relative_path_required` check and before
the state check. All three need the project, which `LoadAsync` already includes.

Resolve the path once:

```csharp
var relative = request.Path.Replace('\\', '/');
var full = Path.GetFullPath(Path.Combine(task.Project!.RepoPath, relative));
```

1. **Escaping the repository.** If `full` is not inside `Path.GetFullPath(task.Project.RepoPath)` —
   compare with `StringComparison.OrdinalIgnoreCase` on Windows, and require a directory separator at the
   boundary so `/repo-evil` does not count as inside `/repo` — throw
   `Fail.Rule("spec_outside_repository", "The spec path must stay inside the project repository.")`

2. **The file is not there.** If `!File.Exists(full)`, throw
   `Fail.Rule("spec_missing", $"No file at '{relative}' in {task.Project.RepoPath}. Commit the spec on the task branch first.")`

3. **The heading names another task.** Read the file's first non-blank line. If it matches
   `^#\s*(T-\d+)\b` (a compiled `[GeneratedRegex]`, as `RoleService` does it) and the captured id is not
   this task's `Wire.TaskId(task.Id)`, throw
   `Fail.Rule("spec_id_mismatch", $"'{relative}' is the spec for {found}, not {Wire.TaskId(task.Id)}. Attaching it here would point implementers at the wrong work — and writing over it would destroy that record.")`

   A first heading that names **no** task id is accepted. Not every project writes `# T-n — title`, and
   this check exists to catch a wrong id, not to impose a house style on a heading.

   Read at most the first **8192 characters** of the file — `StreamReader.ReadBlock` into a `char[8 * 1024]`,
   which is what the code does and the unit this cap is expressed in. An earlier draft said "8 KiB", a
   different unit: UTF-8 decodes up to four bytes per character, so the character cap can consume up to
   32 KB. That is fine, because **the cap bounds work; it is not a security boundary.** It exists so a
   mistaken path to something enormous is cheap to reject, and 32 KB is as cheap as 8 KB.

   One edge is worth pinning, because two requirements in this section can conflict: if there is no non-blank
   line within the cap, the heading names nothing and the spec is **accepted**. A file opening with more than
   8192 characters of whitespace is pathological, and refusing it would mean claiming it is the spec for
   another task without having read one.
   An `IOException` or `UnauthorizedAccessException` while reading is
   `Fail.Rule("spec_unreadable", $"'{relative}' could not be read: {ex.Message}")` — a spec the hub cannot
   read is one an implementer cannot read either.

Nothing else in `SetSpecAsync` changes. The ledger event stays `task.spec_set` with the same payload.

Why the heading and not the filename: a filename can be anything, and `<id>-<slug>.md` is a convention this
repository adopts rather than a rule the hub imposes. The first heading is what an implementer actually
reads to find out what they are building, so it is the thing that must agree with the task.

### Unit B — the orphans keep their content under names that cannot collide

Pure file moves and recoveries under `specs/`. No code.

**B1 — rename the nine orphans** with `git mv`, so history follows:

| From | To |
|---|---|
| `specs/T-3.md` | `specs/T-3-internal-bus.md` |
| `specs/T-5.md` | `specs/T-5-validator-feedback.md` |
| `specs/T-6.md` | `specs/T-6-multi-harness-workers.md` |
| `specs/T-7.md` | `specs/T-7-ingest.md` |
| `specs/T-8.md` | `specs/T-8-down-scoping.md` |
| `specs/T-9.md` | `specs/T-9-outbound-gate.md` |
| `specs/T-10.md` | `specs/T-10-operations-page.md` |
| `specs/T-13.md` | `specs/T-13-kit-docs.md` |
| `specs/T-14.md` | `specs/T-14-operations-flags.md` |

**B2 — recover the three that were overwritten**, each from the commit named in the table above:

```
git show 41e65ce:specs/T-4.md  > specs/T-4-needs-you-page.md
git show 64d8760:specs/T-11.md > specs/T-11-validation-polish.md
git show 72d5807:specs/T-12.md > specs/T-12-t6-t7-followups.md
```

Verify each recovered file's first line is the original heading from the table — `# T-4 — "Needs you"
page: …`, `# T-11 — Polish from validation reports`, `# T-12 — Follow-ups from the T-6 / T-7 validation`.
If any of them is not, stop and report: the commit is wrong and guessing at another one would recover the
wrong document.

Do not modify the content of any recovered or renamed file. Not the heading, not a typo, not a stale link.
They are a record of what was asked at the time, and editing them is the failure this task exists to undo.

**B3 — `specs/README.md`**, new, short, in the voice of the rest of the repository's docs. It says:

- A spec is named `T-<id>-<slug>.md`. The id is the ledger task it was written for; the slug comes from its
  own first heading.
- Ids in `specs/` are not unique, because the ledger was rebuilt and its ids were reused. Two files may
  carry the same id and be about entirely different work — `T-4-needs-you-page.md` and `T-4.md` are the
  example, and naming it is the point.
- To find a spec, grep the headings, not the filenames.
- `muthur task spec` refuses a file whose first heading names a different task. That check is why the
  heading matters more than the filename.
- The six files still named `T-<id>.md` are the current generation; the live ledger's `specPath` points at
  them and they were left alone deliberately.

## Units of work

### Unit A — the guard
- **Files:** `src/Muthur.Server/Services/TaskService.cs`; `tests/Muthur.Server.Tests/`.
- **Does:** the three checks above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, with tests over a `TestRepo` covering: a spec
  whose heading names this task is accepted; one whose heading names another task is refused 422
  `spec_id_mismatch`; a path to no file is refused 422 `spec_missing`; a path containing `..` that escapes
  the repository is refused 422 `spec_outside_repository`; a spec whose first heading names no task id at
  all is accepted; a spec whose first non-blank line is preceded by blank lines is still read correctly; and
  a spec whose first non-blank line lies beyond the 8192-character cap is **accepted** rather than refused —
  write one with more than 8192 characters of leading whitespace followed by a `# T-9` heading, and assert
  the attach succeeds.

### Unit B — the renames, the recoveries, and the README
- **Files:** the twelve files in the tables above, plus new `specs/README.md`.
- **Does:** B1, B2 and B3.
- **Depends on:** nothing. Can run beside Unit A.
- **Acceptance:** `git status` shows nine renames (`git mv`, so git reports them as renames), three new
  recovered files, and one new README. `dotnet build` and `dotnet test` still clean — nothing here should
  touch them, and if either breaks, something was moved that was not a spec. No file under `specs/` has a
  first heading naming an id that another file in `specs/` also claims **for the same subject**; two files
  may share an id when they are genuinely different work from different generations, which is the whole
  point.

## Verification

```
dotnet build
dotnet test
```

Both clean.

Then, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t25
$env:MUTHUR_HOME = "$PWD/artifacts/t25-home"; $env:MUTHUR_URL = "http://127.0.0.1:7433"
./artifacts/t25/muthur.exe up
```

With a project pointing at a real checkout of this branch and a task claimed:

- `muthur task spec T-<n> specs/T-9-outbound-gate.md` is refused with `spec_id_mismatch`, exit 2, and the
  message names T-9 and the task — the failure that started this, now caught.
- `muthur task spec T-<n> specs/nope.md` is refused with `spec_missing`, exit 2.
- `muthur task spec T-<n> ../../etc/passwd` is refused — `relative_path_required` or
  `spec_outside_repository` depending on the shape; both are correct.
- A spec whose heading names the task is accepted, and `muthur task show` reports the path.
- `git log --follow specs/T-9-outbound-gate.md` shows the file's history back through its old name: the
  rename kept the record rather than replacing it.
- The three recovered files open and read as the specs named in the table.

## Out of scope / follow-ups

- **The guard cannot catch everything, and should not pretend to.** Two specs may legitimately share an id
  across generations, so `muthur task spec T-4 specs/T-4-needs-you-page.md` would pass the heading check and
  still attach the wrong document. Only a human knows which T-4 is meant. The check catches the wrong *id*,
  which is the failure that actually happened three times.
- Whether the ledger will be rebuilt again — and therefore whether ids will be reused a third time — is the
  founder's call about their own ledger. If the answer is yes, specs should be named with no id in them at
  all, and that is a different task from this one.
- `muthur task spec` still accepts a spec that is empty, or that is the untouched `_TEMPLATE.md`. Refusing a
  spec that still contains the template's placeholder text would be a cheap further check; it is not this
  task.
