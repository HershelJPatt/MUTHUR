# T-32 — `install.ps1` publishes whatever working tree it is run from

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, every install says what it published — ref, commit, and the worktree it came from — on
every run, correct or not. `-Ref` makes publishing a named commit cheap instead of a discipline, and a dirty
tree is refused, because a binary built from uncommitted work corresponds to no commit and can be named by
neither the ledger nor a validator's evidence.

## The incident, and the measurement

`install.ps1` publishes `$repo`, the working tree it is run from, and copies `kit/` from the same tree. The
founder reinstalled the live hub while an orchestrator had that tree on a task branch. The install
succeeded and silently produced a pre-T-30 CLI, while the live brief — re-served from the same moving tree —
instructed `muthur validate blocked`. Four conductor-started validator sessions on T-13 then refused
correctly, tried to record the refusal, and could not: the binary had no such command. T-30's cap bounded
the loop; the reason never reached the task.

Measured from this checkout's reflog before choosing a fix: **40 branch switches over 20.3 hours, about two
an hour. The tree sat on `main` 11.5% of the time and on a task branch 88.5%.** So an install at a random
moment publishes a task branch with roughly 88% probability. Publishing a task branch is not the defect —
it is usually correct, and `briefs/validator.md` tells validators to do exactly that. Doing it *silently* is
the defect.

## The decision

Founder request #8, answered 2026-09-18. Option 1, with an addition:

> A line reading "published from task/T-15-worker-run-verdict (54c455d)" would have turned a forty-minute
> outage into a five-second double-take. Print it on every run, including the ones that are correct — a
> warning only shown when something looks wrong trains people to skim the normal case, and the normal case
> here is 88% of runs.
>
> `-Ref` then makes the right thing cheap rather than a discipline. Refusing a dirty tree is a separate and
> real hazard: publishing uncommitted work produces a binary that corresponds to no commit, which is
> unreproducible and unattributable […]
>
> One addition […]: where the script is run from a worktree, say so in the same line. Worktrees are how the
> agents work now — there were twenty-three of them on this machine an hour ago — and "published from
> \<ref\> in \<worktree\>" is the sentence that makes an install reproducible by someone else.

Rejected, with reasons, so they are not reopened: **refusing unless the checkout is on the branch being
installed** (breaks the workflow it protects — a validator installing a task branch to a scratch
destination is doing the right thing); **`-Ref` alone** (leaves the default silent, which is the defect);
**no tooling change** (keeps a rule already broken twice by the person who wrote it).

## Non-goals

- Do not refuse because the checkout is on a task branch. That is the common correct case.
- Do not change what is published by default: still the current working tree, unless `-Ref` names otherwise.
- Do not add a `-AllowDirty` escape hatch. A dirty tree is never a thing anyone meant to publish, and the
  remedy — commit first — costs one command. Its absence is deliberate; if an implementer finds a real case
  that needs it, stop and report rather than adding one.
- Do not touch the `-RestartRunning` guard or the running-hub check. They are correct and orthogonal.
- Do not make `muthur doctor` check installs (T-14/T-27 own doctor).

## Design

### Unit A — `scripts/install.ps1` says what it published

All of this happens **before** the first `dotnet publish`, so a refusal costs no build time.

**1. Resolve provenance** of the tree that will be published:

- `git -C $source rev-parse --abbrev-ref HEAD` → the ref. When it returns `HEAD` (detached), use
  `git -C $source describe --all --always HEAD` instead so something nameable is printed.
- `git -C $source rev-parse --short HEAD` → the commit.
- `git -C $source rev-parse --git-common-dir` and `--git-dir`: when they differ, `$source` is a **linked
  worktree**, and its path is named in the line.
- If `$source` is not a git repository at all, print `published from a non-git directory <path>` and carry
  on. That is unusual but not wrong — it must not fail.

