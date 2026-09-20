# T-57 — Containment survives a junction: a manifest cannot write outside the repository

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`muthur kit install` decides containment by **where a path actually leads**, not by how it is spelled. A `to`
that reaches outside the repository through a directory junction, a symbolic link, or a chain of them is
refused with `{"code":"invalid_manifest",…}` and exit 2 before anything is written; the same holds for a `from`
that reaches outside the kit directory. A link that points back *inside* the repository keeps installing, and a
`--repo` that is itself named through a link keeps working.

After this task, T-8's sentence — *"a manifest can no longer choose where its files land"* — is true. Today it
is not: rule 14 is satisfied by a path that does not stay where it resolves.

## Context — measured, not assumed

Everything below was measured on this machine (Windows 11, .NET 10.0.204) before this spec was written. The
implementer does not need to re-derive it, but every claim here is checkable.

### The escape, against the product

`dotnet publish src/Muthur.Cli`, a scratch kit with one entry, a repository containing `link` (a junction to a
sibling `outside` directory) and `inward` (a junction to `repo\docs`):

```
through a junction       to=link/escaped.md       exit=0  files outside the repo: 1
deeper through it        to=link/sub/escaped.md   exit=0  files outside the repo: 1
junction, back inside    to=inward/ok.md          exit=0  files outside the repo: 0
ordinary destination     to=docs/ok.md            exit=0  files outside the repo: 0
plain traversal          to=../outside/escaped.md exit=2  rule 14 refuses it
```

Row 1 is the defect. Rows 3 and 4 are the behaviour that must not change — a rule that refuses them would be
worse than the hole it closes.

### Why rule 14 misses it

`Path.GetFullPath` normalises a path *as a string*. `repo\link\escaped.md` is textually under `repo`, so
`IsInside` says yes, and the filesystem then sends the write to the junction's target. Measured:

```
GetFullPath(repo\link\escaped.md)  ->  …\repo\link\escaped.md
textually under repo?              ->  True
content read back from outside\escaped.md  ->  "this landed outside"
```

### What the platform APIs actually do

| Call | Result |
|---|---|
| `Directory.ResolveLinkTarget(junction, returnFinalTarget: true)` | the target |
| `Directory.ResolveLinkTarget(plain directory, true)` | `null` |
| `Directory.ResolveLinkTarget(plain file, true)` | `null` |
| `Directory.ResolveLinkTarget(path that does not exist, true)` | **throws** `DirectoryNotFoundException` |
| `File.ResolveLinkTarget(junction, true)` | the target — it works on directories too |
| `File.ResolveLinkTarget(path that does not exist, true)` | **throws** `FileNotFoundException` |
| `Directory.ResolveLinkTarget("repo\link\sub", true)` where only `link` is a junction | **`null`** |
| `Directory.ResolveLinkTarget(a path whose parent is a file, true)` | **throws** `IOException` |
| `Directory.ResolveLinkTarget("C:\", true)` | `null` |
| a chain `a -> b -> outside`, `returnFinalTarget: true` | `outside` — the OS collapses the chain |
| a cycle `cycA\to -> cycB`, `cycB\to -> cycA`, asked four deep | resolves, no exception, no hang |
| a junction whose target has been deleted | `Directory.Exists` is **True**, resolves to the missing target |
| `File.ResolveLinkTarget(a hard link, true)` | **`null`** — a hard link is invisible to path resolution |
| the returned `FullName` | absolute, no trailing separator |

Three of these decide the algorithm:

- **A path under a link is not itself a link.** So resolving only the deepest existing component is wrong; the
  walk has to test every component on the way down.
- **Resolving a path that does not exist throws.** So the walk must ask `Directory.Exists` / `File.Exists`
  first and stop resolving where the path stops existing. That existence check is also what keeps the
  `IOException` row unreachable.
- **`returnFinalTarget: true` collapses chains itself.** No loop and no depth cap of our own; each iteration of
  the walk consumes one component of a finite string, so it terminates by construction.

