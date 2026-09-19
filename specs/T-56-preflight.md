# T-56 — `kit install` asks the repository, not only the manifest

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task `kit install` refuses a repository it cannot write into, with a sentence naming the path,
instead of crashing with `Unhandled exception` and an AOT address dump. Three shapes crash today, all of them
decided by the repository rather than by the manifest, which is why T-8 — which closed every
manifest-decidable shape — did not reach them.

## Context

- `src/Muthur.Cli/Commands/KitCommands.cs`:
  - `TryReadManifest` (around line 220) builds `destinations`, one `Destination` per entry **plus one per
    installer file**, and then asks `Collision` whether two of them cannot both exist. That list is the pass
    this task widens: it already holds every path the install will write, resolved.
  - `TryReadEntry` line 449 already asks one repository question — `Directory.Exists(destination)` — with the
    comment "Decided by the repository rather than by the manifest". T-56 is the same question in three more
    directions.
  - `InstallerFiles` = `[".gitignore", ProjectContext.FileName]`, written by `Install` whatever the manifest
    says.
  - `WriteFile` (line 645) creates the parent directory and then calls `File.WriteAllText`.
- The AOT build has `StackTraceSupport=false`, so an unhandled exception reaches the founder as an address
  dump. Every other failure in this CLI is a JSON error with a stable code and exit 2.

Constraints that are not obvious:

- The CLI is Native AOT with no reflection-based JSON.
- `Output.Error(code, message, ExitCodes.RuleViolation)` is how every other rule violation is reported.
- Warnings are errors.

## Non-goals

- **A `try/catch` around the write loop.** T-8 recorded why and T-55 repeats it: it turns the crash into a
  sentence while leaving the half-installed repository, which is the worse half of the damage.
- **T-55's transactional write phase.** It is the right answer to *a write that fails for a reason nobody
  predicted*; it is not the answer to these three, which are predictable and should never reach a write.
  T-56 explicitly notes this: staging and moving would stop the half-install and the install would still fail
  every time, still without saying why.
- Any new mechanism. This is the existing pre-flight asked three more questions.

## Design

All three checks join the pass that already exists, after `destinations` is built and beside `Collision`.
Each returns `invalid_manifest` and exit 2, the code every other refusal here uses, because the founder's
remedy is the same: the install did not happen and nothing was written.

### F1 — an ancestor of a destination is an existing file

`WriteFile` calls `Directory.CreateDirectory` on the parent, which throws `IOException` when any ancestor is
a file. For every destination, walk from the repository root down to the parent and refuse the first
component that exists as a file:

```
kit.json: entry 2 writes "docs/x.md", but "docs" is a file in the repository, so its parent directory
cannot be created.
```

This is the exact mirror of the rule at line 449 — "the destination is an existing directory" — with the
answer the other way round, and it belongs beside it.

### F2 — an installer file exists as a directory

`.gitignore` and `muthur.project.json` are already in `destinations` with `Entry = null`. The same
`Directory.Exists` question the manifest entries get, asked of them:

```
kit install writes ".gitignore", but it is a directory in the repository.
```

Worth stating plainly because it is the worst of the three: **no manifest is involved**, so with either path
present as a directory *every* install of *every* shipped kit crashes, after writing real files.

### F3 — a destination exists and is read-only

`File.WriteAllText` throws `UnauthorizedAccessException` on a read-only file. Checked with
`File.GetAttributes(...).HasFlag(FileAttributes.ReadOnly)` for destinations that exist:

```
kit.json: entry 1 writes "x.md", which is read-only in the repository.
```

A read-only file is a real condition rather than a manifest error, and T-8's out-of-scope list covers "a
locked file, a full disk". It is included because it *crashes* rather than reporting, and because the pass
that answers F1 and F2 answers it for free.

**Only where this install would actually put bytes**, which is a different question from whether the path is
named. `Writes(destination, entries)` decides it:

- a manifest entry in `create` mode whose destination exists is kept — `WriteKitFile` returns "kept" without
  writing — so it is not refused;
- `muthur.project.json` is written only when absent, so an existing one is never refused;
- `.gitignore` is written only when it does not already ignore `.worktrees/`, so one that does is not refused.

Every shipped kit ships `briefs/validator.md` in `create` mode on purpose, so refusing a read-only one would
refuse every install into a repository where a founder had protected their own brief.

### Order

F2 before F1 before F3, checked over the whole list before any of them reports, so a repository with several
problems names the first in manifest order rather than an arbitrary one. `Collision` keeps its place and runs
first: a manifest that contradicts itself is wrong whatever the repository looks like.

## Units of work

### Unit A — the three checks
- **Files:** `src/Muthur.Cli/Commands/KitCommands.cs`
- **Does:** F1, F2 and F3 in the existing pre-flight, per Design.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean; Unit B's tests pass.

### Unit B — the tests
- **Files:** `tests/Muthur.Cli.Tests/`
- **Does:** each of the five reproductions in T-56's body — `docs` as a file with `to: "docs/x.md"`, `a` as a
  file with `to: "a/b/c.md"`, `.gitignore` as a directory, `muthur.project.json` as a directory, and a
  read-only destination — refused with `invalid_manifest`, exit 2, a message naming the path, **and nothing
  written**. Plus an ordinary install into a clean repository, unchanged.
- **Depends on:** Unit A.
- **Acceptance:** each fails with Unit A reverted.