**2. Refuse a dirty tree.** `git -C $source status --porcelain --untracked-files=no`. If it returns
anything, throw:

```
The working tree at <source> has uncommitted changes, so the build would correspond to no commit.
Commit them, or pass -Ref <ref> to publish a named commit instead.
<the first 10 lines of git status --short>
```

**Untracked files must not count** — `bin/`, `obj/` and `artifacts/` are untracked by design and every run
would otherwise fail. This is why `--untracked-files=no` is not optional.

**3. `-Ref <ref>`** (new `[string]$Ref` parameter). When given:

- `git -C $repo rev-parse --verify --quiet "<ref>^{commit}"`; if it fails, throw
  `"'<ref>' is not a commit in <repo>."`
- Create a detached worktree at a temporary directory:
  `git -C $repo worktree add --detach <temp> <ref>`, and publish from there — `$source` becomes `<temp>`.
- Remove it in a `finally`, with `git -C $repo worktree remove --force <temp>`, so a failed publish leaves
  no worktree behind. This cleanup must run even when the publish throws.
- With `-Ref`, the dirty check is skipped: a fresh detached worktree cannot be dirty.

**4. The line.** Printed **on every successful run**, immediately before the existing
`Write-Host "Installed to $Destination"`:

```
Published from main (bc17807).
Published from task/T-16-collisions (946780f) in worktree C:\WorkSrc\MUTHUR\.worktrees\x.
Published from a non-git directory C:\somewhere.
```

Use `Write-Host`. One sentence, ending in a period, naming the ref, the short commit, and the worktree path
only when the source is a linked worktree. `-Ref` prints the same line for the pinned commit.

`$repo` stays the script's own parent directory. `$source` is what gets published: `$repo`, or the
temporary worktree under `-Ref`.

### Unit B — `muthur role define --brief-file` says where the brief came from

`src/Muthur.Cli/Commands/RoleCommands.cs:24` reads the file and sends its bytes. The founder re-served
`briefs/validator.md` from a moving tree, silently got pre-T-30 text, and caught it only by reading the
result rather than the exit code.

Add a small helper — new file `src/Muthur.Cli/Infrastructure/FileProvenance.cs`:

```csharp
/// <summary>Where a file the founder passed on the command line actually came from, since a repository's working tree moves under them.</summary>
public static class FileProvenance
{
    /// <summary>The ref and short commit of the repository holding <paramref name="path"/>, or null when it is not in one.</summary>
    public static (string Ref, string Commit)? Describe(string path);

    /// <summary>True when that one file has uncommitted changes.</summary>
    public static bool IsDirty(string path);
}
```

Both shell out to `git` through the existing `IProcessRunner` (`Muthur.Launch.ProcessRunner`), with a
10-second timeout, run in the file's own directory. `Describe` uses `rev-parse --abbrev-ref HEAD` and
`rev-parse --short HEAD`; `IsDirty` uses `status --porcelain --untracked-files=no -- <file>` and is true
when the output is non-empty. Any git failure means "not in a repository": `Describe` returns null and
`IsDirty` returns false. **Never let a git failure stop a role from being defined** — provenance is
evidence, not a gate.

**An untracked brief matches no commit either, and must be refused the same way.** A file inside a
repository that was never `git add`ed returns nothing from `status --untracked-files=no`, so `IsDirty` is
false and it sails through — while `Describe` still prints `Read x.md at main (abc1234)`, naming a commit
that does not contain that file. **A provenance line that names the wrong commit is worse than none**, and
this task exists because a silently wrong answer cost forty minutes. So:

```csharp
/// <summary>True when git cannot name a commit containing this file: modified, staged, or never added.</summary>
public static bool IsDirty(string path);
```