### The root has to be resolved too

A `--repo` named through a junction resolves to its real path, so comparing a resolved destination against an
*unresolved* root would refuse every legitimate destination. Measured, with `via` a junction to `repo`:

```
Real(via)                  ->  …\repo
via + docs/ok.md           ->  …\repo\docs\ok.md      inside = True
via + link/escaped.md      ->  …\outside\escaped.md   inside = False
```

### `..` after a link is not a second hole

`repo\link\..\climbed.md` is resolved lexically by `Path.GetFullPath` **and** by Win32 before the write, so
both agree on `repo\climbed.md`. Measured: the file lands at `repo\climbed.md`, not beside `outside`. Nothing
to do here.

### The code

`src/Muthur.Cli/Commands/KitCommands.cs`. `ReadManifest` computes `kitRoot` and `repoRoot` and hands them to
`TryReadEntry`, which applies the rules in the order T-8's spec numbers them. Rule 12 (`from` inside the kit)
and rule 14 (`to` inside the repository) both end in the same helper:

```csharp
private static bool IsInside(string path, string root) =>
    path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
```

Read `specs/T-8-manifest-errors.md` first. It is long, and the parts that matter here are the numbered rules
and Amendments 6 and 7, which record two rules this orchestrator got wrong by stating a mechanism it had not
measured. That is the standard this spec is held to and the standard your report is held to.

## Non-goals

- **Hard links.** Measured and confirmed unreachable by path resolution: `File.ResolveLinkTarget` returns
  `null` for one, the destination looks contained, and writing through it changes a file outside the
  repository. Detecting it needs volume-level inspection with no cross-platform API. Filed, not fixed here.
- **The write phase.** Still not transactional (T-55), still not guarded. This task refuses before the write,
  like every other rule in `TryReadManifest`.
- **Time-of-check to time-of-use.** A junction created between validation and the write is not covered, and
  cannot be without holding handles across the whole install. Out of scope, stated so nobody claims otherwise.
- **`RequireSpecBelongsToTaskAsync` in `src/Muthur.Server/Services/TaskService.cs`**, which does the same
  textual containment test on a spec path. Same class, different surface, different risk. Filed.
- Any change to `Install`'s write loop, to `WriteKitFile`, to the write modes, or to the shipped manifests.
  `kit/claude`, `kit/codex` and `kit/generic` must install byte-for-byte as they do today.
- Any new rule about links the founder is *allowed* to have. A junction inside the repository pointing inside
  the repository is legitimate and stays legitimate.

## Design

### The walk

One new private helper in `KitCommands`. This is the exact code that produced every table above; use it.

```csharp
/// <summary>
/// Rule 22. Where <paramref name="absolute"/> actually leads, following every reparse point on the way down,
/// or null when the platform will not say. Path.GetFullPath normalises a path as a string and does not follow
/// links, so a destination textually under the repository can still send its write somewhere else entirely;
/// this is the only answer that a containment test can be built on. Each component is tested rather than the
/// leaf alone, because a path *under* a link is not itself reported as a link. Existence is asked first
/// because resolving a path that does not exist throws, and because that check is what makes the
/// "parent is a file" case unreachable. returnFinalTarget collapses a chain of links itself, so there is no
/// loop here to bound: every iteration consumes one component of a finite string.
/// </summary>
private static string? RealPath(string absolute)
{
    var current = Path.GetPathRoot(absolute);
    if (string.IsNullOrEmpty(current)) return null;

    foreach (var component in absolute[current.Length..]
                 .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    {
        if (component.Length == 0) continue;
        var candidate = Path.Combine(current, component);
        try
        {
            var link = Directory.Exists(candidate) ? Directory.ResolveLinkTarget(candidate, returnFinalTarget: true)
                     : File.Exists(candidate) ? File.ResolveLinkTarget(candidate, returnFinalTarget: true)
                     : null;
            current = link?.FullName ?? candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
    return current;
}
```

