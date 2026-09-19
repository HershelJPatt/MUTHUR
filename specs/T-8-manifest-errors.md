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