`IsDirty` is true when *either* `status --porcelain --untracked-files=no -- <file>` is non-empty **or**
`ls-files --error-unmatch -- <file>` fails. The refusal message covers both cases without naming which:
`"<file> has no committed version, so the brief you install will match no commit. Commit it, or pass the
text you mean."` Keep `--untracked-files=no` on the status call — it is what stops `bin/` and `obj/` failing
every run, and the `ls-files` probe is what closes the gap it leaves.

Pass `--literal-pathspecs` on both git calls. A brief filename containing `*`, `[` or `?` would otherwise be
read as a glob and silently match the wrong thing — the same class of quiet wrong answer.

In `role define`:

- If `IsDirty(file)` → refuse before sending anything:
  `Output.Error("brief_file_dirty", "<file> has uncommitted changes, so the brief you install will match no commit. Commit it, or pass the text you mean.", ExitCodes.RuleViolation)`.
- Otherwise send as today, and when `Describe` returns a value, write one line to **stderr** (never stdout,
  which is the JSON an agent parses):
  `Read <file> at <ref> (<commit>).`

stderr, not stdout, is the load-bearing detail: `Output.Emit` writes the API's JSON to stdout and agents
parse it.

## Units of work

The two units share no file and neither depends on the other.

### Unit A — the install says what it published
- **Files:** modified `scripts/install.ps1`
- **Does:** the Unit A section above, exactly.
- **Depends on:** nothing.
- **Acceptance:** there is no PowerShell test project, so this is verified by running it. All against a
  scratch `-Destination` under `./artifacts/`, never the default:
  - On a clean checkout: the line names the current ref and short commit, and the install succeeds.
  - With a tracked file edited: the script throws the dirty message and **no `dotnet publish` runs** (the
    refusal must be visible before any build output).
  - With an untracked file present (e.g. a new file under `artifacts/`): the install succeeds. This is the
    check that proves `--untracked-files=no` is doing its job.
  - `-Ref main` from a checkout on another branch: the line names `main` and main's commit, the install
    succeeds, and `git worktree list` afterwards shows no leftover worktree.
  - `-Ref does-not-exist`: throws, and leaves no worktree.
  - Run from a linked worktree under `.claude/worktrees/` or `.worktrees/`: the line includes
    `in worktree <path>`.
  - Paste the actual output of each into the report. A line that is merely believed to print is not
    evidence.

### Unit B — the brief says where it came from
- **Files:** created `src/Muthur.Cli/Infrastructure/FileProvenance.cs`; modified
  `src/Muthur.Cli/Commands/RoleCommands.cs`; tests in `tests/Muthur.Cli.Tests/FileProvenanceTests.cs` (new)
- **Does:** the Unit B section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean, `dotnet test` green.
  - Tests build a real temporary git repository (as `KitWriteModeTests` builds real temp directories):
    a committed file reports its ref and short commit and is not dirty; the same file edited is dirty; a
    file outside any repository returns null from `Describe` and false from `IsDirty`.
  - AOT-safe: no reflection-based JSON, no new serializer.

## Verification

```
dotnet build
dotnet test
```

Then, end to end, from the repository root with a scratch destination:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t32        # prints the provenance line
pwsh ./scripts/install.ps1 -Destination ./artifacts/t32 -Ref main
git worktree list                                               # no leftovers
```

And for Unit B, against a scratch hub — never the live one:

```
muthur role define probe --brief-file kit/briefs/validator.md --founder   # stderr names the ref and commit
# edit that file, then run it again                                        # exit 2, brief_file_dirty
```

The check a validator should not skip: **edit a tracked file and confirm the install refuses before any
build output appears.** A refusal that arrives after a two-minute publish has already cost what it was
meant to save.

## Out of scope / follow-ups

- Recording the installed ref *in the hub*, so `muthur status` can say which commit the running server was
  built from, is a larger and better idea than printing it locally, and it belongs with T-14's `doctor`
  work rather than here.
- Every other founder command that reads a path from the moving tree — there are few today — inherits the
  same hazard. `FileProvenance` is deliberately general so the next one costs two lines.