Not gated on `OperatingSystem.IsWindows()`. Unlike rules 18, 19b and 19c, this is not a Win32 quirk: the same
call follows symbolic links on Linux and macOS, and on macOS `/var` is a link, so the platform that most needs
the root resolved is not this one.

### Where it is applied

`ReadManifest` resolves both roots **once**, beside the existing `kitRoot` / `repoRoot` lines, and passes them
to `TryReadEntry`. A root that will not resolve fails the whole manifest before any entry is read:

```
"<manifestPath>: the kit directory \"<kitDir>\" could not be resolved; MUTHUR cannot tell what is inside it."
"<manifestPath>: the repository directory \"<repo>\" could not be resolved; MUTHUR cannot tell what is inside it."
```

`TryReadEntry` gains two checks, each placed **immediately after** the textual containment rule it completes,
so a reader looking for containment finds both halves in one place:

- **After rule 12** (`from` outside the kit), before rule 13.
- **After rule 14** (`to` outside the repository), before rule 18.

Each is: resolve the already-computed path with `RealPath`; if it is null, refuse as unresolvable; if it is not
inside the resolved root, refuse as an escape.

### Rule 22's four messages

```
"<manifestPath>: entry <n> (<to>) reads \"<from>\", which resolves through a link to \"<real>\", outside the kit directory."
"<manifestPath>: entry <n> (<to>) reads \"<from>\", whose location on disk could not be determined."
"<manifestPath>: entry <n> writes \"<to>\", which resolves through a link to \"<real>\", outside the repository."
"<manifestPath>: entry <n> writes \"<to>\", whose location on disk could not be determined."
```

`<real>` is the resolved path. Naming it is the point: the manifest line looks innocent and the reason is a
link in the founder's own repository, so a message that only said "outside the repository" would send them
looking for a `..` that is not there.

The unresolvable messages carry no exception text. `RealPath` returns null rather than an exception, and the
two shapes that reach it — a component that cannot be opened, a link the platform will not follow — do not
produce a sentence worth printing. If one ever matters it is a new rule, filed, not a widened message.

### Rule 20 compares real paths

`Destination.Resolved` currently holds the textual `Path.GetFullPath` result. Change it to hold the
**resolved** destination, which rule 22 has already computed for every entry that gets that far.

This is not scope creep; it is the same defect one rule over. With `inward` a junction to `repo\docs`,
`to: "inward/x.md"` and `to: "docs/x.md/y.md"` are a file-versus-directory collision that textual comparison
cannot see, and the write phase would meet it with files already on disk. It also makes `inward/x.md` and
`docs/x.md` the *same* destination rather than two, which is last-one-wins — the behaviour rule 20's own
comment says the format has always allowed.

The seeded installer files stay as they are, resolved against the resolved repository root:
`Path.Combine(<resolved repo root>, own)`. `.gitignore` being itself a link out of the repository is
`Install` writing its own files without looking, which is T-56's F2 and not this task.

### What does not change

`Install`'s write loop still writes to `Path.Combine(repo, entry.To)`. Validation decides; the write is
untouched.

T-8's last-resort guard around `TryReadManifest` has now fired **zero times in roughly 450 hostile manifests**.
Rule 22 must not be the first thing to make it fire: every path this rule can take is handled, and "the guard
did not fire" is an acceptance check below, not a hope.

## Units of work

One unit. The rule and its tests are a single change to a single file plus a single new test file; splitting
them would produce two branches that cannot be verified apart.

### Unit A — containment by where a path leads

- **Files:** `src/Muthur.Cli/Commands/KitCommands.cs` (modified), `tests/Muthur.Cli.Tests/KitContainmentTests.cs` (new).
- **Does:** everything under Design.
- **Depends on:** nothing.
- **Do not touch** `tests/Muthur.Cli.Tests/KitManifestTests.cs`. Its 90-odd cases are T-8's regression surface
  and must stay green untouched — that they do is how we know no working manifest was refused.

#### Creating a link in a test

