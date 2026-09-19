# T-55 — kit install is not transactional: a write that fails leaves a half-installed repository

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`muthur kit install` either installs completely or leaves the repository exactly as it found it. A write that
fails for **any** reason — a locked file, a read-only destination, a full disk, a permission change mid-run, a
shape nobody has thought of — is undone before the command returns, and the founder gets
`{"code":"install_failed","message":"…"}` on stderr at exit 1 naming the file that failed and saying the
repository was put back. No unhandled exception, no address stack, and no half-installed repository.

This is the remainder T-8 could not close. T-8 made the *manifest* decidable before a byte is written and
deliberately left the write phase unguarded, because a `try/catch` there would have turned the crash into a
sentence while leaving exactly the damage the Goal cares about — *"satisfying half the requirement and hiding
the worse half"* (`specs/T-8-manifest-errors.md:584-589`). Rollback is what makes the catch honest: it answers
the half T-8 refused to hide.

## Context — measured, not assumed

Reproduced against an AOT CLI installed from this branch's base commit (`a18831f`) with `MUTHUR_KIT` pointed at
a scratch kit of three entries:

```json
{"harness":"scratch","files":[
 {"from":"a.md","to":"docs/a.md"},
 {"from":"b.md","to":"b.md"},
 {"from":"c.md","to":"c.md"}]}
```

into a repository that already holds `b.md` (CRLF content, so a restore that round-trips through
`ReadAllText`/`ReplaceLineEndings` is visibly lossy) and `c.md`, with `c.md` made unwritable two ways:

```
fail-mode: readonly              fail-mode: lock
exit            : 1              exit            : 1
crashed         : True           crashed         : True
UnauthorizedAccessException      IOException: … used by another process

repository afterwards (both):    docs\      docs\a.md      b.md      c.md
b.md restored byte-for-byte : False
as it found it              : False
```

Entry 0 created `docs\` and `docs\a.md`; entry 1 overwrote `b.md`; entry 2 crashed. Neither `.gitignore` nor
`muthur.project.json` was written, because the crash happens inside the entry loop, before them.

**Both failure modes are reachable through the real command on a repository the founder named**, and neither is
decidable from the manifest, so no rule T-8 added or T-56 will add can close them. They are the shape this task
exists for.

### The platform behaviours this design rests on, measured on Windows 11 (10.0.26200)

```
WriteAllText over ReadOnly file                UnauthorizedAccessException
ReadAllBytes of ReadOnly file                  OK          <- prior bytes are still journalable
Delete of ReadOnly file                        UnauthorizedAccessException
WriteAllText over FileShare.None handle        IOException
ReadAllBytes over FileShare.None handle        IOException
Directory.Delete(non-empty, recursive:false)   IOException <- refuses rather than destroying
Directory.Delete(empty)                        OK
CreateDirectory where a file exists            IOException
CRLF bytes restored identically                True
File.ReadAllText normalises line endings?      No ("a\r\nb" comes back "a\r\nb")
```

The last two are why the journal holds **raw bytes** and not text: `WriteFile` compares and writes through
`ReplaceLineEndings("\n")`, so restoring a `string` would silently convert a CRLF file the repository owned.

### The code

`src/Muthur.Cli/Commands/KitCommands.cs`. The write phase is `Install` lines 72–140 plus the three write
primitives at lines 634–670. Every mutation `kit install` performs is one of five statements:

| Where | Statement |
|---|---|
| `WriteFile:650-651` | `Directory.CreateDirectory(…)` then `File.WriteAllText(path, content)` |
| `Install:98` | `File.WriteAllText(ignore, …)` — the `.worktrees/` append to `.gitignore` |
| `Install:109` | `File.WriteAllText(projectFile, …)` — the `muthur.project.json` seed |

`WriteKitFile` (line 638) dispatches the modes; `WriteSection` (line 656) composes and delegates to `WriteFile`.
Statuses are `created`, `updated`, `unchanged`, `kept`. `unchanged` and `kept` write nothing and create no
directory — read the early returns at lines 641 and 649 before changing anything.

Two facts about the write loop that constrain the design:

- **The result JSON is written as the side effects happen** (`Utf8JsonWriter` over a `MemoryStream`), so per-entry
  status is interleaved with the writes. Nothing here changes that.
- **Two entries may name the same path.** That is last-one-wins and deliberately not a collision
  (`KitManifestTests.cs:353-366`), so the journal must restore the state before the *first* of them.

### The idiom to follow

`Output.Error(code, message, exitCode)` writes `{"code":…,"message":…}` to stderr through the source-generated
`Relaxed.ErrorResponse`. `ExitCodes.Error` is 1 and is `Output.Error`'s default.

## Non-goals

- **Any change to `TryReadManifest` or to rules 1–21.** Not one rule is added, removed, renumbered or reworded.
  T-56 is in flight in the same file and owns the validation pass; this task owns everything below it.
- **Fixing what T-56 owns.** A repository that already holds a *directory* named `.gitignore` or
  `muthur.project.json` still fails every install, including the three shipped kits. This task makes that
  failure clean and leave nothing behind; it does not make it succeed, and it does not pre-flight it.
  `specs/T-8-manifest-errors.md:756-757` says so already.
- **T-57's junction escape.** Containment is not this task's subject; a destination that resolves inside the
  repository and lands outside it via a reparse point is rolled back at the path we wrote, wherever that led.
- **Surviving a killed process.** The journal is in memory. `kill -9`, a power cut or a crash of the runtime
  itself leaves whatever was on disk; an on-disk journal is a larger change with its own cleanup problem and is
  not part of this. Say it in the type's doc comment so nobody reads a guarantee that is not there.
- **Restoring timestamps, attributes or ACLs.** Content and existence, nothing else.
- **Staging into a temporary directory.** Rejected with reasons under Design; do not build it.
- **Retrying a failed write.** One attempt, then undo.
- **Changing the success path in any observable way** — statuses, result JSON, files written, line endings. That
  is an acceptance criterion, not a hope.

## Design

### Why a journal and not a staging directory

The task filed two options. They are not equivalent, and the second is the one that delivers the Goal:

- **Stage into a temp directory, then move into place.** It stops a *partially written file* and nothing else.
  The moves still call `Directory.CreateDirectory` — which is where most known write-phase crashes are — so the
  failure window is reintroduced during the move, and a move over an existing file destroys the old content with
  no way back, so entries 0…n-1 stay overwritten when entry n fails. It also has to choose a temp location: off
  the repository's volume `File.Move` degrades to copy-and-delete and is not atomic anyway, and inside the
  repository a crash leaves a temp directory behind, which is a worse form of "left something behind".
- **Journal the prior state and roll back.** Restores overwrites as well as creations, needs no second location,
  and covers every statement in the write phase uniformly.

So: **journal**. The two questions the task said this option must answer:

- *What "prior state" means for a file `create` mode left alone*: nothing. No write happens, so the path never
  enters the journal and rollback never touches it. Same for `unchanged`.
- *What it means for the `.gitignore` append*: nothing special. It is a whole-file `File.WriteAllText`, so its
  prior state is the bytes that were there, or absence.

### `InstallTransaction`

New file `src/Muthur.Cli/Infrastructure/InstallTransaction.cs`, `internal sealed class InstallTransaction(string root)`.
It is the only thing in `kit install` that touches the filesystem for writing; `WriteKitFile`, `WriteFile` and
`WriteSection` move out of `KitCommands` and become its members. That is the point of the shape: after this task
there is no way to write a kit file without a transaction holding the undo for it.

```csharp
/// <summary>The path it was last asked to write, so a failure can name the file rather than the command.</summary>
internal string? Writing { get; private set; }

