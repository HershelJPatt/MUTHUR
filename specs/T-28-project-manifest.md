# T-28 — MUTHUR's own `muthur.project.json` is not valid JSON

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the repository's own project manifest parses, the tooling that depends on it works again,
and a test fails the build if the manifest ever stops parsing. The specific regression — a Windows path in
a JSON string silently invalidating the whole file — cannot come back unnoticed.

## Context

`muthur.project.json` line 6, the `notes` field, contains the literal `%LOCALAPPDATA%\Muthur`. `\M` is not
a valid JSON escape, so the **whole file** fails to parse. Confirmed:

```
Invalid \escape: line 6 column 71 (char 337)
  ...development build at the real %LOCALAPPDATA%\Muthur home: s...
```

Both readers swallow the failure and behave as if the file were absent:

- `src/Muthur.Cli/Infrastructure/ProjectContext.cs` — `FindKey()` catches `JsonException` and returns
  `null`. It is the default for `--project` in `task add`, `task list`, `project show` and `inbound push`.
- `src/Muthur.Cli/Commands/WorkerCommands.cs:214` — `ReadProject` catches `JsonException` and returns
  empty lists, so **`muthur worker run` has never found this project's build and test commands**. Every
  worker this organization has launched was told to verify its unit with no commands to run.

That second consequence is why this is being fixed now: T-15 exists to judge whether `muthur worker run`
holds up, and a verdict reached with the manifest silently unread would be a verdict about the wrong thing.

Meanwhile `kit/generic/MUTHUR.md`, `kit/codex/AGENTS.section.md` and `kit/core/orchestrate.md` all tell
agents that build/test/run commands live in this file — a file the tooling could not read.

Checked: the only `muthur.project.json` in the organization is this repository's. The ten under
`.claude/worktrees/` are copies of it in agent worktrees and need no separate fix.

### Files and patterns

- `muthur.project.json` — the manifest.
- `src/Muthur.Cli/Infrastructure/ProjectContext.cs` — `FindFile(string? startDirectory = null)` already
  takes an optional start directory; `FindKey()` does not.
- `tests/Muthur.Cli.Tests/KitWriteModeTests.cs` — the style to match (sealed class, XML doc saying why the
  tests matter, `Assert` on real files).

Codebase rules that apply: warnings are errors; file-scoped namespaces; AOT-safe (no reflection-based JSON).

## Non-goals

- **Do not change how a malformed manifest is reported.** Making it loud is a real improvement and it is
  deliberately not in this task — see *Out of scope*. `FindKey` must keep returning `null` rather than
  throwing, because it is the silent default behind `--project` in four commands and a throw would turn a
  bad manifest into a failure of every one of them.
- Do not touch the `.claude/worktrees/` copies; they are transient.
- Do not reword `notes` to dodge the backslash. The note is about a Windows path and should keep saying so.
- Do not add manifest validation to `muthur doctor`; T-14 owns doctor and already found this.

## Design

### 1. The manifest parses

In `muthur.project.json`, escape the backslash in the `notes` field: `%LOCALAPPDATA%\\Muthur`. The rendered
string is then exactly `%LOCALAPPDATA%\Muthur`, which is what the note means to say. Nothing else in the
file changes — `key`, `build`, `test` and `run` keep their current values byte for byte.

### 2. `FindKey` can be pointed at a directory

In `src/Muthur.Cli/Infrastructure/ProjectContext.cs`, give `FindKey` the same optional parameter `FindFile`
already has, and pass it through:

```csharp
public static string? FindKey(string? startDirectory = null)
{
    if (FindFile(startDirectory) is not { } file) return null;
    ...
}
```

Existing callers pass nothing and are unaffected. This exists so a test can exercise the real read path
against the real manifest without mutating `Environment.CurrentDirectory`, which is process-global and
unsafe while tests run in parallel.

### 3. A test that fails the build if the manifest stops parsing

New file `tests/Muthur.Cli.Tests/ProjectManifestTests.cs`, in the style of `KitWriteModeTests.cs`, with a
class-level XML doc saying why it exists: this manifest silently did nothing for two days, and the tooling
that depends on it reported no error.

Locate the manifest from the test binary, never from the current directory:
`ProjectContext.FindFile(AppContext.BaseDirectory)`.

Three facts, as separate tests:

1. `The_repositorys_own_manifest_is_valid_json` — the file is found, and `JsonDocument.Parse` of its text
   succeeds. If this fails, the assertion message must name the file path.
2. `The_repositorys_own_manifest_names_its_key_and_its_commands` — parsed, it has `key` equal to `muthur`,
   and non-empty `build` and `test` strings. These are the three fields `worker run` and the CLI actually
   read; a file that parses but has lost them is the same outage.
3. `The_key_is_readable_through_the_path_that_regressed` —
   `ProjectContext.FindKey(AppContext.BaseDirectory)` returns `muthur`. This is the end-to-end guard: it
   is the exact call that returned `null` for two days, and test 1 alone would not catch a future failure
   in the reader rather than in the file.

If the manifest cannot be found from `AppContext.BaseDirectory`, the tests must fail with a message saying
so rather than silently passing on a `null`.

## Units of work

### Unit A — the whole change
- **Files:** modified `muthur.project.json`, `src/Muthur.Cli/Infrastructure/ProjectContext.cs`;
  created `tests/Muthur.Cli.Tests/ProjectManifestTests.cs`
- **Does:** everything under **Design**, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` — clean, zero warnings.
  - `dotnet test` — green, with `Muthur.Cli.Tests` up from 9 tests to 12.
  - A JSON parser that is not the code under test agrees the file is valid, e.g.
    `python -c "import json,io; json.load(io.open('muthur.project.json',encoding='utf-8'))"` exits 0.
  - **Reverting only the `muthur.project.json` change makes all three tests fail.** Check this and report
    it — a regression test that does not fail on the regression is worth nothing. Restore the fix
    afterwards.

    (This line first said "tests 1 and 3". The implementer reported that test 2 fails too, because it must
    parse the file before it can read any field. That is unavoidable and correct; the spec was wrong.)

One unit: three small edits that share one reason, and splitting them would serialize on the same test file.

## Verification

```
dotnet build
dotnet test
```

Plus, end to end, the behaviour this restores — run from the repository root with an installed CLI and a
scratch `MUTHUR_HOME`/`MUTHUR_URL`, never against the live hub:

```
muthur task list            # infers --project muthur from the manifest instead of falling back
```

A validator should also confirm from the diff that `notes` still renders as `%LOCALAPPDATA%\Muthur` (one
backslash) rather than having been reworded, and that no caller of `FindKey()` was changed.

## Out of scope / follow-ups

- **A malformed manifest is still silent.** Both readers catch `JsonException` and degrade. This task fixes
  the file and guards this repository; it does not make the next typo loud. That is the same defect T-8
  raises for `kit.json` — "a malformed manifest fails like a rule violation, not an AOT crash" — and the
  two should be settled together, with one stable `invalid_manifest` code and exit 2, rather than
  separately. Add it to T-8 rather than opening a third task.
- `muthur doctor` checking every configured project's manifest belongs to T-14/T-27, not here.

## Proof (run 2026-09-18, on the integrated task branch)

End to end through the installed CLI, from the repository root, which is what the task is actually about —
`muthur project show` with no key resolves the project only if the manifest can be read:

```
with the fix:        {"key":"muthur","name":"muthur","repoPath":"C:\WorkSrc\MUTHUR",...}
manifest reverted:   {"code":"project_required","message":"Pass a project key, or run inside a
                      repository with muthur.project.json."}
```

That second line is the two-day outage, reproduced on demand and then fixed again.

The implementer's own deliberate-revert experiment agreed from the other side: with only the manifest
reverted, `ProjectManifestTests` went 0 passed / 3 failed, test 3 reporting
`FindKey read '(null)' from ...\muthur.project.json, which exists.`

An accepted deviation: test 3 is `Assert.True(key == "muthur", "<message>")` rather than the one-line
`Assert.Equal` this spec sketched. A bare `Assert.Equal` fails with only "Expected: muthur, Actual:
(null)", which cannot distinguish a broken reader from a missing file — and this spec separately requires
that a manifest which cannot be found fails with a message saying so. The implementer's shape satisfies
both; the sketch did not.

Known, out of scope, and worth a validator knowing: these tests walk up from the test binary, so run inside
an agent worktree they assert against that worktree's copy of the manifest. The nine copies under
`.claude/worktrees/` still hold the broken string and would fail there. They are transient and this spec
excludes them.