Measured on this machine: `Directory.CreateSymbolicLink` throws
`IOException: A required privilege is not held by the client`, and `cmd /c mklink /J` succeeds. So the test
helper is:

- **Not Windows:** `Directory.CreateSymbolicLink(link, target)`.
- **Windows:** try `Directory.CreateSymbolicLink`; on `IOException`, fall back to `cmd.exe /c mklink /J`.
- **Neither worked:** throw, with a message saying the test cannot create the link it needs and therefore
  cannot measure what it claims. **Do not skip and do not pass.** xunit 2.9.3 has no dynamic skip, and a
  containment test that quietly passes when it could not build its own attack is worse than one that fails.

`Process.Start` in a test has precedent: `tests/Muthur.Cli.Tests/FileProvenanceTests.cs` and
`tests/Muthur.Server.Tests/TestRepo.cs` both shell out to `git`.

#### Test structure

Follow `KitManifestTests.cs` exactly: `[Collection(KitEnvironment.Name)]` (the class joins the existing
collection — MUTHUR_KIT is process-global), a scratch kit built in the constructor, an `Outside` directory
beside the repository, `Invoke` / `Rejected` built the same way, and `Dispose` deleting everything it made.

**Deleting a directory that contains a junction:** `Directory.Delete(path, recursive: true)` removes the
junction without following it into the target on .NET — but the fixture must prove it, not assume it. `Dispose`
asserts nothing survives outside the scratch root, and a test that leaves a file behind fails the run.

#### Acceptance

`dotnet build` clean (warnings are errors) and `dotnet test` green, with these cases, each asserting exit code,
`invalid_manifest`, the exact message, an empty repository **and an empty `Outside`**:

1. `to: "link/escaped.md"` through a junction out of the repository — refused, rule 22's `to` message naming
   the resolved path.
2. `to: "link/sub/escaped.md"` — the same, one component deeper.
3. `to` through a **chain** of two junctions out of the repository — the same.
4. `to: "inward/ok.md"` where `inward` is a junction to `repo\docs` — **exit 0**, the file exists at
   `repo\docs\ok.md`, nothing outside.
5. `to: "docs/ok.md"` with junctions present but unused — **exit 0**, unchanged behaviour.
6. `from` through a junction out of the kit directory — refused, rule 22's `from` message.
7. `--repo` named through a junction, ordinary destination — **exit 0**, installed.
8. Rule 20 across a link: `to: "inward/x.md"` and `to: "docs/x.md/y.md"` in one manifest — refused as a
   collision.
9. `to: "inward/x.md"` and `to: "docs/x.md"` in one manifest — **exit 0**, last-one-wins, one file.
10. A junction whose target has been deleted, `to: "dangling/x.md"` — refused (it resolves outside), not a
    crash.
11. The three shipped kits still install: `kit install` for `claude`, `codex` and `generic` into temp
    repositories, exit 0, the same file count as before.

## Verification

```
dotnet build
dotnet test
```

Both clean. `Muthur.Cli.Tests` gains cases and loses none.

Then against a real CLI, because the founder's failure is an installed binary and an exit code, not a unit
test:

```powershell
pwsh ./scripts/install.ps1 -Destination ./artifacts/t57
```

and a script that builds a scratch kit and a repository holding a junction out (`link`), a junction back in
(`inward`) and a chain, then installs each of these and reports the exit code plus **how many files exist
outside the repository afterwards**:

```
link/escaped.md      link/sub/escaped.md     chain/escaped.md
inward/ok.md         docs/ok.md              ../outside/escaped.md
from through a junction out of the kit
```

Passing looks like: rows 1–3 and 6–7 exit 2 with `invalid_manifest` and **zero** files outside; rows 4 and 5
exit 0 with the file inside the repository and zero outside. No `Unhandled exception`, no
`muthur!<BaseAddress>`.

Then the shipped kits, with `MUTHUR_KIT` unset: `claude`, `codex` and `generic` each exit 0 and install 12
files.