## Verification

Every command here runs with no browser, no GUI and no human. Nothing in this task can only be seen by eye,
so it is not marked `attended` and there is no `needs:` line. Nothing here merges or checks out any branch.

```
dotnet build
dotnet test
```

Passing is 0 warnings, 0 errors and every test green.

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 28 Launch, 194 Cli, 450 Server — all green.

Load-bearing checked by a targeted revert — the `Unwritable` call removed, the method left in place. Five of
the six fail, and the sixth is the one that must not: `A_clean_repository_still_installs` keeps passing, which
is what says this is a refusal of the unwritable and not of everything.

```
An_installer_file_that_is_a_directory_is_refused_before_anything_is_written(".gitignore")            FAIL
An_installer_file_that_is_a_directory_is_refused_before_anything_is_written("muthur.project.json")   FAIL
An_ancestor_of_a_destination_that_is_a_file_is_refused(".claude")                                    FAIL
An_ancestor_of_a_destination_that_is_a_file_is_refused(".claude/skills")                             FAIL
A_read_only_destination_is_refused_rather_than_crashing                                              FAIL
A_clean_repository_still_installs                                                                    pass
```

Each failure is `Expected: 2, Actual: 1` — exit 1 is the unhandled exception T-56 reported, which in an AOT
build with `StackTraceSupport=false` is the address dump a founder sees. Exit 2 is a sentence.

### One bug of my own, found by the test

`AncestorFile` first called `IsInside(repoRoot, parent)`. The helper reads `IsInside(path, root)` — the
arguments were the wrong way round, so the walk terminated immediately, `walked` stayed empty and F1 never
fired. The two F1 rows failed with exit 1 while F2 and F3 passed, which is what pointed at it: a check that
never runs and a check that finds nothing look identical until one family fails alone.

## Amendment after the first validation round (2026-09-19, top-right)

`conductor-validator-t-56` confirmed the three crash fixtures now "refuse cleanly with zero writes" and failed
the task on a promise this spec made and my code then broke:

> all shipped kits reject read-only briefs/validator.md even though its mode=create must keep it and succeed
> … Clearing only ReadOnly makes the install succeed with status kept and original bytes intact.

The Design section said in as many words that a `create` entry whose destination exists is "untouched by
this … so a read-only file it is not going to write is not refused". `Unwritable` then asked
`File.GetAttributes` of every destination regardless of mode. The sentence was right and the code did not
implement it — the worst of the two ways to be wrong, because the spec reads as though it had been
considered.

The consequence is not small: `briefs/validator.md` is `create` in all three shipped kits, so a founder who
made their own brief read-only could not install any kit at all.

**F3 now asks `Writes()` first** — see the amended Design — so it refuses a read-only path this install would
write and leaves alone one it would keep. Three tests were added and one of them is deliberately the other
half, because narrowing a check is the easy way to make a validator's complaint go away while switching the
check off:

- `A_read_only_file_this_install_would_keep_rather_than_write_is_not_refused`, over all three harnesses, with
  the brief's bytes asserted unchanged afterwards — the validator's repro.
- `A_read_only_file_this_install_would_write_is_still_refused` — `.gitignore` lacking the worktrees line.
- `A_read_only_gitignore_that_already_ignores_the_worktrees_is_not_refused` — the same file, already saying
  what the install would add.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 28 Launch, 199 Cli, 450 Server — all green.

## Amendment after the second validation round (2026-09-19, top-right)

`conductor-validator-t-56` confirmed the create-mode and installer-file exceptions now work, and found the
same defect one layer in:

> The ordinary unchanged-file case does not. A founder who protects an already-installed, current kit file
> cannot re-run installation. This affects all three shipped kits, including overwrite mode in claude/generic
> and section mode in codex.

Right, and the reason is worth naming rather than patching around: `Writes()` was answering from the
**mode**, and the write answers from the **content**. `WriteFile` returns `"unchanged"` without writing when
the bytes already match, and `WriteSection` computes a whole document that is usually identical on a re-run.
Two functions answering "would this write" from different information is a defect that regenerates itself
every time either one changes — which is exactly what the first amendment did.

### One place decides

`Rendered(path, content, mode)` now returns exactly what the install would leave in a file, or null when it
would not write at all. Both callers go through it:

- `WriteKitFile` is `Rendered(...) is { } bytes ? WriteFile(path, bytes) : "kept"`.
- `Writes()` asks `Rendered(...)`, and then whether those bytes are already there.

`WriteSection` became `SectionDocument`, which builds the document and returns it rather than writing it —
the same code, with the decision and the action separated. The pre-flight now needs the kit directories to
read and expand a source, which `TryReadManifest` already had.

This is smaller than the first amendment and it is the fix that stops the class, not the instance: mode,
content, line endings and the section merge are now decided once.

### Tests

- `A_read_only_file_a_reinstall_would_leave_unchanged_is_not_refused` — install, protect a file, install
  again: exit 0 and the bytes untouched. Over overwrite mode (claude, generic) and section mode (codex),
  which is the spread the validator named.
- `A_read_only_file_a_reinstall_would_change_is_still_refused` — the same file, edited by hand first, so the
  install really would write it. Refused.

Fifteen tests in this class now, and each narrowing of F3 has arrived with the half that keeps it honest.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 28 Launch, 203 Cli, 450 Server — all green.
