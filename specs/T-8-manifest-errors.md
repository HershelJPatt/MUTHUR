# T-8 — A malformed kit.json fails like a rule violation, not an AOT crash

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`muthur kit install` reads its manifest through a validation pass that runs **before anything is written**.
A manifest that is malformed, wrongly typed, incomplete, or that points outside the directories it is allowed
to touch, fails with `{"code":"invalid_manifest","message":"…"}` on stderr and **exit 2**, naming the file, the
entry and the field. No unhandled exception, no address stack, and no half-installed repository.

## Context — measured, not assumed

Reproduced against the installed AOT CLI with `MUTHUR_KIT` pointed at a scratch kit. **Eleven manifests, ten
defects.** Every crash prints `Unhandled exception: …` followed by `muthur!<BaseAddress>+0x…` — the AOT build
sets `StackTraceSupport=false`, so a founder gets an address dump instead of a sentence.

| Manifest | Today | Exception |
|---|---|---|
| valid | exit 0 | — |
| `files` truncated mid-array | **exit 1, crash** | `JsonReaderException` |
| no `files` key | **exit 1, crash** | `KeyNotFoundException` |
| `"files": 42` | **exit 1, crash** | `InvalidOperationException` (needs Array) |
| entry is a string, not an object | **exit 1, crash** | `InvalidOperationException` (needs Object) |
| `"from": 42` | **exit 1, crash** | `InvalidOperationException` (needs String) |
| entry with no `to` | **exit 1, crash** | `KeyNotFoundException` |
| `"mode": 7` | **exit 1, crash** | `InvalidOperationException` (needs String) |
| `"validator": "yes"` | **exit 1, crash** | `InvalidOperationException` (needs Boolean) |
| `from` names a file that does not exist | **exit 1, crash** | `FileNotFoundException` |
| a `{{core:nope.md}}` token in a source file | **exit 1, crash** | `FileNotFoundException` |

The two that do **not** crash are worse:

| Manifest | Today | What happened |
|---|---|---|
| `"from": "../../secret.txt"` | **exit 0** | copied a file from outside the kit into the repository |
| `"to": "../OUTSIDE/escaped.md"` | **exit 0** | wrote **outside the repository** |
| `"to": "<absolute path>"` | **exit 0** | wrote to that absolute path, anywhere on disk |

`Path.Combine(repo, to)` returns `to` unchanged when `to` is absolute, and follows `..` when it is relative.
So a manifest chooses where its files land, and `--repo` is advisory. The founder is invited to edit
`kit.json`, so this is not a privilege boundary in the ordinary case — but "install a kit" should not be able
to write to `C:\Windows`, and a kit obtained from anywhere other than this repository makes it a real one.

**A crash also leaves a half-installed repository.** Entries are written as the loop walks, so the manifest
whose *ninth* entry is wrong has already written eight. Confirmed: after the `"validator":"yes"` crash the
scratch repo held `briefs/x.md`, `docs/dummy.md`, `.gitignore` and `muthur.project.json`.

### The code

`src/Muthur.Cli/Commands/KitCommands.cs`, `Install`. It parses with `JsonDocument.Parse` and then reads with
raw `JsonElement` accessors inside the write loop:

```csharp
using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
…
foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
{
    var from = Path.GetFullPath(Path.Combine(harnessDir, file.GetProperty("from").GetString()!));
    var to = file.GetProperty("to").GetString()!;
    var mode = file.TryGetProperty("mode", out var m) ? m.GetString() : "replace";
    var content = Expand(File.ReadAllText(from), kitDir);
    var status = WriteKitFile(Path.Combine(repo, to), content, mode);
    if (to.StartsWith(BriefDirectory, …))
        briefs.Add((to, file.TryGetProperty("validator", out var v) && v.GetBoolean()));
    …
}
```

Every accessor named above throws on the wrong type, and nothing catches. `Expand`
(`KitCommands.cs`, bottom of the file) does `File.ReadAllText` on whatever a `{{core:…}}` token names.

### The idiom to follow

`Output.Error(code, message, exitCode)` writes `{"code":…,"message":…}` to stderr through the source-generated
`Relaxed.ErrorResponse` (AOT-safe, no reflection) and returns the code. `Install` already uses it three times:

```csharp
return Output.Error("unknown_harness", $"No kit for harness '{harness}'. Try: muthur kit list", ExitCodes.RuleViolation);
return Output.Error("repo_not_found", $"'{repo}' is not a directory.", ExitCodes.NotFound);
```

`ExitCodes.RuleViolation` is 2 (`src/Muthur.Contracts/Errors.cs`). `CLAUDE.md`: rule violation → exit 2.

## Non-goals

- Any change to the hub, the API, or `muthur kit list`. `list` only enumerates directories containing a
  `kit.json`; it never parses one.
- Rewriting the manifest format, adding a schema file, or introducing a JSON schema validator.
- Reporting every problem in one run. One clear first failure matches every other error in this CLI.
- Changing `WriteKitFile` or the `create`/`replace`/`section` write modes.
- Making the three shipped manifests (`kit/claude`, `kit/codex`, `kit/generic`) any different. They must
  continue to install byte-for-byte as they do today; that is an acceptance criterion, not a hope.

## Design

### Shape

Add to `KitCommands` a validation pass that turns the parsed document into a list of validated entries, or an
error. Nothing is written until it has succeeded for **every** entry.

```csharp
/// <summary>One manifest entry, after validation: every field present, typed, and inside its directory.</summary>
internal readonly record struct KitEntry(string From, string To, string Mode, bool Validator);
```

```csharp
/// <summary>
/// The manifest's entries, or the error to return. Everything a bad manifest can do is decided here, before
/// a single file is written: an entry that fails leaves the repository untouched rather than half-installed.
/// </summary>
internal static bool TryReadManifest(
    string manifestPath, string harnessDir, string kitDir, string repo,
    out List<KitEntry> entries, out string problem)
```