/// <summary>Applies one manifest entry. Returns created | updated | unchanged | kept.</summary>
internal string Apply(string path, string content, string? mode);

/// <summary>Writes a file verbatim — no mode, no line-ending normalisation, no status.</summary>
internal void Write(string path, string content);

/// <summary>Forgets the journal. Called once the install has succeeded.</summary>
internal void Commit();

/// <summary>Puts the repository back. Returns the paths, relative to the root, it could not restore.</summary>
internal IReadOnlyList<string> Rollback();
```

**`Apply`** is today's `WriteKitFile` verbatim in behaviour — the `section` / `create` / default switch, the
`ReplaceLineEndings("\n")` normalisation, the `unchanged` early return, the `existed ? "updated" : "created"`.
Move the code; do not rewrite it. Set `Writing = path` on entry.

**`Write`** is the verbatim primitive, and the only place a write happens:

```csharp
internal void Write(string path, string content)
{
    Writing = path;
    Record(path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}
```

`Writing` is set before `Record` so a failure *inside journaling* — reading the prior bytes of a locked file —
still names the file. `Writing` is never cleared; on the happy path nothing reads it.

**`Write` is what `.gitignore` and `muthur.project.json` use.** They must not go through `Apply`: `Apply`
normalises line endings, and routing the `.gitignore` append through it would rewrite a CRLF `.gitignore` the
repository owns to LF. That is a behaviour change this task does not make.

### The journal

Two lists, in the order things happened, and no set or dictionary:

```csharp
/// <summary>A path as it was before we touched it: its bytes, or null when it did not exist.</summary>
private readonly record struct Undo(string Path, byte[]? Content);

private readonly List<Undo> files = [];
private readonly List<string> directories = [];
```

**No de-duplication by path, deliberately.** Two entries may write the same path, and a rollback that walks the
list in reverse restores the intermediate state and then the original, ending correct. Keying by path would need
a comparer that is case-insensitive on Windows and case-sensitive elsewhere, and getting that wrong loses a
file's prior state entirely. Reverse order costs one redundant write and needs no comparer.

`Record(path)`:

1. Walk from `Path.GetDirectoryName(path)` upward, collecting each ancestor for which `Directory.Exists` is
   false, stopping at the first that exists or when `Path.GetDirectoryName` returns null. Reverse the collected
   list so it is shallowest-first — the order `Directory.CreateDirectory` will create them in — and append to
   `directories`. An ancestor an earlier entry already created exists by now, so it is not recorded twice.
2. Append `new Undo(path, File.Exists(path) ? File.ReadAllBytes(path) : null)` to `files`.

Both steps run **before** anything is created or written, so a `CreateDirectory` that makes `a` and then fails on
`a\b` still has both recorded.

`Rollback()`:

1. Walk `files` **backwards**. `Content is null` → `File.Delete(path)` (a no-op when the path is not there);
   otherwise `File.WriteAllBytes(path, Content)`. Catch `Exception` per path and add
   `Path.GetRelativePath(root, path)` to the failure list.
2. Walk `directories` **backwards** — deepest first. If `Directory.Exists`, `Directory.Delete(path)`
   **non-recursively**, so a directory holding something we did not put there refuses rather than being
   destroyed. Catch per path as above.
3. Clear both lists and return the failures.

A restore that fails will usually make its parent's delete fail too; two entries in the list is the honest
answer, not a bug to tidy away.

### What `Install` does with it

```csharp
var transaction = new InstallTransaction(repo);
using var stream = new MemoryStream();
try
{
    using (var json = new Utf8JsonWriter(stream))
    {
        …the existing body, with WriteKitFile(…) → transaction.Apply(…)
          and the two File.WriteAllText calls → transaction.Write(…)…
    }
}
catch (Exception ex)
{
    return Failed(repo, transaction, ex);
}
transaction.Commit();
return Output.Emit(parse, new ApiResult(200, Encoding.UTF8.GetString(stream.ToArray())));
```

The `try` covers the whole write phase including the `File.ReadAllText(from)` and `Expand` calls inside the loop,
which T-8 Amendment 1 recorded as the one place a manifest-proof read could still throw. Those now roll back too.

```csharp
private static int Failed(string repo, InstallTransaction transaction, Exception ex)
```

names the file from `transaction.Writing` as `Path.GetRelativePath(repo, path)`, calls `Rollback()`, and returns
one of exactly two errors, both at `ExitCodes.Error` (1):

**Rollback clean — `install_failed`:**

```
<repo>: the install failed while writing "<relative path>": <ExceptionType>: <ex.Message>. The repository was
put back the way it was found; nothing was left behind, and kit install is safe to re-run.
```

**Rollback incomplete — `install_not_undone`:**

```
<repo>: the install failed while writing "<relative path>": <ExceptionType>: <ex.Message>. Putting the
repository back also failed, so it is part-installed: <a>, <b> could not be restored. Look at those paths
before re-running.
```

When `Writing` is null — nothing had been attempted — the ` while writing "…"` clause is omitted and the rest of
each sentence is unchanged.

**Exit 1, not 2.** A rule violation is exit 2 and a conflict exit 3 (`CLAUDE.md`); a locked file is neither. The
founder broke no rule and the manifest is valid. Exit 1 is also what this case returns today, so no agent's
branching on exit codes changes.

### Why catching `Exception` here is not the widening T-8 refused

T-8 twice refused a broad catch, and both refusals stand. This one is a different thing and the spec should be
read with the difference in front of it:

- T-8's were **inside a rule**, where catching broadly would shorten a specific sentence into a generic one.
  This is around the whole write phase, outside every rule, and there are no rules here to shorten.
- It **names the exception type**, so a defect in MUTHUR stays legible in a bug report.
- Above all, T-8's objection to a write-phase catch was that it *"would turn the crash into a sentence while
  leaving exactly the damage the Goal cares about"*. The rollback is what removes the damage. Without it this
  catch would be the thing T-8 rejected; with it, it is the thing T-8 filed.

`Exception` and not a list of types is the requirement, not a shortcut: the task's own sentence is *"a write that
fails for a reason nobody predicted"*, and a list is an enumeration, which three rounds of T-8 showed does not
close a class.

## Units of work

### Unit A — the transaction, and every write through it

- **Files:** new `src/Muthur.Cli/Infrastructure/InstallTransaction.cs`; modified
  `src/Muthur.Cli/Commands/KitCommands.cs`, `tests/Muthur.Cli.Tests/KitWriteModeTests.cs`; new
  `tests/Muthur.Cli.Tests/InstallTransactionTests.cs` and `tests/Muthur.Cli.Tests/KitInstallRollbackTests.cs`.
- **Does:** everything under Design.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and:

  **`InstallTransactionTests` — the type on its own, no failure needed and no platform gate.** Drive it against
  a temp directory, then call `Rollback()` and assert the directory is byte-identical to before:
  - a file that did not exist is deleted;
  - a file that existed is restored, **asserted on bytes**, with a CRLF fixture and one with no trailing newline;
  - directories the transaction created are removed, deepest first, including a three-level `a/b/c`;
  - a directory that already existed is **not** removed;
  - a directory the transaction created that now holds a file it did not create is **not** removed, and is
    reported in the returned failure list;
  - the same path written twice is restored to the state before the **first** write;
  - `unchanged` and `kept` put nothing in the journal — write a file, `Apply` the same content, roll back, and
    assert the file is still there;
  - `Commit()` then `Rollback()` restores nothing;
  - `Writing` names the path last attempted.

  **`KitInstallRollbackTests` — end to end through the command.** Build the Context section's scratch kit and
  repository; make entry 2's destination unwritable; invoke `kit install`; assert exit 1, code `install_failed`,
  a message naming `c.md`, and that the repository afterwards holds exactly `b.md` and `c.md` with `b.md`'s bytes
  unchanged — no `docs\`, no `docs\a.md`, no `.gitignore`, no `muthur.project.json`. Then release the condition,
  re-run, and assert exit 0 and a complete install: **safe to re-run is half the Goal.**

  Cover **both** failure modes — the read-only attribute and a `FileShare.None` handle held for the whole
  invocation. Each is measured above. Gate each with `if (!OperatingSystem.IsWindows()) return;` only if the
  implementer **measures** that it does not fail on the platform the suite runs on; the convention is the early
  return already used at `KitManifestTests.cs:402`. Do not gate on an assumption.

  This class points `MUTHUR_KIT` at a scratch kit, so it **must** carry `[Collection(KitEnvironment.Name)]` and
  save and restore the previous value in its constructor and `Dispose`, exactly as `KitManifestTests` does
  (`KitManifestTests.cs:14-18`). Getting this wrong is a race that reproduces rarely and reads as a mystery.

  **The success path is unchanged.** `KitInstallTests` and `KitManifestTests` must pass untouched — do not edit
  either file. `KitWriteModeTests` changes only because `WriteKitFile` became `InstallTransaction.Apply`; every
  assertion in it stays as it is.

### Unit B — attack it, and report rather than paper over

- **Files:** none in `src/` unless Unit B finds a defect; then the smallest fix plus its test.
- **Does:** takes Unit A's merged branch, installs the AOT CLI from it
  (`pwsh ./scripts/install.ps1 -Destination ./artifacts/t55`), and tries to break the claim.
- **Depends on:** Unit A.
- **Acceptance:** a table, in the report, of every case tried with its exit code, whether `Unhandled exception`
  or `muthur!<BaseAddress>` appeared in **raw stderr** (not a truncated column — `specs/T-8-manifest-errors.md:838-846`
  records a harness that could not show the thing it was measuring), and a full recursive listing of the
  repository before and after. At minimum:
  - a failure on the **first** entry, the **last** entry, and an entry in the middle;
  - a failure on the `.gitignore` append (make `.gitignore` read-only) and on the `muthur.project.json` seed;
  - a failure with `mode: "create"` and `mode: "section"` entries earlier in the manifest, including a `section`
    write into a file the repository owns — assert its non-MUTHUR content comes back byte-for-byte;
  - a failure in a repository that was **already installed**, so the entries are `unchanged` up to the failure;
  - a rollback that itself fails, reaching `install_not_undone`: assert it names the paths and exits 1;
  - an entry whose destination directory is created three levels deep, asserting all three levels go;
  - a locked **source** file in the kit, so the failure is a read inside the loop rather than a write;
  - the three shipped kits (`claude`, `codex`, `generic`) installing into fresh temp repositories: exit 0, and
    their trees identical to the pre-change binary's, file by file, `muthur.project.json` excepted (its generated
    key is the temp directory's name). Publish the base commit as a second binary and diff, as T-8's Unit A did.

  Anything that still crashes, or still leaves something behind, is either a fix with a test or an honest
  finding reported for the orchestrator to file. **Do not widen a catch, and do not delete or narrow a case to
  make a table look clean.**

## Verification

```
dotnet build
dotnet test
```

Both clean; warnings are errors. Then the founder's own failure against an installed AOT CLI, because the crash
only looks like a crash in an AOT build (`StackTraceSupport=false`):

```powershell
pwsh ./scripts/install.ps1 -Destination ./artifacts/t55

# Save as measure.ps1 in a scratch directory. -Fail readonly | lock. This is the script that produced the
# Context table, run verbatim; run it against ./artifacts/t55/muthur.exe, and against a binary built from
# the base commit to see the before.
param([Parameter(Mandatory)][string]$Cli, [ValidateSet('readonly','lock')][string]$Fail = 'readonly')
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("t55-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
$kit = Join-Path $scratch 'kit'; $repo = Join-Path $scratch 'repo'
New-Item -ItemType Directory -Force "$kit\core", "$kit\scratch", $repo | Out-Null
[IO.File]::WriteAllText("$kit\core\dummy.md", 'dummy')
foreach ($n in 'a','b','c') { [IO.File]::WriteAllText("$kit\scratch\$n.md", "$n from the kit") }
[IO.File]::WriteAllText("$kit\scratch\kit.json", '{"harness":"scratch","files":[
 {"from":"a.md","to":"docs/a.md"},{"from":"b.md","to":"b.md"},{"from":"c.md","to":"c.md"}]}')
[IO.File]::WriteAllBytes("$repo\b.md", [Text.Encoding]::UTF8.GetBytes("ORIGINAL B`r`nsecond line"))
[IO.File]::WriteAllBytes("$repo\c.md", [Text.Encoding]::UTF8.GetBytes("ORIGINAL C"))
$bBefore = [IO.File]::ReadAllBytes("$repo\b.md")
$handle = $null
if ($Fail -eq 'readonly') { Set-ItemProperty "$repo\c.md" -Name IsReadOnly -Value $true }
else { $handle = [IO.File]::Open("$repo\c.md", 'Open', 'ReadWrite', 'None') }
$env:MUTHUR_KIT = $kit
$stderr = Join-Path $scratch 'stderr.txt'
$p = Start-Process $Cli -ArgumentList @('kit','install','--harness','scratch','--repo',$repo) -NoNewWindow -Wait `
     -PassThru -RedirectStandardError $stderr -RedirectStandardOutput (Join-Path $scratch 'stdout.txt')
$err = (Get-Content -Raw $stderr); if ($null -eq $err) { $err = '' }
if ($handle) { $handle.Dispose() }
Set-ItemProperty "$repo\c.md" -Name IsReadOnly -Value $false
$after = @(Get-ChildItem -Recurse -Force $repo)
$bAfter = if (Test-Path "$repo\b.md") { [IO.File]::ReadAllBytes("$repo\b.md") } else { [byte[]]@() }
$restored = [Linq.Enumerable]::SequenceEqual([byte[]]$bBefore, [byte[]]$bAfter)
Write-Output "exit            : $($p.ExitCode)"
Write-Output "crashed         : $(($err -match 'Unhandled exception') -or ($err -match 'muthur!'))"
Write-Output "stderr          : $(($err -split "`n")[0])"
Write-Output "--- repository afterwards ---"
foreach ($e in $after) { Write-Output ("  " + $e.FullName.Substring($repo.Length+1) + $(if ($e.PSIsContainer) {'\'} else {''})) }
Write-Output "b.md restored byte-for-byte : $restored"
Write-Output "as it found it              : $(($after.Count -eq 2) -and $restored)"
Remove-Item -Recurse -Force $scratch
```

Passing looks like, for both fail modes:

```
exit            : 1
crashed         : False
stderr          : {"code":"install_failed","message":"… while writing \"c.md\" … put back the way it was found …"}
--- repository afterwards ---
  b.md
  c.md
b.md restored byte-for-byte : True
as it found it              : True
```

Then, with nothing unwritable, `muthur kit install --harness claude --repo <a fresh temp dir>` must still exit 0
and install all 12 files.

No browser is needed and none should be used.

## Out of scope / follow-ups

- **T-56** — a repository holding a directory where `.gitignore` or `muthur.project.json` goes still fails every
  install, shipped kits included. This task makes that failure clean and reversible; making it succeed is T-56's.
- **T-57** — a destination reached through a junction is rolled back at the path we wrote, which is outside the
  repository. Rollback inherits containment from rule 14 and does not repair it.
- **A killed process leaves the journal unread.** An on-disk journal, replayed by the next `kit install`, is the
  only thing that would close it. File it if anyone ever hits it; nobody has.

## Amendment 1 — "could not be restored" is a claim about the file, not about the call (2026-09-19)

Unit A reported `spec-problem` before building and was right. The Design's `Rollback()` builds its failure list
from *the restore call threw*, while the sentence that list feeds says *could not be restored*. Those are
different claims, and they come apart exactly when the write being undone never landed — which is the commonest
failure of all, because **the restore fails for the same reason the write did**.

Traced by Unit A from this spec's own Context table and then measured by the orchestrator:

```
WriteAllBytes restore over ReadOnly file        UnauthorizedAccessException
  content still the original?                   True
File.Delete on a path that is a directory       UnauthorizedAccessException
  directory still there?                        True
File.Delete on an absent path                   OK
WriteAllBytes restore over locked file          IOException
```

So as frozen, `measure.ps1 -Fail readonly` — **this spec's own primary verification case** — journals `c.md`'s
prior bytes, fails to write over it, then fails to write the same bytes back over the same read-only file, and
tells the founder the repository *"is part-installed: c.md could not be restored"* when `c.md` is byte-perfect
and nothing was left behind. That is a false alarm, and it fails the pass condition this spec states
(`"code":"install_failed"`).

Unit A found a second, independent path to the same false positive, which is what makes it structural rather
than a one-off: a repository holding a *directory* named `.gitignore` (T-56's F2, which the Non-goals keep in
scope for a *clean* failure). `File.Exists` is false there, so the path is journaled as absent, and rollback
calls `File.Delete` on a directory, which throws. The directory was there before and is there after.

### The rule: attempt, then ask the filesystem

Both loops in `Rollback()` take the same shape — try the restore, swallow the exception, then **check the
claim**:

```csharp
foreach (var undo in files in reverse)
{
    try
    {
        if (undo.Content is null) File.Delete(undo.Path);
        else File.WriteAllBytes(undo.Path, undo.Content);
    }
    catch (Exception) { }
    if (!IsAsRecorded(undo)) failed.Add(Path.GetRelativePath(root, undo.Path));
}

foreach (var directory in directories in reverse)
{
    try { if (Directory.Exists(directory)) Directory.Delete(directory); }
    catch (Exception) { }
    if (Directory.Exists(directory)) failed.Add(Path.GetRelativePath(root, directory));
}
```

```csharp
/// <summary>
/// Whether the path is in the state the journal recorded — which is the question the failure list's own
/// sentence asks, and not the question "did the call throw". A write that failed before it landed leaves the
/// path exactly as journaled and the restore then fails for the same reason the write did, so blaming it
/// would be a false alarm on the commonest failure there is.
/// </summary>
private static bool IsAsRecorded(Undo undo)
{
    try
    {
        return undo.Content is null
            ? !File.Exists(undo.Path)
            : File.Exists(undo.Path) && File.ReadAllBytes(undo.Path).SequenceEqual(undo.Content);
    }
    catch (Exception)
    {
        return false;
    }
}
```

Three points where the predicate has to be exactly this and not the obvious thing:

- **`Content is null` asks `!File.Exists`, and says nothing about a directory.** Unit A's own proposal added
  `&& !Directory.Exists(Path)`, which would have left its second case still falsely reported: the directory
  named `.gitignore` was there before we ran and is not ours to account for. If we had created a directory at
  that path it would be in `directories` and loop 2 would own it. Measured above: `File.Exists` on a directory
  is false, so the path is correctly seen as holding no file of ours.
- **A read that throws returns false**, so a file we genuinely cannot inspect is reported rather than assumed
  clean. This is reachable only as a race — `Record` cannot journal a locked file in the first place, because
  its own `ReadAllBytes` throws — and the conservative answer is right for a race.
- **The directory loop needs no predicate helper**: `Directory.Exists` after the attempt *is* the claim. A
  directory we created that survives is residue whatever the reason, including the non-empty case.

The catches swallow rather than record, which is the opposite of what a catch usually earns. It is right here
precisely because the very next line asks the filesystem what actually happened, so the exception carries no
information the check does not already have.

### And `Writing` stops blaming the wrong file

Unit A also found that `Install` reads `.gitignore` to decide whether `.worktrees/` is already listed, and
reads `File.Exists(projectFile)`, **outside** `transaction.Write`. A `.gitignore` held by another process throws
there while `Writing` still holds the last kit entry's path, and the message names a file that was written
fine. Note that Unit B's listed read-only-`.gitignore` case does *not* catch this — that read succeeds — so it
would have survived the sweep.

Make the setter `internal` and have `Install` set `transaction.Writing` to the installer file it is about to
look at, immediately before each of the two reads. Two lines, and the message stops lying.

### Acceptance, added

- `InstallTransactionTests`: roll back over a destination made read-only after the transaction wrote it, and
  assert the returned failure list is **empty** because the bytes are as recorded; and roll back a path
  journaled as absent that now holds a directory nobody journaled, and assert the same.
- `KitInstallRollbackTests`: the read-only end-to-end case asserts `install_failed`, not `install_not_undone`.
  It already did; it would have failed against the frozen Design, and that failing test was the report.
- Unit B adds a row for a **locked** `.gitignore`, which is the `Writing` case, and one for a repository holding
  a directory named `.gitignore` — asserting `install_failed` and an untouched repository, not a successful
  install. Making it succeed is T-56's.

### Accepted as built — the amendment's own acceptance clause could not match its own case

Added after the build, because the same fault recurred one level down. The acceptance above reads *"roll back
over a destination made read-only **after** the transaction wrote it, and assert the returned failure list is
empty **because the bytes are as recorded**"*. Those two clauses contradict each other, and Unit A reported it
rather than picking one silently:

> If the write **succeeds** and the file is made read-only afterwards, the bytes on disk are the kit's, the
> journal holds the repository's originals, `WriteAllBytes` throws, and `IsAsRecorded` then correctly answers
> **false**. The file genuinely is not as recorded, and reporting it is right.

The state where the bytes *are* as recorded is the one where the write never landed — set the file read-only
first, let `Apply` throw, then roll back. That is `measure.ps1 -Fail readonly` exactly, and it is the case
Amendment 1 exists for, so it is what the test does. **The "after" ordering is struck.**

Also accepted: `IsAsRecorded` is written as an early return rather than the amendment's ternary, because
`undo.Content is not { } content ? … : …` binds `content` only in the negated branch. Same semantics, and the
better code.

This is the second clause in a T-55 spec that could not match the case it named, after Amendment 1's own
defect. Both were found by an implementer reading the frozen text against a measurement before building.

### Recorded

The spec asserted a rollback mechanism it had not measured, one paragraph after a Context section that had
measured everything else — including the two rows that disprove it. T-8's closing note says a spec that states
a mechanism it has not measured is a spec that will cost a round. This one did, and it was caught the same way:
an implementer traced the frozen Design against the spec's own table before building.

## Amendment 2 — which failure is load-bearing, once T-56 lands (2026-09-19)

Read off `task/T-56-preflight` while Unit A was building, and it changes the tests rather than the code.

T-56 adds an `Unwritable(…)` pass to `ReadManifest` that refuses, at validation with `invalid_manifest` and
exit 2, exactly the three repository-state families it was filed for — including **F3, a read-only
destination**. That is right for T-56. It also means that once T-56 lands, a read-only destination never
reaches the write phase, and this spec's clearest demonstration stops demonstrating anything.

So the two tasks are separated along the line that actually divides them:

> **T-56 closes what is decidable from the repository before a byte is written. T-55 covers the remainder that
> is not decidable at all** — and the exclusive `FileShare.None` lock is the canonical member of that
> remainder, because a pre-flight for "is this file locked" is a TOCTOU race by construction. Nothing can
> check it; only an undo can survive it.

### What changes

- **The lock is the load-bearing end-to-end case.** `KitInstallRollbackTests` keeps both, but the lock is the
  one that must still pass after T-56 integrates, and the one a validator should weigh. Say so in the class's
  doc comment, naming T-56, so the next person to see the read-only test fail knows it is integration and not
  regression.
- **The read-only test is true today and dies honestly.** Until T-56 lands it exercises the write phase exactly
  as the Context table measured. After T-56 lands it becomes an `invalid_manifest` refusal at exit 2 — still
  "nothing was left behind", by a different mechanism. Whoever integrates second re-points it at T-56's
  sentence rather than deleting it.
- **`measure.ps1 -Fail readonly` will read `exit : 2`, `install_failed` → `invalid_manifest`, after T-56.**
  `-Fail lock` is unaffected and stays the verification that matters. A validator seeing exit 2 on the
  read-only run against a tree that contains T-56 is seeing the two tasks compose, not a failure.
- **Nothing about `InstallTransaction` changes.** Its unit tests drive the type directly and are untouched by
  what validation does or does not refuse, which is the point of testing it on its own.

Amendment 1's second motivating case — a directory named `.gitignore` — is likewise pre-flighted away by T-56.
Amendment 1's fix stays: it is reachable today, the unit tests pin it directly, and "report the claim, not the
call" was wrong code regardless of which inputs happen to reach it.

## Amendment 3 — T-56 landed, and we integrate second (2026-09-19)

Amendment 2 said "whoever integrates second re-points it". That is us: `086fee9 Land T-56` is on `main`, and
this branch is behind it. This amendment is the whole of Unit C and changes nothing a validator was promised —
the Goal, the error codes, the rollback and `install_not_undone` are all as frozen.

`git merge main` into this branch conflicts in exactly one hunk: the block at the end of `KitCommands` holding
the write primitives. Unit A deleted them (they became `InstallTransaction`); T-56 rewrote them in place. Both
sides are right about their half, so neither side of the conflict is the answer.

### What T-56 actually did, and why it matters here

T-56's pre-flight `Writes(…)` originally answered from an entry's **mode**, while the write answers from its
**content** — `WriteFile` returns `unchanged` without writing when the bytes already match — so a read-only file
a re-install would have left alone was refused anyway. T-56 fixed that by making **one function the single
answer**: `Rendered(path, content, mode)` returns exactly what the install would leave in a file, or null when
it would not write at all. `WriteKitFile` became `Rendered(…) is { } bytes ? WriteFile(path, bytes) : "kept"`,
and `WriteSection` became `SectionDocument(path, content)`, which returns the document instead of writing it.

That property is the thing to preserve. A transactional write phase still wants one answer to "what would end
up in this file", and losing it is how T-56's defect comes back.

### The resolution

**`Rendered` and `SectionDocument` move into `InstallTransaction`, and the pre-flight calls them there.** The
dependency runs `Commands` → `Infrastructure`, which is the direction that already exists; the reverse would
have `Infrastructure` reaching back into `Commands`. Concretely:

- `KitCommands` keeps `Unwritable` and `Writes` unchanged in behaviour. `Writes` calls
  `InstallTransaction.Rendered(d.Resolved, content, entry.Mode)`. One character of the call site moves; the
  question it asks does not.
- `KitCommands.WriteKitFile`, `KitCommands.Rendered`, `KitCommands.WriteFile` and `KitCommands.SectionDocument`
  are deleted. After this, `KitCommands` writes no bytes at all — which is Unit A's point restated.
- `InstallTransaction` gains `internal static string? Rendered(string path, string content, string? mode)` and
  `private static string SectionDocument(string path, string content)`, taken **verbatim** from `main`.
  `InstallTransaction.WriteSection` is deleted: `SectionDocument` replaces it, and the transaction's own
  instance `WriteFile` does the writing.
- `Apply` takes T-56's shape, keeping Unit A's `Writing = path`:

  ```csharp
  internal string Apply(string path, string content, string? mode)
  {
      Writing = path;
      return Rendered(path, content, mode) is { } bytes ? WriteFile(path, bytes) : "kept";
  }
  ```

  `WriteFile` stays the transaction's instance method — comparing, then delegating to `Write`, which journals.
  It re-normalises line endings that `Rendered` already normalised; that is a no-op, it is what `main` does, and
  it is not this task's to tidy.
- `main` carries **two** `<summary>` tags on `SectionDocument`, one left from the rename. Carry a single one
  saying what the method now does. This is a doc comment inside a block we are moving, not a drive-by edit.

### The read-only test dies honestly, as Amendment 2 said it would

`KitInstallRollbackTests.A_read_only_destination_leaves_the_repository_exactly_as_it_was_found` asserts exit 1
and `install_failed`. After T-56, `Unwritable`'s F3 refuses that destination at validation. Re-point it, do not
delete it:

- rename it for what it now proves — a read-only destination is refused *before* the write phase, and the
  repository is untouched;
- assert `ExitCodes.RuleViolation` (2) and `invalid_manifest`, a message naming `c.md`, and `AsItWasFound()`
  exactly as it does now. `AsItWasFound` is the assertion that carries across both mechanisms and does not
  change;
- keep the `InstallsCompletely()` re-run at the end: safe to re-run is half the Goal under either mechanism;
- update the class doc comment's third paragraph from a prediction to a record — T-56 landed at `086fee9`, this
  is what it did, and the lock test below is the one that proves *this* task.

Nothing else in that class changes, and the **lock** test must pass with no edit whatsoever. If it needs one,
stop and report: that is the case T-55 exists for, and T-56 cannot have touched it.

`InstallTransactionTests` and `KitWriteModeTests` are untouched by this amendment. `KitPreflightTests` arrives
from `main` and must pass unedited — if a preflight test needs changing to accommodate this merge, the
resolution above is wrong somewhere and the answer is a report, not an edited assertion.

### Unit B, restated after the merge

Unit B has not run yet and runs against the merged branch, not against Unit A's tip. Two rows change and the
rest of its list stands:

- the **read-only** rows now expect exit 2 / `invalid_manifest` with an untouched repository, and
  `measure.ps1 -Fail readonly` reads `exit : 2`. That is the two tasks composing.
- the row for **a repository holding a directory named `.gitignore`** likewise now expects exit 2 /
  `invalid_manifest` — T-56's F2 — rather than a clean write-phase failure.

The **lock** row is unchanged and is the row the task turns on. Unit B must still show, in raw stderr, that no
case prints `Unhandled exception` or `muthur!<BaseAddress>`, and that no case leaves the repository altered.

### Unit C — integrate T-56

- **Files:** `src/Muthur.Cli/Commands/KitCommands.cs`, `src/Muthur.Cli/Infrastructure/InstallTransaction.cs`,
  `tests/Muthur.Cli.Tests/KitInstallRollbackTests.cs`, and the merge commit itself.
- **Does:** everything in this amendment.
- **Depends on:** Unit A (merged).
- **Acceptance:** `dotnet build` clean (warnings are errors) and `dotnet test` green, with
  `KitPreflightTests`, `KitInstallTests`, `KitManifestTests`, `KitWriteModeTests` and `InstallTransactionTests`
  all passing unedited, and the lock test passing unedited.