**Two traps this orchestrator hit in this harness, recorded so the next person does not:**

- `Set-Content -NoNewline <path> <value>` — with `-NoNewline` before positional arguments — **silently writes
  nothing and creates no file**, with no error even under `$ErrorActionPreference = 'Stop'`. Use
  `-Path` / `-Value` by name. The first run of the reproduction above failed every row for this reason, which
  is exactly the "a table where everything fails is the shape a bad harness makes" note in T-8's spec. Print a
  `FIXTURE <path> exists: True` line before the table.
- `Get-ChildItem -Recurse` **follows junctions**. A cleanup or a count written that way walks into the
  junction's target and reports or deletes things outside the repository. Count what is under `outside`
  directly.

Every command here runs with no browser, no GUI and no human.

Never against the live hub: `kit install` does not talk to it, but set `MUTHUR_HOME` and `MUTHUR_URL` to
scratch values anyway, as `CLAUDE.md` requires.

## Out of scope / follow-ups

- **Hard links defeat containment and path resolution cannot see it.** Measured: `mklink /H repo\hard.md`
  pointing at a file outside, `RealPath` says inside, `File.WriteAllText` through it changes the outside file.
  Needs `FindFirstFileNameW` or equivalent per platform. Its own task.
- **`TaskService.RequireSpecBelongsToTaskAsync` has the same textual containment test**, so a spec path can
  name a file outside the project checkout through a junction. Read-only and on a repository the founder named,
  but the same defect. Its own task.
- **Rule 20 across a link is fixed; rule 19c and the `Directory.Exists(destination)` check are not asked about
  the resolved path.** They ask about the textual one. No shape is known that exploits this — `to` must already
  be contained by the time they run — but it is stated rather than left to be discovered.
- **Time-of-check to time-of-use**, above.

## Amendment 1 — two locals, not one, and a count that is not a literal (2026-09-19)

The first specialist assigned to this unit was blocked by its harness before it could build anything, and spent
the session reading instead. Two of its reading notes are corrections to this spec, taken before any code was
written.

### 1. `destination` must not change meaning mid-method

The Design section says `Destination.Resolved` becomes the resolved path. The Out-of-scope section says rule
19c (`UncreatableParent`) and the existing-directory check keep asking the **textual** path. Both are intended,
and together they are a contradiction an implementer would resolve by reusing one variable — because in
`TryReadEntry` today the single `out string destination` is what feeds all three.

Stated properly, and this is binding:

- Keep the `Path.GetFullPath` result in its own local. Rules 18, 19, 19b, 19c and the
  `Directory.Exists(destination)` check all continue to use **that**, unchanged. None of their measured
  behaviour moves.
- Rule 22 computes `RealPath` of it into a **second** local.
- The `out` parameter that `ReadManifest` turns into `Destination.Resolved` carries the **resolved** one, which
  is the only thing rule 20 compares.

Two locals. If that makes the signature read badly, rename the `out` parameter to say which one it is; do not
collapse them.

### 2. The shipped-kit count is derived, not written down

Acceptance case 11 said "the same file count as before", and Verification says 12. `KitManifestTests`'
`A_shipped_kit_still_installs_every_file_it_names` already asserts this by reading each `to` out of the
manifest, which is why adding a file to a kit does not break it. Assert the count the same way — derived from
the manifest — rather than as a literal `12`. A literal would be a second, more brittle statement of something
already covered, in a new file, while the file that states it properly is off-limits to edit.

The `12` in Verification is what the orchestrator measured today, for a human reading the proof. It is not a
constant for a test.

### 3. Two things the note-taker flagged that are correct as written

- **The dangling-junction row was measured, not assumed.** `Directory.Exists` on a junction whose target has
  been deleted is `True`, and `ResolveLinkTarget` returns the missing target; acceptance case 10 therefore
  refuses. The note was right that this is the one row where a wrong platform claim would produce a *hole*
  rather than an over-refusal, so measure it again on your machine before you trust it — but it is measured.