`Install` calls it immediately after the `repo_not_found` check and returns
`Output.Error("invalid_manifest", problem, ExitCodes.RuleViolation)` when it is false. The existing write loop
then iterates `entries` and uses their fields directly — no `JsonElement` accessors survive in it.

### The rules, in order, and the message each produces

Every message begins with the manifest path so the founder knows which file to open. `<n>` is the entry's
zero-based index in `files`; where the entry has a usable `to`, name it as well.

1. **Unreadable file** — `IOException`/`UnauthorizedAccessException` from `File.ReadAllText`:
   `"<manifestPath> could not be read: <ex.Message>"`
2. **Not JSON** — `JsonException`: `"<manifestPath> is not valid JSON: <ex.Message>"`
3. **Root not an object**: `"<manifestPath> must contain a JSON object."`
4. **No `files` key**: `"<manifestPath> has no \"files\" array."`
5. **`files` not an array**: `"<manifestPath>: \"files\" must be an array, not <kind>."`
6. **Entry not an object**: `"<manifestPath>: entry <n> must be an object, not <kind>."`
7. **`from` missing**: `"<manifestPath>: entry <n> has no \"from\"."`
8. **`from` not a string**: `"<manifestPath>: entry <n> has a \"from\" that is <kind>, not a string."`
9. **`to` missing / not a string** — same two, for `"to"`.
10. **`mode` present and not a string**: `"<manifestPath>: entry <n> (<to>) has a \"mode\" that is <kind>, not a string."`
11. **`validator` present and not a boolean**: `… has a \"validator\" that is <kind>, not a boolean."`
12. **`from` escapes the kit directory** — resolve `Path.GetFullPath(Path.Combine(harnessDir, from))` and
    require it to be under `Path.GetFullPath(kitDir)`:
    `"<manifestPath>: entry <n> (<to>) reads \"<from>\", which is outside the kit directory."`
13. **`from` does not exist**:
    `"<manifestPath>: entry <n> (<to>) reads \"<from>\", which does not exist."`
14. **`to` is absolute or escapes the repository** — reject `Path.IsPathRooted(to)`, and resolve
    `Path.GetFullPath(Path.Combine(repo, to))` and require it under `Path.GetFullPath(repo)`:
    `"<manifestPath>: entry <n> writes \"<to>\", which is outside the repository."`
15. **A `{{core:…}}` token naming a file that is not in `kit/core`** — checked here, not at expansion time:
    `"<manifestPath>: entry <n> (<to>) includes \"{{core:<name>}}\", which does not exist."`

`<kind>` is the `JsonValueKind` lowercased (`number`, `string`, `array`, `object`, `true`, `false`, `null`).
Write a tiny local helper for it rather than repeating `.ValueKind.ToString().ToLowerInvariant()`.

**Containment test.** Compare with `string.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)`
after `Path.GetFullPath` on both sides and `TrimEnd` of any trailing separator on the root. A path equal to the
root itself is not a file and fails rule 13 anyway. Do not use `Uri` or `Path.GetRelativePath` string tests.

**Rule 15's scan** reads each `from` file once during validation. That read is the same one the write loop
needs, so hold the content on the `KitEntry`? **No** — keep `KitEntry` to the four manifest fields and let the
write loop re-read. Validation is one pass over a handful of small files; correctness beats saving a read, and
a `KitEntry` that carries file content is a different kind of object.

### `Expand` stays as it is

Rule 15 makes the missing-include case unreachable in `kit install`. Leave `Expand` unchanged; it is also the
place a future caller would hit, and a defensive check there would duplicate the message without the manifest
context that makes it useful.

## Units of work

### Unit A — validate the manifest before writing anything
- **Files:** `src/Muthur.Cli/Commands/KitCommands.cs`, new `tests/Muthur.Cli.Tests/KitManifestTests.cs`.
- **Does:** everything under Design.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and a test per numbered
  rule above asserting the exit code is `ExitCodes.RuleViolation`, the code is `invalid_manifest`, and the
  message names the entry index and field. Plus:
  - **the three shipped kits still install** — assert `kit install` for `claude`, `codex` and `generic` into
    temp repositories still exits 0 and writes what it wrote before;
  - **nothing is written when validation fails** — install a manifest whose *second* entry is bad into an
    empty temp repo and assert the repo is still empty afterwards, including that `.gitignore` and
    `muthur.project.json` were not created.

Do not touch `tests/Muthur.Cli.Tests/KitInstallTests.cs` or `KitWriteModeTests.cs`; a third file keeps this
task clear of T-20, which adds `KitRosterTests.cs` and is in validation.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then the founder's own failure, against the installed AOT CLI — the crash only looks like a crash
in an AOT build, so a `dotnet run` check would not show what this task fixes:

```powershell
pwsh ./scripts/install.ps1 -Destination ./artifacts/t8
$kit = "<scratch>\kit"; New-Item -ItemType Directory -Force "$kit\broken","$kit\core","<scratch>\repo" | Out-Null
Set-Content "$kit\core\dummy.md" "x"
Set-Content "$kit\broken\kit.json" '{"harness":"broken","files":[{"from":"../core/dummy.md","to":"docs/x.md","mode":7}]}'
$env:MUTHUR_KIT = $kit
./artifacts/t8/muthur.exe kit install --harness broken --repo "<scratch>\repo"
$LASTEXITCODE
```

Passing looks like: one line of JSON on stderr whose `code` is `invalid_manifest` and whose `message` names
`entry 0` and `"mode"`, `$LASTEXITCODE` of **2**, no `Unhandled exception`, no `muthur!<BaseAddress>` and an
empty `<scratch>\repo`.

Repeat for each row of the Context table; every one must give exit 2 and a sentence. The two rows that exit 0
today — a `from` outside the kit and a `to` outside the repo — must now exit 2, and **nothing may appear
outside the repository directory**.

