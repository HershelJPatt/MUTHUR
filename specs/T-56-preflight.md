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
that answers F1 and F2 answers it for free. A directory in `create` mode whose destination already exists is
untouched by this: `WriteKitFile` returns "kept" without writing, so a read-only file it is not going to
write is not refused.

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