- **Rule 22 on `from` runs before rule 13**, so a `from` that does not exist *and* sits under an escaping link
  reports the escape rather than "which does not exist". Intended: the escape is the more serious fact and the
  one the founder needs to act on. Recorded because it is a message change on an input T-8 already covers.

## Amendment 2 — the base moved, and T-56's `Unwritable` reads the destinations this rule changes (2026-09-19)

The second specialist assigned to this unit was also blocked by its harness before it could build anything. It
also read first, and it found something neither this spec nor Amendment 1 could have known: **the spec was
frozen against a `KitCommands.cs` that no longer exists.**

### 1. The base is now `283c6a9`, not `9fc6390`

T-56 and T-65 landed in `KitCommands.cs` after this spec froze. `git diff 9fc6390 283c6a9 --
src/Muthur.Cli/Commands/KitCommands.cs` is 117 lines. Everything the Design section quotes is still
byte-identical at the new base — the `kitRoot`/`repoRoot` lines, the seeded-installer loop, all of
`TryReadEntry`, the `Destination` record, the area around `IsInside` — so the Design section stands as written.
What is new is a caller the Design section never saw.

Work happens on **`task/T-57-containment`**, branched from `283c6a9`, which carries this spec. The old
`task/T-57-junction-containment` and `task/T-57-a-containment` are left where they are as the record of the two
blocked sessions; nothing lands from them.

### 2. `Unwritable` is handed resolved destinations, so it must be handed the resolved root

T-56 added `Unwritable`, which `ReadManifest` calls after rule 20 and which consumes `Destination.Resolved`
four times: `Directory.Exists(d.Resolved)` (F2), `AncestorFile(repoRoot, d.Resolved)` (F1),
`File.Exists(d.Resolved)` / `File.GetAttributes` (F3), and `File.ReadAllText(d.Resolved)` in `Writes`.

Rule 20's change makes `Destination.Resolved` the **resolved** path, so all four now probe where the path
actually leads. For F2, F3 and `Writes` that is strictly better and needs no change: they ask the filesystem
about a path, and the real path is the one the write will land on.

**F1 is not fine, and this is binding.** `AncestorFile` bounds its walk with `while (… IsInside(parent,
repoRoot))` and builds its message with `Path.GetRelativePath(repoRoot, directory)`. Given resolved
destinations and a **textual** `repoRoot`, the moment `--repo` is named through a link the two share no prefix,
the loop body never runs, and F1 returns `null` for every entry — a rule that goes silent rather than wrong,
which is the worst way for one to fail. That is acceptance case 7's exact shape.

So: **pass the resolved repository root to `Unwritable`.** One argument at one call site. The walk then shares a
prefix with the destinations it is given, and `GetRelativePath` produces the relative path a founder would
recognise instead of one full of `..`.

`Unwritable`'s other two arguments, `harnessDir` and `kitRoot`, **stay textual and unchanged**. They are used
only by `Writes` → `Expand`, which reads `Path.Combine(kitDir, "core", …)` to render content. Resolving them
would read the same bytes through a link for no benefit, and the shipped kits must install byte-for-byte as
they do today.

### 3. The out-of-scope note now names two checks, not one

The Out-of-scope section says "rule 19c and the `Directory.Exists(destination)` check are not asked about the
resolved path". At this base there are two such checks and they part company:

- `UncreatableParent(destination)` and `Directory.Exists(destination)` **inside `TryReadEntry`** keep asking the
  textual path, exactly as Amendment 1 §1 requires.
- `Directory.Exists(d.Resolved)` **inside `Unwritable`** (F2) asks the resolved path, because that is what
  `Destination.Resolved` now is.

Both are intended. Stated here so that an implementer meeting two identical-looking calls does not make them
agree.

### 4. A draft exists; it is not evidence

The second specialist wrote `KitCommands.cs` and `KitContainmentTests.cs` before it discovered it could not
compile them. They are preserved at `.work/T-57-draft/` and they have **never been built or run**. Read them if
they help; treat every line as unverified. The acceptance list and the Verification section are unchanged and
are what this unit is judged by — a draft that happens to compile is not a substitute for the table.