Finally, `muthur kit install --harness claude --repo <a temp dir>` must still exit 0 and install the real kit.

No browser is needed and none should be used.

## Out of scope / follow-ups

- **`kit install` is not transactional.** This task makes it validate-then-write, so a *manifest* fault leaves
  nothing behind. A write that fails halfway — a locked file, a full disk — still can. Making the write phase
  roll back is a different task with a different cost.
- **`MUTHUR_KIT` pointing at a kit from outside this repository** is the case that turns the containment rules
  from tidiness into a boundary. Nothing today says where a kit may come from. Worth its own task if kits ever
  become shareable.

## Amendment 1 — two decisions the build surfaced (2026-09-19)

**1. The `MUTHUR_KIT` constraint gets a collection, not an assembly.**

`KitInstallTests` (T-46) already points `MUTHUR_KIT` at the repository's own kit "for the life of the class",
on the stated grounds that no other class in the assembly reads it. This task adds a second class that must
point it at a scratch kit, which retires that reasoning. xUnit 2.9.3 runs collections in parallel, so without
something the two could swap the kit out from under each other mid-install — a race that would reproduce
rarely and read as a mystery.

The implementer's first answer was `[assembly: CollectionBehavior(DisableTestParallelization = true)]`. It
works, and it is the wrong size: it serializes the whole `Muthur.Cli.Tests` assembly forever as a side effect
of one file, where nobody later wondering why the suite is slow would think to look. Replaced by a
`[CollectionDefinition(..., DisableParallelization = true)]` that both classes join, so the constraint is
declared where it is caused and the rest of the assembly stays parallel.

`KitRosterTests` (T-20, in validation) reads `kit.json` files off disk and never sets the variable, so it does
not join and does not conflict.

**2. An empty `to` gets its own rule.**

`to: ""` fell into rule 14 and reported *"writes "", which is outside the repository"* — true, since
`Path.Combine(repo, "")` resolves to the repository root and the containment test requires a path strictly
under it, but no help to someone who typed an empty string. Added as **rule 9b**, checked with the other `to`
checks:

> `"<manifestPath>: entry <n> has an empty \"to\"."`

### Scope of the claim this task makes

Worth stating precisely, because the implementer drew the line and it is the right one. Rule 15 reads each
`from` file after rules 12 and 13 have proved it is inside the kit and exists. If *that* read fails — an
exclusive lock or a permission change in the window between the check and the read — the exception is
unhandled, exactly as the write loop's own `File.ReadAllText` has always been.

No manifest content can reach it. So what T-8 delivers is **"no unhandled exception a manifest can cause"**,
not "no unhandled exception". Inventing a sixteenth rule for an unreachable case would have made the spec
look more complete and the software no safer.

### Accepted as built

- **Rule 1 is unit-tested rather than end-to-end.** `Install` checks `File.Exists` before validating, so the
  only route to it through `kit install` is an exclusive lock, which is a Windows-only construction. The test
  calls `TryReadManifest` with a directory as the manifest path — `File.ReadAllText` on a directory fails on
  every platform — and the fourteen end-to-end rows carry the exit-code and code mapping that rule 1 shares.
- **Proof 3 is stronger than the acceptance asked for.** Rather than assert the shipped kits still install,
  the implementer published the *pre-change* commit as a second binary, installed all three harnesses with
  both, and compared the trees file by file: 11 of 12 files identical per harness, the twelfth being
  `muthur.project.json`, whose generated key is the temp directory's name. That is the difference between
  "still works" and "unchanged".

## Proof (2026-09-19)

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test
  Muthur.Launch.Tests    23/23
  Muthur.Core.Tests      3708/3708
  Muthur.Cli.Tests       88/88      (65 before this task)
  Muthur.Server.Tests    280/280
```

The Context table's own reproduction, re-run by the orchestrator against an installed AOT CLI from this
branch. `repo=` counts every entry under the target repository afterwards, directories included:

```
invalid-json       exit=2 clean  repo=0      to-empty           exit=2 clean  repo=0
files-missing      exit=2 clean  repo=0      mode-wrong-type    exit=2 clean  repo=0
files-not-array    exit=2 clean  repo=0      validator-wrong    exit=2 clean  repo=0
entry-not-object   exit=2 clean  repo=0      from-missing       exit=2 clean  repo=0
from-wrong-type    exit=2 clean  repo=0      bad-include        exit=2 clean  repo=0
to-missing         exit=2 clean  repo=0      from-traversal     exit=2 clean  repo=0
second-entry-bad   exit=2 clean  repo=0      to-traversal       exit=2 clean  repo=0
valid              exit=0 clean  repo=4      to-absolute        exit=2 clean  repo=0

