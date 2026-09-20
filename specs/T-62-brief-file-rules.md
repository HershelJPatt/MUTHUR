# T-62 — Defining a role from a file has its rules in two places

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

The two rules that govern a file the founder names by path — **it must stay inside the repository**, and
**it must match a commit** — exist in exactly one place, `Muthur.Launch.RepoFile`, and every client of
`role define` obeys both. Today the CLI enforces the dirty rule and the console does not, so the console is
the laxer of the two paths to the same operation; after this task it is not.

The file *reading* stays where it is. The CLI resolves against the caller's working directory; the console
resolves against the repositories the hub already knows. Those are genuinely different questions and this
task does not merge them. What is unified is the pair of rules, which is the part that rots when one client
changes and the other does not.

## Context

### What is true today, which is not quite what T-62's ledger body says

The body's table claims the console gained the `brief_file_dirty` refusal in T-24 amendment 2. **It did
not.** `specs/T-24-founder-console.md` amendment 4 §1 records the correction: the refusal was ordered,
never built, and found by Unit C while reading the code it was extending. So the honest table is:

| Rule | CLI (`RoleCommands.cs:26-36`) | Console (`Console.razor:536-562`) |
|---|---|---|
| resolve a repo-relative path | yes, against the caller's cwd | yes, against each project's `RepoPath` in key order |
| refuse a path that escapes the repository | implicit (the caller's own cwd) | explicit `IsInside` guard |
| refuse a file with uncommitted changes | yes (`brief_file_dirty`) | **no** |

This is not duplication about to diverge. It has already diverged.

### Two of the three objections in the ledger body are already answered by the code

- *"It puts a git invocation in a request path that currently has none."* `DoctorRepoCheck`
  (`src/Muthur.Server/Services/DoctorRepoCheck.cs`) already shells out to git, through `IProcessRunner`,
  on `GET /doctor`. The hub does this today and has a fake-able seam for it.
- *"It gives the hub a reason to read arbitrary files on the founder's disk."* This spec does **not** do
  that — see Non-goals. The console keeps reading exactly what it reads today: files under a configured
  project `RepoPath`, and nothing else.

The third — *"a containment rule you can pass in is one you can pass wrong"* — is why the containment
**root** is never a parameter of a public hub operation here. It stays what the caller already had: the
project's `RepoPath` in the console, the caller's cwd in the CLI.

### Why the shared code goes in `Muthur.Launch`

`Muthur.Launch` is the only assembly both `Muthur.Cli` and `Muthur.Server` reference (each also references
`Muthur.Contracts`, which holds DTOs and no logic). It already owns `IProcessRunner`/`ProcessRunner` and is
`IsAotCompatible`, which the CLI requires. `Muthur.Core` is not an option: `Muthur.Cli` does not reference
it and must not start.

T-24 amendment 4 §1 named the two reasons `FileProvenance` could not simply be lifted into the server:
it lives in `src/Muthur.Cli/Infrastructure/`, and it shells out to git **directly** rather than through
`IProcessRunner`. This task fixes both — the logic moves down to `Muthur.Launch` and takes an
`IProcessRunner`.

### Files that matter

- `src/Muthur.Cli/Infrastructure/FileProvenance.cs` — the rules as they exist, in the wrong assembly.
  Read it first; its body is what moves, nearly unchanged.
- `src/Muthur.Cli/Commands/RoleCommands.cs:26-36` — the CLI's use. Unchanged by this task.
- `src/Muthur.Cli/Commands/TaskCommands.cs:17` — the other caller of `FileProvenance.Describe`. Unchanged.
- `src/Muthur.Server/Components/Pages/Console.razor:511-562` — `DefineRoleAsync`, `ReadBriefAsync`,
  `IsInside`. This is what changes.
- `src/Muthur.Server/Services/TaskService.cs:294-377` — a byte-identical second copy of `IsInside`.
- `src/Muthur.Server/Services/DoctorRepoCheck.cs` — the pattern to follow for git through `IProcessRunner`
  in the server.
- `tests/Muthur.Cli.Tests/FileProvenanceTests.cs` — eight tests against a real temporary repository. They
  must keep passing **unchanged**; that is the main evidence the extraction preserved behaviour.

### Conventions that are not obvious

- Warnings are errors. File-scoped namespaces, primary constructors.
- Services throw `MuthurException` via `Fail.*` with a stable `code`; they never return error tuples or
  null-with-a-field-set.
- Anything talking to the outside sits behind an interface so it can be faked — hence `IProcessRunner`.
- Tests against git run against a **real** temporary repository, not a faked runner.
  `FileProvenanceTests` states the reason and `TestRepo` is the server-side equivalent.

## Non-goals

- **No `RoleService.DefineFromFileAsync`, and no path parameter on any hub operation.** The hub's
  file-reading surface is exactly what it is today. `RoleService.DefineAsync` keeps taking brief *text*.
- **No new HTTP route, no contract change, no DTO change, no migration.**
- **No provenance line in the console.** The CLI prints `Read <file> at <ref> (<commit>).` to stderr after
  a successful read. The console showing the same is a reasonable thing to want and is *not* part of this
  task: mirroring the CLI's **safety rules** is the requirement; mirroring its informational output is not.
  Filed as a follow-up.
- **`KitCommands.IsInside` (`src/Muthur.Cli/Commands/KitCommands.cs:586`) is not touched.** It is a third
  containment implementation with deliberately different semantics — always `OrdinalIgnoreCase`, no
  `GetFullPath` on the root, operating on manifest text rather than a founder-typed path. Folding it in
  would change kit behaviour on Linux. Filed as a follow-up.
- **No change to the console's markup.** No new inputs, labels, buttons or panels. Every existing assertion
  in `DashboardConsoleRolesTests` must still pass untouched.
- **No change to `DefineRoleRequest` or to what the console sends the service.**

## Design

### 1. `Muthur.Launch.RepoFile` — the two rules, in one place

New file `src/Muthur.Launch/RepoFile.cs`, namespace `Muthur.Launch`:

```csharp
public static class RepoFile
{
    public static bool IsInside(string root, string full);

    public static Task<bool> IsDirtyAsync(IProcessRunner processes, string path, CancellationToken ct = default);

    public static Task<(string Ref, string Commit)?> DescribeAsync(IProcessRunner processes, string path, CancellationToken ct = default);
}
```

**`IsInside(root, full)`** — the containment rule. Behaviour is `TaskService.IsInside`'s, with the root
full-pathed inside the method (which is what `Console.razor`'s copy does, and is a no-op for
`TaskService`'s already-full root):

- full-path the root, then trim trailing `Path.DirectorySeparatorChar` and `Path.AltDirectorySeparatorChar`;
- comparison is `OrdinalIgnoreCase` on Windows, `Ordinal` elsewhere (`OperatingSystem.IsWindows()`);
- true only when `full.Length > trimmed.Length`, `full` starts with `trimmed`, and the very next character
  of `full` is a directory separator. The separator test is what keeps `/repo-evil` from counting as inside
  `/repo`;
- if full-pathing the root throws `ArgumentException`, `PathTooLongException`, `NotSupportedException`,
  `IOException` or `UnauthorizedAccessException`, **return false**. A root this machine will not accept
  contains nothing, and false is the refusing answer.

**`IsDirtyAsync`** — `FileProvenance.IsDirty`'s body, verbatim in behaviour, now async and through
`processes`. True when git cannot name a commit containing this file: modified, staged, or never added.
Keep every part of it and the comments explaining why:

- full-path the file first; if that fails, **false** (nothing to be dirty against);
- `git rev-parse --is-inside-work-tree` must be exactly `true`, else **false** — outside a repository there
  is no commit to disagree with, and provenance never gates a role;
- then `--literal-pathspecs status --porcelain --untracked-files=no -- <filename>` non-empty,
  **or** `--literal-pathspecs ls-files --error-unmatch -- <filename>` failing. Both probes are needed:
  `status` misses a file that was never added, `--untracked-files=no` is what stops `bin/` and `obj/`
  failing every run, and `--literal-pathspecs` is what makes a name containing `*`, `[` or `?` a name.

**`DescribeAsync`** — `FileProvenance.Describe`'s body: `rev-parse --abbrev-ref HEAD` and
`rev-parse --short HEAD`, both non-empty, else null.

**The git helper** runs in the file's own directory with a 10-second budget (the existing
`FileProvenance.Budget`), and returns null on any failure — no git, no repository, an unreadable path,
a non-zero exit, or any exception out of `RunAsync`. Keep `FileProvenance`'s comment explaining that for
`Describe` this reads as "not in a repository", because evidence that cannot be gathered stops nothing.

The 10-second budget is a `private static readonly TimeSpan` on `RepoFile`. It is not a parameter: a
timeout a caller can pass is a rule a caller can weaken.

Pass `ct` through to `RunAsync`. A cancelled token propagates (`ProcessRunner` rethrows on `ct`); it is not
swallowed into "not dirty".

### 2. `Muthur.Cli.Infrastructure.FileProvenance` — a sync adapter over `RepoFile`

Its public surface does not change: `Describe(string)` and `IsDirty(string)`, both synchronous, both with
the same signatures and the same answers. The bodies become delegation:

```csharp
public static class FileProvenance
{
    private static readonly IProcessRunner Processes = new ProcessRunner();

    public static (string Ref, string Commit)? Describe(string path) =>
        RepoFile.DescribeAsync(Processes, path).GetAwaiter().GetResult();

    public static bool IsDirty(string path) =>
        RepoFile.IsDirtyAsync(Processes, path).GetAwaiter().GetResult();
}
```

`.GetAwaiter().GetResult()` is what the file already does; the CLI is synchronous at these call sites and
this task does not change that. Keep the type's `<summary>`. `RoleCommands.cs` and `TaskCommands.cs` are
**not edited**.

### 3. `Muthur.Server.Services.TaskService` — one copy fewer

Delete the private `IsInside` at `TaskService.cs:370-377` and call `RepoFile.IsInside(root, full)` at
line 304. Add `using Muthur.Launch;` if it is not already there. Move the sentence *"A separator at the
boundary is what keeps '/repo-evil' from counting as inside '/repo'"* onto `RepoFile.IsInside`'s doc
comment — it is the reason the rule is shaped this way and it must not be lost. Nothing else in
`TaskService` changes; `SpecGuardTests` must pass untouched.

### 4. `Muthur.Server.Services.BriefFileReader` — the console's client of those rules

New file `src/Muthur.Server/Services/BriefFileReader.cs`:

```csharp
/// <summary>
/// A role brief the founder named by path, read under the rules every client of `role define` obeys.
/// The console is not a laxer second door to that operation, so both rules are here: the path stays
/// inside a repository the hub was configured with, and the file matches a commit.
/// </summary>
public sealed class BriefFileReader(IProcessRunner processes)
{
    public async Task<string> ReadAsync(IReadOnlyList<string> repositories, string path, CancellationToken ct = default)
}
```

It throws `MuthurException` rather than returning null — the codebase rule, and it lets the console's
existing `catch (MuthurException ex) { _roleError = ex.Message; }` carry every refusal in one place.

Steps, **in this order**:

1. `path.Trim()` is empty →
   `Fail.Rule("brief_file_required", "A role needs a brief: the path of its markdown file, from the repository root.")`
   Call the trimmed value `relative` from here on; every message below names `relative`, never the
   absolute path (the founder typed the relative one, and absolute paths are not this page's business).
2. `repositories` is empty →
   `Fail.Rule("no_repository", "No project exists yet, so there is no repository to read a brief from. muthur project add <key> --repo <path> --founder")`
3. Walk `repositories` in the order given. For each `root`, compute
   `full = Path.GetFullPath(Path.Combine(root, relative))`; if that throws `ArgumentException`,
   `PathTooLongException`, `NotSupportedException`, `IOException` or `UnauthorizedAccessException`, treat
   this root as no match and continue. The first root where `RepoFile.IsInside(root, full)` **and**
   `File.Exists(full)` is the match, and **the search stops there**.
4. No root matched →
   `Fail.Rule("brief_file_missing", $"No file at '{relative}' in {string.Join(" or ", repositories)}. The path is read from the repository root and stays inside it.")`
5. `await RepoFile.IsDirtyAsync(processes, full, ct)` on the matched file →
   `Fail.Rule("brief_file_dirty", $"'{relative}' has no committed version, so the brief you install would match no commit. Commit it, or pass the text you mean to 'muthur role define'.")`
6. Otherwise `return await File.ReadAllTextAsync(full, ct);`

**Step 3's "the search stops there" is a rule, not an implementation detail, and it needs a comment
saying so.** A dirty file in the first repository is refused; it never falls through to a clean file of the
same name in the second. Falling through would mean a founder with two projects sometimes installs a brief
from a repository they were not looking at, chosen by which copy happened to be committed — and it would
make the refusal skippable by leaving a stale copy elsewhere.

Steps 1, 2 and 4 keep the exact wording the console uses today, so this is not a rewording of anything the
founder has already read; only their delivery changes (an exception instead of a field) and step 5 is new.

Register in `src/Muthur.Server/Infrastructure/Startup.cs` as
`builder.Services.AddSingleton<BriefFileReader>();`, placed immediately after
`builder.Services.AddSingleton<RoleService>();` (line 59) — it is the role control's collaborator and the
list reads in service order. `IProcessRunner` is already registered at line 74; ordering of registrations
does not matter to the container.

### 5. `Console.razor`

Add `@inject BriefFileReader Briefs` after `@inject RoleService Roles`.

`DefineRoleAsync` becomes:

```csharp
private async Task DefineRoleAsync()
{
    ClearOnce();
    _roleError = null;
    _defined = null;
    try
    {
        var brief = await Briefs.ReadAsync(
            [.. _projects.Select(p => p.RepoPath).Distinct(StringComparer.Ordinal)], _roleBrief);
        _defined = await Roles.DefineAsync(Caller.Founder, new DefineRoleRequest(_roleKey, brief, _roleIsValidator));
        // The ruling was about the role just defined. The next key typed into this field is a different
        // entry and infers again, or the founder's one refusal would quietly govern every role after it.
        _roleValidatorTouched = false;
    }
    catch (Muthur.Core.MuthurException ex)
    {
        _roleError = ex.Message;
    }
}
```

A refused brief must still leave `_defined` null and `_roleValidatorTouched` untouched — the throw skips
both, which is the behaviour the old early `return` had.

Delete `ReadBriefAsync` and the private `IsInside` (`Console.razor:530-562`) entirely. The reason the
containment rule exists here is not deleted with them: it moves, in one sentence, onto `BriefFileReader`'s
doc comment — *this control reads the founder's disk from a browser tab, and a path free to climb out of a
checkout with `..` would let that tab read anything on the machine.*

### Error behaviour, complete

| Situation | Code | Kind |
|---|---|---|
| empty brief path | `brief_file_required` | RuleViolation |
| no project configured | `no_repository` | RuleViolation |
| no such file in any repository, or a path that escapes one | `brief_file_missing` | RuleViolation |
| file found but uncommitted, staged-only, or never added | `brief_file_dirty` | RuleViolation |

A path that escapes the repository deliberately answers `brief_file_missing` and not a distinct
"outside" code: the console is a browser tab, and `../../../etc/passwd` returning "outside the repository"
while a typo returns "no such file" tells whoever is typing which absolute paths exist. One answer for
both, and the message already says *"stays inside it"* so a founder who typed a real `..` by accident is
told the rule.

None of these reach HTTP — `BriefFileReader` is called only from the Blazor page — so no status-code
mapping is involved.

## Units of work

### Unit A — the rules move to `Muthur.Launch`

- **Files:**
  - create `src/Muthur.Launch/RepoFile.cs`
  - rewrite `src/Muthur.Cli/Infrastructure/FileProvenance.cs` as the sync adapter
  - edit `src/Muthur.Server/Services/TaskService.cs` (call `RepoFile.IsInside`, delete the private copy)
  - create `tests/Muthur.Launch.Tests/RepoFileTests.cs`
- **Does:** Design §1, §2, §3. No behaviour changes anywhere — this is the extraction, and the evidence it
  is faithful is that `FileProvenanceTests` and `SpecGuardTests` pass with no edits.
- **Depends on:** nothing
- **Acceptance:**
  - `dotnet build` clean (warnings are errors)
  - `dotnet test` green, with `tests/Muthur.Cli.Tests/FileProvenanceTests.cs` and
    `tests/Muthur.Server.Tests/SpecGuardTests.cs` **not modified** — `git diff --stat main` must not list
    either file
  - `RepoFileTests` covers, against a real temporary repository built the way `FileProvenanceTests` builds
    one (a `Dispose` that clears the read-only bit git sets on objects, and the same four `git config`
    lines):
    - `IsDirtyAsync` false for a committed file, true once edited, true when staged-but-never-committed,
      true when never added, false for a file outside any repository
    - `IsDirtyAsync` false for a clean file whose *sibling* in the same tree is dirty — only the named file
      counts
    - `DescribeAsync` names the branch and short commit for a committed file, null outside a repository
    - `IsInside`: true for a file one level down and several levels down; false for the root itself; false
      for `<root>-evil/x` (the prefix-but-not-inside case); false for a `..` path that climbs out; true
      with a trailing separator on the root; on Windows, true for a root differing only in case
    - `IsInside` returns false rather than throwing for a root the platform rejects (a string containing
      `\0` is the portable way to get one; if that does not throw on this platform, use whatever does and
      say so in the report)
  - the new tests use `new ProcessRunner()`, not a fake: what is being tested is what git actually says

### Unit B — the console obeys both rules

- **Files:**
  - create `src/Muthur.Server/Services/BriefFileReader.cs`
  - edit `src/Muthur.Server/Infrastructure/Startup.cs` (one registration line)
  - edit `src/Muthur.Server/Components/Pages/Console.razor` (inject, rewrite `DefineRoleAsync`, delete
    `ReadBriefAsync` and `IsInside`)
  - create `tests/Muthur.Server.Tests/BriefFileReaderTests.cs`
- **Does:** Design §4, §5.
- **Depends on:** Unit A (`RepoFile` must exist). Build on top of Unit A's branch.
- **Acceptance:**
  - `dotnet build` clean
  - `dotnet test` green, with `tests/Muthur.Server.Tests/DashboardConsoleRolesTests.cs` **not modified** —
    the console's markup and its inferred-checkbox rule are unchanged, and that file passing untouched is
    the proof
  - `BriefFileReaderTests` resolves the service out of `HubFactory` (`_hub.Services.GetRequiredService<
    BriefFileReader>()`, the way `DashboardConsoleRolesTests` gets `RoleService`) and uses `TestRepo`, and
    covers one case per row of the error table plus the two that matter most:
    - a committed brief is read and its text returned
    - **the divergence this task exists to close:** an edited-but-uncommitted brief throws
      `brief_file_dirty`, and the code matches the CLI's — assert the literal string `"brief_file_dirty"`,
      and add a line of comment naming `RoleCommands.cs` as the other place it is spelled
    - a brief that was never added to the repository throws `brief_file_dirty`
    - **first match wins, and a dirty first match does not fall through:** two `TestRepo`s, the same
      relative path in both, dirty in the first and committed in the second → `brief_file_dirty`, not the
      second repository's text
    - `../` out of the repository throws `brief_file_missing`, and so does a sibling directory named
      `<repo>-evil` that shares the root's prefix (create it, put a file in it, assert the refusal, delete
      it in a `finally` — `SpecGuardTests.A_path_that_escapes_the_repository_is_refused` is the model)
    - empty and whitespace-only paths throw `brief_file_required`
    - no projects configured → `no_repository`
    - a path to no file → `brief_file_missing`, and the message names the relative path the founder typed
  - a test asserting the console page still renders with the new injection:
    `GET /console` returns 200 and does not contain `"An unhandled error"` (an unregistered injected
    service is a runtime failure a compile cannot catch). Put it in `BriefFileReaderTests` rather than
    editing `DashboardConsoleRolesTests`.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then, the end-to-end behaviour a validator should exercise — the point of the task is that
these two now answer the same way:

```
# a scratch hub, never the live one
$env:MUTHUR_HOME = "<scratch>"; $env:MUTHUR_URL = "http://127.0.0.1:<port>"
pwsh ./scripts/install.ps1 -Destination ./artifacts/t62

# a repository with one committed brief and one dirty brief
muthur project add scratch --repo <repo> --founder

# 1. the CLI refuses a dirty brief, as it always has: exit 2, code brief_file_dirty
muthur role define t62-validator --brief-file <repo>/briefs/dirty.md --founder

# 2. the CLI accepts a committed one: exit 0
muthur role define t62-validator --brief-file <repo>/briefs/clean.md --founder

# 3. the hub still stores only text, and has no route that takes a path
muthur role brief t62-validator --raw
```

The console half of this is the same two files through the Roles control on `/console`: `briefs/dirty.md`
refused with *"has no committed version"* where it previously installed silently, `briefs/clean.md`
accepted. That is a browser click and cannot be automated here — but **it is not the evidence this task
turns on**. The rule it exercises is `BriefFileReader`, which `BriefFileReaderTests` drives directly out of
the real container, including the dirty refusal, the escape refusal and the first-match-wins rule. The page
itself is unchanged markup, proven by `DashboardConsoleRolesTests` passing unedited.

So this task is **not** attended. Do not add a `needs:` line.

## Out of scope / follow-ups

Each becomes its own ledger task:

1. **The console does not show a brief's provenance.** The CLI prints `Read <file> at <ref> (<commit>).`
   after a successful read, so the founder knows which commit they installed. `RepoFile.DescribeAsync`
   now exists on the server side and would make this a small change to `DefineRoleAsync` plus a
   `section-note`. Left out here to keep this task to the safety rules.
2. **`KitCommands.IsInside` is a third containment implementation with weaker semantics** — always
   `OrdinalIgnoreCase` (so on Linux it is laxer than the other two) and no `GetFullPath` on the root (so a
   relative or unnormalised root silently answers false). Folding it into `RepoFile.IsInside` is right, and
   it changes kit behaviour on Linux, which deserves its own task and its own tests.
3. **The ledger body of T-62 is wrong about the console having the dirty check.** Not a code task; noted
   here so the next reader of that body finds the correction. `specs/T-24-founder-console.md` amendment 4
   §1 is the original record.