## Amendment 3 — existing path rules precede destination resolution; unlink fixtures explicitly (2026-09-19)

The resumed Unit A built the specified implementation successfully, then ran the untouched regression suite.
That measurement disproved two instructions above. This amendment supersedes those instructions, without
changing the containment policy or the `RealPath` algorithm.

### 1. Destination resolution follows the existing spelling checks

Putting the new destination check immediately after rule 14 lets filesystem resolution consume invalid
Windows names before the rules that already explain them. The observed regressions include `   /x.md`,
`....../x.md`, `.. ./x.md`, `.../x.md`, ` /x.md`, a destination of only spaces, and `docs/x:.md`.
They receive an unresolvable-location or apparent escape message instead of their established spelling error.
The requirement that the untouched `KitManifestTests.cs` remain green takes precedence over that placement.

Binding placement: keep rule 14 and all existing destination spelling checks in their existing order.
Run the new destination `RealPath` / null / containment checks **immediately after `UncreatableParent`
and before `Directory.Exists(destination)`**. Set `resolvedDestination` there. This is still before any
write, before token expansion, and before collision and unwritable checks. A syntactically valid escaping
destination still receives the exact rule 22 escape message, including when its target is a directory.

The source check stays immediately after rule 12 and before rule 13. The two separate destination locals,
resolved collision paths, resolved root passed to `Unwritable`, and all four rule 22 messages are unchanged.
Do not edit or weaken `KitManifestTests.cs` to accommodate the bad original placement.

### 2. Fixture cleanup removes links before trees

The delegated test run encountered `UnauthorizedAccessException` at a junction chain during recursive
repository deletion. The original assertion that recursive deletion alone suffices is therefore not a valid
cleanup contract for this environment. The orchestrator's installed-CLI reproduction has successfully removed
the same outward/inward/chain fixture by explicitly deleting each junction before recursive scratch cleanup.

Track every successfully created link and remove each link itself with non-recursive `Directory.Delete`
before deleting repository or kit trees. Reverse creation order is deterministic and removes aliases before
their targets. Verify the outside directory and the kit's outside source still exist, with unchanged source
content, after unlinking and before deleting the scratch tree. Preserve the assertions that no file escaped
and that every scratch directory, including shipped-kit repositories, was removed. Assertion failures must
be reported after cleanup so they do not strand their own attack fixture. If fixture construction fails,
clean the links and directories already created before rethrowing the construction failure.

### 3. Verification remains the same

Build and the complete test suite must pass, with every old CLI test retained. Add the Amendment 2 regression
that names `--repo` through a link with a file blocking a destination's parent; assert the existing F1 message
and unchanged blocker bytes. Then run the installed CLI acceptance table and all shipped kits. A faithful
implementation of superseded instructions that fails these checks is evidence for this amendment, not a pass.

### Reproducible installed-binary check

The Windows reproduction is checked in as `specs/T-57-verify.ps1`. After publishing the task branch with
`scripts/install.ps1 -Destination ./artifacts/t57`, run:

```powershell
pwsh ./specs/T-57-verify.ps1 -Cli ./artifacts/t57/muthur.exe
```

It checks the outward, deeper and chained junctions; inward and ordinary destinations; traversal; an escaping
source; a linked repository root; collisions and aliases through links; a dangling outward junction; and all
three shipped kits. Each attack reports its exit code and newly created outside-file count. The script
asserts messages, verifies installed bytes, removes its links before recursive cleanup, and restores its
environment variables. The unit tests additionally cover the linked-root F1 blocker from Amendment 2.

The acceptance evidence is Windows evidence. Linux and macOS execution must not be claimed without a run.
The goal's containment claim is bounded by the explicit non-goals: hard links remain T-83, and concurrent
filesystem changes between checking and writing remain outside this task. Spec-path containment is T-84.