sentinel directory beside every repo: 0 entries
```

`clean` means neither `Unhandled exception` nor `muthur!<BaseAddress>` appeared. Sixteen cases, fifteen
refusals and one success, and **nothing was written by any refusal** — including `second-entry-bad`, which
under the old binary wrote its first entry, `.gitignore` and `muthur.project.json` before crashing.

The two that used to exit 0 now exit 2, and the sentinel directory that sat beside every repository for the
whole run is empty: a manifest can no longer choose where its files land.

The real kits, with `MUTHUR_KIT` unset so the CLI uses its own bundled `kit/`:

```
claude   exit=0 files=12
codex    exit=0 files=12
generic  exit=0 files=12
unexpanded {{core: tokens: 0
```

The implementer additionally published the pre-change commit as a second binary and compared the installed
trees file by file: 11 of 12 identical per harness, the twelfth being `muthur.project.json`, whose generated
key is the temp directory's name. Nothing about the shipped kits changed.

No browser was used.

## Amendment 2 — the claim was false, not narrow (2026-09-19)

`conductor-validator-t-8` failed T-8. Everything the validation pass covers holds — they re-ran all sixteen
rows of the Proof table independently and wrote twenty-one further manifests attacking the containment rules,
all refused cleanly. Three manifest shapes still crash with `Unhandled exception` and a
`muthur!<BaseAddress>+0x…` dump at exit 1.

| Manifest | Where it dies | Exception |
|---|---|---|
| `"to": "docs/x\u0000.md"` | rule 14, `Path.GetFullPath` | `ArgumentException: Null character in path.` |
| `"from": "go\u0000od.md"` | rule 12, `Path.GetFullPath` | the same, one frame earlier |
| `"to": "<256-char component>/x.md"` | the **write** phase, `Directory.CreateDirectory` | `IOException: The filename, directory name, or volume label syntax is incorrect.` |

`System.Text.Json` accepts `\u0000` inside a string; `Path.GetFullPath` does not. Rules 12–14 call it
unguarded, so validation crashes before it can refuse anything.

**Amendment 1 does not cover these, and I should not have let it read as if it might.** Its narrowing —
*"no unhandled exception a manifest can cause"* — was about one genuinely unreachable case: a file read
failing between the containment check and the read itself. All three shapes above are reached by manifest
content and nothing else, on a repository the founder named, with no concurrent process. The Goal's sentence
was simply not met. Narrowing a claim to the case you thought of is not the same as bounding it.

### The fix is a class, not three cases

Adding "reject NUL" and "reject long components" would fix these three and leave the next shape to another
validator. The rules become:

**Rule 16 — a string that cannot be used as a path at all.** Wrap every path resolution in validation —
both `Path.GetFullPath` calls, and any other that takes manifest text — and treat `ArgumentException`,
`NotSupportedException` and `PathTooLongException` as a refusal rather than letting them escape:

```
"<manifestPath>: entry <n> has a \"<field>\" that is not a usable path: <ex.Message>"
```

`<field>` is `from` or `to`, whichever was being resolved. This catches the NUL cases and every other string
the platform rejects outright, including ones nobody has thought of yet.

**Rule 17 — a path component longer than 255 characters.** This one escapes validation entirely: 
`Path.GetFullPath` accepts it and `Directory.CreateDirectory` dies later, which is also why it violates
"nothing is written when validation fails" in spirit even though the repo happened to be empty. It is
decidable from the manifest string alone — the validator bounded it: six 61-character segments install fine,
one 200-character segment installs fine, 256 and 300 crash — so it is the per-component limit, not total
path length.

Check each component of `to`, and of `from`, against a named constant:

```
"<manifestPath>: entry <n> writes \"<to>\", whose path component \"<first 40 chars>…\" is <len> characters; the limit is 255."
```

255 is NTFS's per-component limit and also most Linux filesystems'. Name it as a constant with a comment
saying that, rather than writing `255` into two expressions.

### And then sweep, rather than stopping at three

The three shapes were found by a validator attacking the parser, not by the spec anticipating them. So the
unit does not finish at rule 17: it runs a list of hostile strings through the installed CLI and reports what
happens to each — Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`, `LPT1`), a trailing dot
and a trailing space, `.` and `..` as whole components, a bare drive letter, a UNC path, an alternate data
stream (`x.md:stream`), a `to` of `.`, control characters other than NUL, and an over-long `from`.

Every one must produce exit 2 or a clean exit 0 — never a crash. Any that crashes is either a new rule or an
honest finding reported rather than fixed. **Do not paper over one by widening rule 16's catch to
`Exception`**: a catch that broad would also swallow a bug in the validator itself, and the point of this
task is that a founder gets a sentence naming what is wrong, not that nothing is ever printed.

## Amendment 3 — the sweep found a fourth, and it got a rule (2026-09-19)

Amendment 2's sweep was the point of the exercise, and it earned its place: a `to` containing a control
character other than NUL — `\u0001`, `\u001f` — still crashed after rules 16 and 17 were in. `Path.GetFullPath`
accepts it; Win32 refuses it in a name, so it died in `Directory.CreateDirectory` **after creating
`repo\docs\`**, breaking "nothing is written when validation fails" as well.

The implementer did not widen rule 16's catch to make it go away. It got:

**Rule 18 — a character the platform will not put in a file name.** Stated by the platform's own
`Path.GetInvalidFileNameChars()` through a cached `SearchValues<char>`, tested per path component because the
separators on that list are legitimate *between* components:

```
"<manifestPath>: entry <n> writes \"<to>\", which contains \uXXXX, a character this platform does not allow in a file name."
```

Same class as rule 17 — a component no filesystem will accept — said by characters instead of by length.

**Rule 18 applies to `to` only.** A `from` the platform cannot name is a file that cannot exist, so rule 13
already refuses it with a better sentence and no crash. Confirmed rather than assumed.

### Decisions taken on the sweep's findings

- **`x.md:stream` now exits 2, where it used to exit 0.** It wrote an empty `x.md` and hid the content in an
  alternate data stream — the path the result reported was not the thing that existed. `:` is on the
  platform's own forbidden list and no exception is carved out of that answer to preserve a behaviour nobody
  asked for. A behaviour change on a previously-passing string, recorded deliberately.
- **`CON`, `PRN`, `AUX`, `COM1`, `LPT1` install as real files** inside the repository on this machine — 12
  bytes, the source content, not redirected to a device. Clean exit 0, inside the repo, so left alone.
- **`docs/x.md.` and `docs/x.md ` install**, with Windows normalising the trailing dot or space away, so the
  file on disk is `x.md` while the result reports `docs/x.md.`. Clean, contained, and the same shape of
  mismatch as the ADS case one layer lower. Not fixed here; filed.

### Rule 17's boundary, measured

Component lengths 200 / 254 / 255 / 256, at a 13-character repository path and at a 117-character one — the
latter putting the total well past `MAX_PATH`:

```
repo-path= 13   200/254/255 -> exit 0    256 -> exit 2
repo-path=117   200/254/255 -> exit 0    256 -> exit 2
```

No crash on either side at either depth. The validator's reading holds: per-component, not total, and 255 is
the cut.

## Proof of Amendments 2 and 3 (2026-09-19)

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test
  Muthur.Launch.Tests    23/23
  Muthur.Core.Tests      3708/3708
  Muthur.Cli.Tests       93/93      (65 before T-8, 88 after Unit A)
  Muthur.Server.Tests    280/280
```

The validator's three reproductions plus the new shapes and the boundary, re-run by the orchestrator against
a CLI installed from this branch:

```
to-nul         exit=2 clean repo=0  entry 0 has a "to" that is not a usable path: Null character in path.
from-nul       exit=2 clean repo=0  entry 0 has a "from" that is not a usable path: Null character in path.
to-256         exit=2 clean repo=0  entry 0 writes "bbb…", whose path component … is 256 characters
to-ctrl-01     exit=2 clean repo=0  entry 0 writes "docs/x\u0001.md", which contains \u0001, …
alt-stream     exit=2 clean repo=0  entry 0 writes "x.md:stream", which contains :, …
to-255-ok      exit=0 clean repo=4  installed
valid          exit=0 clean repo=4  installed

sentinel directory beside every repo: 0 entries
```

The implementer's own sweep covered 25 strings, every one exit 2 or a clean exit 0, and re-ran all eighteen
rows of the earlier Proof table plus the three shipped kits. Nothing crashed.

**A note on this orchestrator's first run of the above:** every row came back exit 2, including the two that
should have installed. That was my harness, not the product — `Set-Content` had not created the fixture, so
everything hit rule 13 ("reads \"good.md\", which does not exist"). Worth recording because a table where
*everything* fails is the shape a bad harness makes, and reading it as a result would have sent a correct
branch back to an implementer.

## Out of scope / follow-ups (added)

- **The path we report is not always the path we wrote.** `docs/x.md.` and `docs/x.md ` install as `x.md`
  because Windows normalises the trailing character, while the result JSON echoes what the manifest said.
  Harmless today and contained, but a result that names a file which does not exist under that name is worth
  its own task.

## Amendment 4 — enumeration is not closing the class (2026-09-19)

`conductor-validator` failed T-8 a second time. Rules 16–18 hold: all sixteen Proof rows plus fifty-three
further manifests refuse cleanly, nothing escapes the repository, the shipped kits install unchanged. Twelve
further shapes, in three families, still crash with an address dump at exit 1. All twelve crash the pre-change
binary too, so none is a regression — they are the same class, still open.

**Two of the three families are not path-string problems**, so rules 16–18 could not have caught them.

### Family A — a `to` that names a directory (4 shapes)

`"to": "docs/"` satisfies every rule 1–18: a non-empty, unrooted string, inside the repository, no component
over 255, and `UnusableCharacter` splits on the separator so the empty trailing component holds nothing
forbidden. `WriteFile` then creates `repo\docs` and calls `File.WriteAllText` on a path ending in a separator.

```
exit=1  DirectoryNotFoundException   repository afterwards: dir docs
```

Also `"docs\\"`, `"a/b/"` (leaves `a` **and** `a\b`), `"briefs/"`. Each leaves a half-installed repository,
which the Goal forbids in its own sentence.

### Family B — two entries that collide as file vs directory (4 shapes)

`[{"to":"docs/x.md"},{"to":"docs"}]`. **Each entry is individually valid; the fault is the pair.** Nothing in
`TryReadManifest` looks at one entry in the light of another, so the write loop meets the collision with
earlier entries already on disk.

```
docs/x.md then docs   →  UnauthorizedAccessException   left: dir docs, file docs\x.md
docs then docs/x.md   →  IOException                   left: file docs
```

### Family C — a lone surrogate escape (4 shapes)

`"to": "docs/x\ud800.md"`. `JsonDocument.Parse` accepts it — a legal JSON escape — and `GetString()` refuses
to return an unpaired surrogate, throwing *after* the `ValueKind` check has said "string".

```
exit=1  InvalidOperationException: Cannot read incomplete UTF-16 JSON text as string with missing low surrogate.
```

Also through `from` and through `mode`. Rule 16 cannot reach it: it wraps `Path.GetFullPath`, and this throws
two statements earlier, before any path exists. As the validator put it — *the thing that exists to turn a bad
manifest into a sentence is itself the thing that crashes.*

### The rules

**Rule 19 — a `to` that names a directory.** Decidable from the string, before anything is written. Refuse a
`to` whose last component is empty, i.e. one ending in `/` or `\`:

```
"<manifestPath>: entry <n> writes \"<to>\", which names a directory, not a file."
```

**Rule 20 — destinations that collide.** The first rule in this spec that reads the manifest as a whole rather
than an entry at a time. After every entry validates individually, compare the resolved `to` paths: refuse
when one is an ancestor directory of another, naming **both** entries:

```
"<manifestPath>: entry <a> writes \"<to-a>\" and entry <b> writes \"<to-b>\"; one cannot be both a file and a directory."
```

Two entries writing the *same* path are not an error — that is last-one-wins, which the manifest format has
always allowed. Only file-versus-directory collides.

**Rule 21 — a string the reader will not hand back.** Wrap each `GetString()` in `TryReadEntry`. The
`ValueKind` check has already established the element is a string, so the only remaining cause is an escape
`System.Text.Json` will parse but not materialise:

```
"<manifestPath>: entry <n> has a \"<field>\" that is not readable text: <ex.Message>"
```

Catch `InvalidOperationException` **around the `GetString()` call alone**, never around a block.

### And a guard, because three rounds say enumeration will not close this

Amendment 2 told the implementer not to widen rule 16's catch to `Exception`, and that was right: the fix for
a *known, reachable* shape is a rule with a message, not a silence. It is still right. But three rounds have
each found "one more family", and the Goal's sentence — *no unhandled exception, no address stack* — is a
categorical claim that a list of rules cannot keep.

So `TryReadManifest` gains a last-resort guard **around its whole body**, outside every rule:

```
"<manifestPath> could not be read: <ex.GetType().Name>: <ex.Message>. This is a defect in MUTHUR's manifest
 reader, not necessarily in your manifest — please report it."
```

`invalid_manifest`, exit 2, and no file written, because it is still inside the validation pass. Three things
make this different from the widening Amendment 2 refused:

- it is **outside** the rules, not inside one, so it never shortens a specific message into a generic one;
- it **names the exception type** and says the reader is at fault, so a crash-shaped bug stays legible in a
  bug report instead of reading as "your JSON is bad";
- it bounds the **message**, not the filesystem. It cannot un-write a file, which is exactly why families A
  and B must be caught by rules 19 and 20 pre-flight and may not be left to it.

The guard is a floor, not a ceiling. A shape that reaches it is still a missing rule and should be filed.

### The write phase is still not guarded, deliberately

Families A and B die in `WriteFile`, after files exist. Rules 19 and 20 stop the twelve known shapes before
the write begins. A guard around the write loop would turn a future write-phase crash into a sentence but
would still leave a half-installed repository, so it would satisfy half the Goal and hide the worse half.
Making the write phase transactional is the honest fix and remains the follow-up this spec already records.

## Amendment 5 — the guard caught nothing, and that is the finding (2026-09-19)

Rules 19–21 and the guard are built and hold: all fourteen shapes of Amendment 4's three families refuse at
exit 2 with an empty repository, and twenty-three regression rows still behave. The implementer then attacked
its own work with 31 further manifests and found **six that still crash**. None is a regression — the
pre-change binary crashes on nine, so the change strictly reduces the set.

**The guard caught nothing. Not one manifest in about seventy reached it.**

That is worth more than the six shapes. Amendment 4 argued that enumeration was not closing the class, and
added a guard around the validation pass on that reasoning. The reasoning was right about the symptom and
wrong about the location: **every remaining crash is in the write phase**, which Amendment 4 deliberately
leaves unguarded, so the guard could not have caught any of them and widening it would fix nothing. The open
class was never in the reader.

The guard stays — it is cheap, and its absence is exactly what let family C escape for two rounds — but it is
now known to be a floor nobody has stood on, not the answer to this task.

### Four of the six are in-class extensions of rules 19 and 20

| Shape | Why the existing rule misses it |
|---|---|
| `to: "docs/."` | names a directory, but its last component is `.`, not empty |
| `to: "docs/x.md/.."` | the same, with `..` |
| `to: ".gitignore/x.md"` | collides with a file `Install` writes but the manifest never names |
| `to: "muthur.project.json/x.md"` | the same |

**Rule 19 widens.** Its predicate as frozen — "ends in `/` or `\`" — is narrower than its own name. A `to`
whose last component is empty, `.`, or `..` names a directory:

```
"<manifestPath>: entry <n> writes \"<to>\", which names a directory, not a file."
```

**Rule 20 seeds.** `Install` unconditionally writes `repo\.gitignore` and `repo\muthur.project.json`; an entry
writing *under* either turns it into a directory first. Seed the destination set with those two resolved
paths as implicit entries, and name them as the installer's own in the message:

```
"<manifestPath>: entry <n> writes \"<to>\", which collides with \"<file>\", a file kit install writes itself."
```

### The other two are new territory, and both are in scope

**`to: "docs"` into a repository that already holds `docs\`** is decided by repository *state*, not by the
manifest. The implementer flagged that checking it means the validator starts reading the target repo — but it
already does: **rule 13 calls `File.Exists(source)`**. Reading the destination is the same kind of question,
one directory over. Add, with the other `to` rules:

```
"<manifestPath>: entry <n> writes \"<to>\", which is an existing directory in the repository."
```

Reachable in ordinary use: re-running `kit install` after a manifest changed a `to` from a file to a directory
name.

**`to: "NUL/x.md"`** is the Windows device namespace one level above rule 18. `NUL` is not on
`Path.GetInvalidFileNameChars()`, resolves fine, and `Directory.CreateDirectory` follows it to `\.\NUL`.
Amendment 3 measured reserved names as *file* names and left them alone because they install as real files;
the implementer measured them as *directory* components and found `NUL` crashes while `CON` and `COM1` do not.

Refuse a reserved device name **as a directory component**, on the platform that reserves them, leaving
Amendment 3's measured file-name behaviour untouched:

```
"<manifestPath>: entry <n> writes \"<to>\", whose component \"<name>\" is a reserved device name on this platform."
```

Ask the platform the way rule 18 does rather than carrying a list if there is an API for it; if there is not,
a named constant array with `CON PRN AUX NUL COM1-9 LPT1-9` and a comment is fine.

### What this task will claim when it is done

Not "no unhandled exception". That sentence has now been wrong three times, and the reason is structural: the
write phase is neither guarded nor transactional, so a shape that reaches it crashes *and* leaves a
half-installed repository — five of the six above do. The Goal is amended to what the work actually delivers:

> **Every manifest shape anyone has found is refused before a byte is written**, with a stable code, exit 2
> and a sentence naming the file, the entry and the field. The write phase is not transactional; a failure
> there is a separate defect with its own task.

Making the write loop transactional — stage, then move into place, then roll back on failure — closes the
remainder in one move and is the honest end of this line of work. It is a real design change to `Install` and
deserves its own spec and its own verdict, so it is filed rather than folded in here.

## Amendment 6 — a measurement I did not take, and the line this task stops at (2026-09-19)

Units C and D are in and the six shapes of Amendment 5 are closed. Three corrections, one of them mine.

### 1. The reserved-device rule over-refuses, because I asserted instead of measuring

Amendment 5 said `NUL` crashes as a directory component "while `CON` and `COM1` do not", and then told the
implementer to refuse the whole list `CON PRN AUX NUL COM1-9 LPT1-9`. Those two sentences contradict each
other and I did not notice. The implementer measured `Directory.CreateDirectory` before writing the rule:

```
NUL, nul, "NUL ", "NUL."   -> DirectoryNotFoundException
CON  PRN  AUX  COM0  COM1  LPT1  NUL.md  CON.md  COM1.md   -> all create real directories
```

**Only `NUL` fails on this platform.** The other eight names install cleanly, so refusing them refuses
manifests that work. The implementer built the frozen list anyway — correctly, the spec is frozen — and
flagged it as an over-refusal and a behaviour change on previously-passing strings.

**Narrow the rule to `NUL`.** Two reasons, and the second is the one that settles it:

- The measurement says `NUL` is the crash. Nothing else on the list is.
- **Amendment 3 already decided this question the other way for file names.** It measured `CON`, `PRN`,
  `AUX`, `COM1` and `LPT1` as *file* names, found they install as real files, and deliberately left them
  alone. Refusing the same names one path component earlier, where they also work, would make the two
  decisions contradict each other for no measured reason.

Keep the `TrimEnd(' ', '.')` before the comparison — `"NUL "` and `"NUL."` are the same device — and keep the
`OperatingSystem.IsWindows()` gate. The message stays as written.

### 2. F4 — a directory component Win32 trims to nothing

The implementer's own attack found eight further crashes in four families. Seven are decided by the
*repository*, not the manifest, and are filed below. **One family is decidable from the manifest string
alone**, so it is in scope and breaks the Goal as this spec now words it:

```
to: "docs /x.md"   (trailing space)   -> DirectoryNotFoundException, leaves 1 entry behind
to: ".../x.md"                        -> DirectoryNotFoundException
to: "   /x.md"                        -> DirectoryNotFoundException
```

Win32 trims trailing spaces and dots from a path component; a component made only of them trims to nothing,
and the directory cannot be created. Same class as rules 17, 18 and 19 — a component that resolves but cannot
be created — and the same fix shape. Refuse a `to` any of whose components is empty after
`TrimEnd(' ', '.')`, checked with the other `to` rules:

```
"<manifestPath>: entry <n> writes \"<to>\", whose component \"<component>\" is only spaces and dots, which this platform trims to nothing."
```

This also repairs a message the implementer flagged as imprecise: `to: "   "` is currently refused as *"an
existing directory in the repository"*, which is true only because Win32 trimmed it to the repository root.
After this rule it is refused for what it actually is.

### 3. What this task does not close, and will not

The other seven crashes need a particular repository, not a particular manifest:

| Family | Shape | Filed |
|---|---|---|
| F1 | an ancestor of `to` already exists as a **file** — the exact mirror of the rule Unit D added | T-56 |
| F2 | the repository already holds a **directory** named `.gitignore` or `muthur.project.json` — **every install crashes, including the three shipped kits, with no manifest involved** | T-56 |
| F3 | the destination exists and is read-only | T-56 |

And one that is not a crash at all but defeats rule 14's central claim:

> **Containment does not survive a directory junction.** With `repo\link` a junction to somewhere else,
> `to: "link/escaped.md"` exits **0** and writes the file outside the repository — confirmed by reading the
> content back from the target. `Path.GetFullPath` normalises the string and does not follow reparse points,
> so rule 14 is satisfied by a path that does not stay where it resolves. Filed as **T-57**, because rule
> 14's whole sentence is "a manifest can no longer choose where its files land", and this is the one way it
> still can.

None of the eight is a regression: the pre-change binary crashes on 18 of the same 25 manifests, this one on
8, and after this amendment on 3 — all repository-decided.

**The Goal stands as Amendment 5 amended it** — every manifest shape anyone has found is refused before a
byte is written — and after change 2 above that is true for the first time. It was never a claim about
repository state, and F1–F3 are exactly that. Note that **T-55's transactional write phase does not fix F2
either**: the fault is that `Install` writes its own two files without checking what is already at those
paths.

### Also recorded

The guard caught **nothing in 136 manifests** — double the previous unit's sample, same result, same reason.
And `to: "."`, `to: "briefs/.."` and `to: "   "` never reach rule 19 because rule 14 refuses them one step
earlier as outside the repository, which is the better sentence; left alone deliberately.

## Amendment 7 — I theorised the mechanism twice; here is the measurement (2026-09-19)

Unit E reported `spec-problem` and it is right. Change 1 of Amendment 6 (narrowing the device rule to `NUL`)
is built and correct. **Change 2's rule is wrong in both directions**, and the implementer implemented it as
frozen, measured what it actually did, and said so rather than quietly fixing my sentence.

### It does not close the shape it names

Amendment 6 gave the mechanism as *"Win32 trims trailing spaces and dots from a path component; a component
made only of them trims to nothing"*. True of `...` and `   `. **Not true of `docs `**, which trims to `docs`,
not to nothing — so the frozen predicate cannot match the very shape the amendment lists first, and
`docs /x.md` still crashes and still leaves an entry behind.

The real mechanism, measured against `Directory.CreateDirectory` and `File.WriteAllText` as `WriteFile` calls
them, over 26 shapes:

```
docs /x.md      CRASHES   leaves repo\docs
docs /y/x.md    INSTALLS  3 entries   <-- "docs " is fine as an INTERMEDIATE component
a /b /x.md      CRASHES   leaves a , a \b
docs.../x.md    CRASHES   GetFullPath keeps "docs..."
docs./x.md      INSTALLS  GetFullPath normalises a single trailing dot away
```

`Directory.CreateDirectory` trims trailing spaces off **the last component it is given**; `File.WriteAllText`
does not trim interior ones. So `WriteFile` creates `repo\docs` and then writes into `repo\docs `, which does
not exist. The class is therefore **the file's immediate parent component**, not any component — which is why
`docs /y/x.md` installs and must keep installing.

### And read literally it refuses manifests that work

`TrimEnd(' ', '.')` of `""`, `"."` and `".."` is empty, so the frozen predicate refuses five shapes that
install today, two of them asserted by green tests:

```
docs//x.md      ./docs/x.md      docs/./x.md      a/../docs/x.md      docs/../x.md
```

`Path.GetFullPath` *resolves* those away rather than trimming them to nothing, and the rule's own sentence —
*"is only spaces and dots"* — is false of an empty component and false of what the platform does to `.` and
`..`. The implementer excluded exactly those three spellings and nothing else.

**That exclusion is ratified.** It is the same over-refusal Amendment 6 was written to correct, one rule over,
and I wrote it two paragraphs after correcting the first one.

### The rules, stated from the measurement

**Keep the existing rule, with the exclusions**, for a component that is *only* spaces and dots (`...`,
`   `), excluding `""`, `"."` and `".."`. It is what repairs the `to: "   "` sentence, and Unit E verified
that.

**Add a second rule** for the file's immediate parent. After resolving the destination, take the final
component of its parent directory; on Windows, refuse when it ends with a space or a dot:

```
"<manifestPath>: entry <n> writes \"<to>\", whose parent directory \"<component>\" ends in a space or a dot, which this platform cannot create."
```

Checked **after** `Path.GetFullPath`, which is what makes `docs./x.md` install (the dot is already gone) and
`docs.../x.md` refuse (the dots are kept). Only the immediate parent, which is what keeps `docs /y/x.md`
installing.

Both rules stay gated on `OperatingSystem.IsWindows()`. Unit E gated the first without being told to and was
right: the trimming is a Win32 behaviour, `"   "` is a legal directory name on Linux, and both messages say
*"this platform"*. Refusing it elsewhere would refuse a manifest that installs — the sin Change 1 corrects.

### Two corrections to Amendment 6's own prose

- It said the residual crashes are "on 3 — all repository-decided". As *families* that reads correctly; as a
  row count `attack2.ps1` shows **6** (F1×2, F2×2, F3×1, plus `docs /x.md`). Row count is what a reader will
  check, so: six rows, three repository-decided families, plus the one shape this amendment closes.
- The guard measurement is now **nothing in 111 manifests**, a third independent sample after 70 and 136.

### A methodological finding worth more than the rule

Unit E noticed that **`regress.ps1` could not have answered the guard question at all**: it truncates its
message column at 110 characters, every message opens with the full manifest path, and the guard's sentence
falls past the cut. Its table would have looked identical whether the guard fired or not. The implementer
replayed all 23 cases through a checker that greps raw stderr before reporting the measurement.

That is the same discipline as this spec's earlier note about a table where everything fails — a harness that
*cannot show* the thing being measured is worse than one that fails loudly, because it reads as evidence.

## Proof of Amendment 7, and the end of this task (2026-09-19)

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test
  Muthur.Launch.Tests    23/23
  Muthur.Core.Tests      3736/3736
  Muthur.Cli.Tests       159/159    (65 before T-8)
  Muthur.Server.Tests    349/349
```

Amendment 7's measurement table, re-run by the orchestrator against an installed AOT CLI from this branch:

```
FIXTURE good.md exists: True
docs /x.md     exit=2 clean  repo=0
a /b /x.md     exit=2 clean  repo=0
docs.../x.md   exit=2 clean  repo=0
docs /y/x.md   exit=0 clean  repo=5      <- an intermediate component may end in a space
docs./x.md     exit=0 clean  repo=4      <- GetFullPath already removed the single dot
OUTSIDE entries: 0
```

Nothing narrowed, nothing deleted: no passing manifest broke.

The implementer additionally re-ran every sweep from every round — `six`, `shapes`, `regress`, `attack`,
`attack2`, `kits` — with `attack2` now showing **5** crashes rather than 6, all five repository-decided and
filed as T-56.

### The guard, four times

| Unit | Manifests | Fires |
|---|---|---|
| C | 70 | 0 |
| D | 136 | 0 |
| E | 111 | 0 |
| F | 127 | 0 |

**444 manifests, zero fires.** Unit F went further than reporting the number: it put a logging shim in front
of `muthur.exe` and replayed all seven scripts through it, so the measurement is raw untruncated stderr rather
than a table that might not have been able to show a fire — the trap Unit E found in `regress.ps1`.

Amendment 5 concluded the guard was "a floor nobody has stood on". Four samples later the stronger statement
is warranted: **nothing in scope is capable of making it fire**, because the guard is around the reader and
every remaining crash is in the write phase. It stays, because its absence is what let the surrogate family
escape for two rounds and a future reader-phase shape would otherwise crash. But it was never the answer to
this task, and Amendment 4 added it on reasoning that the evidence has since overturned.

### What T-8 delivers

> Every manifest shape anyone has found — across five rounds, two validators and roughly 450 hostile
> manifests — is refused before a byte is written, with `invalid_manifest`, exit 2 and a sentence naming the
> file, the entry and the field.

What it does not deliver, all filed: **T-56** (five crashes decided by repository state, one of which needs no
manifest at all), **T-57** (containment does not survive a junction), **T-55** (the write phase is not
transactional).

### What this task cost, and what that is worth recording for

Five implementation rounds and two validator failures for what was filed as "a malformed kit.json crashes".
The rounds were not waste — each found real defects, and two of them found defects in *my own spec*: a rule
that could not match the shape it named, and a predicate that would have refused five working manifests.
Both were caught because an implementer measured before building and reported a `spec-problem` instead of
quietly making the tests pass.

The pattern worth carrying: **the orchestrator theorised a mechanism three times and was wrong twice.**
`NUL` versus the whole device list, and `docs ` trimming "to nothing". Both were settled in minutes by someone
calling `Directory.CreateDirectory` and looking. A spec that states a mechanism it has not measured is a spec
that will cost a round.
