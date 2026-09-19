# T-61 — A typo in an agent's harness registers fine and fails much later

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, `muthur doctor` answers the question *"does every agent in this organization run on a harness
that actually exists here?"*. An agent registered as `cladue` is named in the report, with its harness, with
what the organization does have, and with a status that depends on whether that agent is still live. The hub
gains the thing it has never had and that T-24 had to work around: a server-side way to ask **which harnesses
the kit has procedures for**. Registration itself is unchanged — it still accepts any non-empty harness, on
purpose.

## Context

### What is wrong today

`AgentService.RegisterAsync` validates the agent name hard and takes any non-empty string as the harness. So
`muthur agent register --name corner --harness cladue …` succeeds, the agent appears on the board and in the
rail, and the typo surfaces only when a session starts and its kit does not resolve, or when the conductor
tries to staff it.

**This is the right behaviour and it stays.** T-24's Amendment 1 (`specs/T-24-founder-console.md:253-311`)
settled that the console offers a `<datalist>` rather than a closed list *precisely because* the service
accepts anything: `harnesses.json` is the founder's own configuration and a new harness must be usable the
moment they add it, before any code knows its name. Constraining registration would make the console narrower
than the API behind it, which is the defect Amendment 1 removed. T-24 closed with *"Noted, not fixed"*. This
task is the fix, and it is a **doctor check** — `muthur doctor` already answers *"is this organization in a
state that will work"*, and this is exactly that shape of question.

### The two sources, and why they are two facts

The hub has two candidate answers to "what is a real harness here", and they disagree:

| Source | On a fresh hub | What it means |
|---|---|---|
| The tier catalog, `{MUTHUR_HOME}/harnesses.json` | `claude`, `codex`, `codex-oss` | Which harnesses a **tier can be staffed on headlessly**. Read by `HarnessService.ReadCatalog` (`src/Muthur.Server/Services/HarnessService.cs:87-114`), seeded from `HarnessDefaults.CatalogJson`. |
| The kit directory, `kit/<harness>/kit.json` | `claude`, `codex`, `generic` | Which harnesses have **agent procedures to install**. Read only by `KitCommands` in the CLI (`src/Muthur.Cli/Commands/KitCommands.cs:37-45`). |

They are not one fact and must not be merged into one:

- A harness with a **kit but no tier entry** is fine. `generic` is exactly this. It just cannot be staffed
  headlessly by `muthur worker run` or the conductor.
- A harness with a **tier entry but no kit** is a real defect: a tier can be staffed on it and the session
  that starts gets no procedures.
- A harness with **neither** is the typo this task exists for.

### The part with real work in it

T-24 Unit A established that **the kit list is not reachable from the hub**: `kit.json` is read by
`KitCommands` in `Muthur.Cli`, and `Muthur.Server` cannot reference the CLI. That is the same gap T-24 worked
around with a datalist. This task closes it by moving the "where is the kit, and what is in it" knowledge into
`Muthur.Launch`, which both the CLI and the server already reference, and which `CLAUDE.md` names as one of
the two places harness knowledge is allowed to live.

`scripts/install.ps1` is what makes the server-side probe possible and fixes its shape:

```
<destination>/muthur.exe          # the AOT CLI            (install.ps1:121)
<destination>/server/             # Muthur.Server          (install.ps1:124)
<destination>/kit/                # the kit, copied whole  (install.ps1:127-129)
```

So the kit sits **beside** the CLI and **beside the parent** of the server. Both probes are needed.

### Patterns to follow

- `src/Muthur.Server/Services/DoctorRoleCheck.cs` is the closest existing check: one line per subject, an
  `Inspect` method returning `CheckDto`, a local `Check(status, detail)` function, `Format.Age` for "last
  seen", `return []` when there is nothing to judge.
- `src/Muthur.Server/Services/DoctorProjectCheck.cs` shows a check that reads the filesystem and turns a
  parse failure into a `CheckDto` rather than an exception.
- `DoctorService` catches whatever a check throws and reports `"This check itself failed: …"`. Reaching that
  line is a defect in this task, not a feature: **`DoctorAgentCheck` must never throw**.
- `src/Muthur.Cli/Infrastructure/ServerProcess.cs:9-16` is the existing shape of "an explicit setting, else a
  probe beside the binary" — including the rule that an explicit setting pointing nowhere returns null rather
  than falling through to a guess.

### Constraints that are not obvious

- **Warnings are errors.** `CLAUDE.md`.
- `Muthur.Launch` is `IsAotCompatible` and is linked into the Native AOT CLI. Directory enumeration is fine;
  reflection-based JSON is not. Nothing here needs JSON.
- `tests/Muthur.Server.Tests/DoctorTests.cs:14-30` asserts that a hub with nothing configured produces
  **exactly one** check (`logging`). That test must keep passing untouched — see the Design.
- `MUTHUR_KIT` is process-global. `tests/Muthur.Cli.Tests/KitManifestTests.cs:9-16` documents the collection
  that exists to stop two classes swapping it out from under each other. Any new test that touches it obeys
  the same rule.

## Non-goals

- **Do not validate the harness at registration.** Not in `AgentService`, not in the console, not in the CLI.
  The task body and T-24 Amendment 1 both settle this.
- **Do not change the console's datalist**, its suggestion sources, or `Console.razor` at all.
- **Do not check the tier catalog's own harnesses for kits** independently of agents. A catalog entry naming a
  harness with no kit is only reported here when an agent is registered on it. The standalone version of that
  check is a follow-up, filed below.
- **Do not report conductor-staffed sessions.** See the Design for why.
- **No new DTO, route, or CLI command.** `CheckDto` and `GET /doctor` already carry everything.
- Do not touch `MuthurJsonContext`: no type crossing HTTP changes.

## Design

### 1. `src/Muthur.Launch/KitDirectory.cs` — new file, the one answer about the kit

```csharp
namespace Muthur.Launch;

public static class KitDirectory
{
    public const string Variable = "MUTHUR_KIT";
    public const string Manifest = "kit.json";

    public static string? Locate(string? configured = null, string? baseDirectory = null);

    public static IReadOnlyList<string>? Harnesses(string? kitDirectory);
}
```

**`Locate`** returns the kit directory, or null. In order, stopping at the first that applies:

1. `configured` is non-empty → return it if `Directory.Exists(configured)`, otherwise **null**. An explicit
   setting that is wrong is never silently replaced by a guess; `ServerProcess.Locate` decides the same way.
2. `Environment.GetEnvironmentVariable(Variable)` is non-empty → the same rule, and the same null.
3. `Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "kit")` → return it if it exists. *(The CLI's
   layout: `muthur.exe` sits beside `kit/`.)*
4. `kit/` beside the parent of that base directory → return it if it exists. *(The hub's layout: the server is
   published into `server/` and the kit is copied next to it.)* Use
   `Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(base))`; if that is null, skip this step.
5. null.

**`Harnesses`** returns the harnesses that kit has procedures for:

- `kitDirectory` is null or does not exist → **null**.
- Otherwise the names of the immediate subdirectories that contain a `Manifest` file, sorted with
  `StringComparer.Ordinal`.
- An `IOException` or `UnauthorizedAccessException` while enumerating → **null**.

**Null and empty mean different things and callers depend on it:** null is *"could not tell"*; an empty list
is *"told, and there are none"*. Say so in the XML doc comment.

### 2. `src/Muthur.Cli/Commands/KitCommands.cs` — delegate, do not duplicate

- `public const string KitVariable = KitDirectory.Variable;` — the symbol stays; several test classes use it.
- `LocateKit()` becomes `KitDirectory.Locate()`. Its two callers are unchanged.
- The `kit list` action reads `KitDirectory.Harnesses(dir) ?? []` instead of enumerating itself. Comment the
  `?? []`: the directory was there a statement ago, so an empty list is the honest answer to *"which
  harnesses can I install right now"*.
- Nothing else in this file changes. In particular `Install`, `TryReadManifest` and every rule under it are
  not to be touched.

### 3. `src/Muthur.Server/MuthurOptions.cs` — one property

```csharp
/// <summary>
/// Where the agent kit is. Empty means: $MUTHUR_KIT, else kit/ beside the hub, else kit/ beside its parent —
/// which is how install.ps1 lays an install out, with the server in server/ and the kit next to it.
/// </summary>
public string? KitDir { get; set; }
```

Bound from `Muthur:KitDir` like every other option. It exists so a test can point one hub at a scratch kit
without touching the process-global environment variable.

### 4. `src/Muthur.Server/Services/DoctorAgentCheck.cs` — new file

```csharp
public sealed class DoctorAgentCheck(AgentService agents, HarnessService harnesses, MuthurOptions options) : IDoctorCheck
```

All three are singletons already (`Startup.cs:55,64` and the options instance). Register it in `Startup.cs` at
the top of the `IDoctorCheck` block, keeping that list alphabetical.

**Who is judged.** `agents.ListAsync(ct)`, then `.Where(a => !a.ConductorStaffed)`. Conductor-staffed sessions
are excluded because their harness came out of the tier catalog moments earlier — by construction it cannot be
a typo, and a busy hub would bury the standing roster under them. Put that reason in the XML doc comment.

**Nothing to judge, nothing to say.** If no standing agent remains, return `[]` — including the two source
lines below. This is what keeps
`DoctorTests.A_hub_with_nothing_configured_answers_anyone_with_only_what_it_knows_of_itself` passing, and it
matches `DoctorRoleCheck`'s `if (roles.Count == 0) return []`.

**Reading the two sources.** Both are three-valued: yes, no, or *could not tell*.

```csharp
var located = KitDirectory.Locate(options.KitDir);
var kits = KitDirectory.Harnesses(located);          // null => could not tell

IReadOnlyList<string>? catalog;
string? catalogProblem;
try
{
    catalog = [.. (await harnesses.TiersAsync(ct: ct))
        .SelectMany(t => t.Candidates).Select(c => c.Harness)
        .Distinct(StringComparer.Ordinal)];
    catalogProblem = null;
}
catch (MuthurException ex)   // catalog_invalid; the Tiers panel reports the same thing
{
    catalog = null;
    catalogProblem = ex.Message;
}
```

Catch `MuthurException` only. Anything else is a defect and belongs in `DoctorService`'s own net.

**Category `"harness"` — the two source lines.** Emitted only when a source could not be read, and only when
there is at least one standing agent. They carry category `"harness"` rather than `"agent"` because they are
not about any agent.

| Condition | Subject | Status | Detail |
|---|---|---|---|
| `located is null` | `kit` | Warn | `The kit directory was not found: $MUTHUR_KIT is unset or points nowhere, and there is no kit/ beside the hub or beside its parent. Harnesses were checked against harnesses.json alone.` |
| `located is not null && kits is null` | `kit` | Warn | `The kit directory {located} could not be read. Harnesses were checked against harnesses.json alone.` |
| `catalog is null` | `harnesses.json` | Warn | `{catalogProblem} Harnesses were checked against the kit directory alone.` |

The `harnesses.json` subject is `MuthurEnvironment.HarnessFile`, never a literal. `{catalogProblem}` is
`HarnessService`'s own message verbatim, which already names the path and the reason — render it, do not
invent wording around it.

**Category `"agent"` — one line per standing agent.** Subject is the agent's name. With
`hasKit = kits?.Contains(a.Harness, StringComparer.Ordinal)` and
`inCatalog = catalog?.Contains(a.Harness, StringComparer.Ordinal)`, both `bool?`, the nine cells are:

| `hasKit` | `inCatalog` | Status | Detail |
|---|---|---|---|
| true | true | Ok | `Runs on harness '{H}', which has a kit and a tier entry.` |
| true | false | Ok | `Runs on harness '{H}', which has a kit but no entry in harnesses.json, so no tier can be staffed on it headlessly.` |
| true | null | Ok | `Runs on harness '{H}', which has a kit.` |
| false | true | Warn | `Runs on harness '{H}', which has a tier entry in harnesses.json but no kit, so a session started on it gets no procedures. Add kit/{H}/kit.json, or correct the spelling in harnesses.json.` |
| false | false | **see below** | **see below** |
| false | null | Warn | `Runs on harness '{H}', which has no kit; harnesses.json could not be read, so whether a tier names it could not be settled.` |
| null | true | Ok | `Runs on harness '{H}', which has a tier entry in harnesses.json.` |
| null | false | Warn | `Runs on harness '{H}', which has no entry in harnesses.json; the kit directory could not be read, so whether it has a kit could not be settled.` |
| null | null | Warn | `Runs on harness '{H}'. Neither the kit directory nor harnesses.json could be read, so whether it is a real harness could not be settled.` |

`{H}` is `a.Harness` exactly as registered, in single quotes, never trimmed or lower-cased. Comparison
throughout is `StringComparer.Ordinal`: `Claude` is not `claude`, and a check that pretended otherwise would
hide the class of typo it exists to catch.

**The rule the table encodes, stated once for the doc comment:** a `Fail` is only ever reached when both
sources were read and both said no. A source that could not be read costs the check its confidence, never its
honesty — the worst verdict available then is `Warn`.

**`false`/`false` — the typo.** The status depends on `a.Status`:

- `AgentStatus.Live` → **Fail**:
  `Runs on harness '{H}', which has no kit and no entry in harnesses.json: nothing can be installed for it and no tier can staff it. Most likely a typo. {Known}`
- `AgentStatus.Stale` or `AgentStatus.Limited` → **Warn**:
  `Last seen {age} ago on harness '{H}', which has no kit and no entry in harnesses.json. Either a typo at registration or a harness since removed. {Known}`

`{age}` is `Format.Age(a.LastHeartbeat, context.Now)` — the same call `DoctorRoleCheck.Inspect` makes, from
`Muthur.Server.Components.Shared`.

`{Known}` is the sorted, ordinal-distinct union of `kits` and `catalog`, rendered as:

- non-empty → `This organization has: claude, codex, codex-oss, generic.`
- empty → `Nothing here names a harness: neither the kit directory nor harnesses.json has one.`

(In this cell both sources were read, so neither is null and the union is well defined.)

### 5. Two lists that name the categories

- `DoctorService.Order` (`src/Muthur.Server/Services/DoctorService.cs:27`) becomes
  `["secret", "logging", "ingest", "outbound", "project", "repo", "role", "harness", "agent"]`.
  `harness` before `agent`: an agent's verdict depends on what the sources could say, so a reader meets the
  caveat before the verdict it qualifies.
- `CheckDto`'s `Category` doc comment (`src/Muthur.Contracts/Doctor.cs:13`) gains `"harness"` and `"agent"`.

### 6. One sentence of help text

`src/Muthur.Cli/Commands/SystemCommands.cs:16`: the `doctor` command's description becomes
`"Check whether this hub can do its job: ingest, outbound, projects, repositories, roles, agents. Exit 1 if anything failed."`

### Exit codes are already correct

`SystemCommands.DoctorExitCode` makes any `fail` exit 1 and a `warn` exit 0. A live agent on a typo'd harness
therefore reddens `muthur doctor`; a stale one does not. That is the intent, and no code there changes.

## Units of work

One unit. The server check cannot compile until `KitDirectory` exists, so there is no parallel split to make;
two implementers would only add an integration round to a change of roughly two hundred lines.

### Unit A — the hub can see the kit, and says who is on a harness that is not there

- **Files created:**
  - `src/Muthur.Launch/KitDirectory.cs`
  - `src/Muthur.Server/Services/DoctorAgentCheck.cs`
  - `tests/Muthur.Launch.Tests/KitDirectoryTests.cs`
  - `tests/Muthur.Server.Tests/DoctorAgentTests.cs`
- **Files modified:**
  - `src/Muthur.Cli/Commands/KitCommands.cs` (Design §2 only)
  - `src/Muthur.Server/MuthurOptions.cs` (one property)
  - `src/Muthur.Server/Infrastructure/Startup.cs` (one registration)
  - `src/Muthur.Server/Services/DoctorService.cs` (the `Order` array)
  - `src/Muthur.Contracts/Doctor.cs` (one doc comment)
  - `src/Muthur.Cli/Commands/SystemCommands.cs` (one description string)
  - `tests/Muthur.Cli.Tests/KitInstallTests.cs` (one added test, below)
- **Does:** exactly the Design, nothing beside it.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean with no warnings, `dotnet test` green, and every test below present and
  passing.

#### `tests/Muthur.Launch.Tests/KitDirectoryTests.cs`

Create scratch directories under `Path.GetTempPath()` and remove them in `Dispose`. Tests 4–7 must clear
`MUTHUR_KIT` for the life of the class and restore the previous value in `Dispose`, with a comment saying no
other class in this assembly reads it — the pattern and the reasoning at `KitInstallTests.cs:22-35`.

1. `Harnesses` returns only the subdirectories holding a `kit.json`, and in ordinal order. Build a scratch kit
   with `zeta/kit.json`, `alpha/kit.json` and a `core/` with no manifest; expect exactly `["alpha", "zeta"]`.
2. `Harnesses(null)` is null, and `Harnesses(<a path that does not exist>)` is null.
3. `Harnesses(<an existing empty directory>)` is an empty list and **not** null — assert both, because the two
   answers mean different things to the caller.
4. `Locate(configured)` returns `configured` when it exists.
5. `Locate(configured)` returns **null** when `configured` does not exist, even though a valid kit sits beside
   the base directory — an explicit setting that is wrong is not replaced by a guess.
6. `Locate(null, baseDirectory)` finds `{baseDirectory}/kit`.
7. `Locate(null, baseDirectory)` finds `{parent-of-baseDirectory}/kit` when `{baseDirectory}/kit` does not
   exist. Name this test for the layout it protects: this is the installed hub, whose server lives in
   `server/` with the kit beside it.

#### `tests/Muthur.Server.Tests/DoctorAgentTests.cs`

One `HubFactory` per test class as `DoctorTests` does. Point the hub at a scratch kit with
`Settings["Muthur:KitDir"] = <dir>` and write the catalog by overwriting
`Path.Combine(hub.DataDir, MuthurEnvironment.HarnessFile)` — the hub re-reads it on every call
(`HarnessService` line 13), so a test may write it before or after startup. Register agents through the real
HTTP API with the founder token, as the other server tests do. Advance the clock with `hub.Clock` to make an
agent stale; `MuthurOptions.AgentStaleSeconds` is 180 by default.

1. **The ordinary case is quiet.** An agent on a harness with both a kit and a tier entry: exactly one `agent`
   check, `Ok`, and no `harness` check at all.
2. **A live agent on a harness in neither source fails.** Assert the status is `Fail`, that `report.Fail == 1`,
   that the detail contains the harness as typed, and that it lists what the organization does have.
3. **The same agent, gone stale, only warns.** Advance the clock past `AgentStaleSeconds`; assert `Warn`,
   `report.Fail == 0`, and that the detail says "Last seen".
4. **A kit with no tier entry is fine.** `Ok`, and the detail says no tier can be staffed on it headlessly.
5. **A tier entry with no kit warns**, and the detail says a session started on it gets no procedures.
6. **A kit directory that is not reachable costs the check its Fail.** Point `Muthur:KitDir` at a path that
   does not exist. Assert one `harness`/`kit` `Warn`, that an agent whose harness is only in the catalog is
   `Ok`, and that a **live** agent whose harness is in neither is `Warn` rather than `Fail`.
7. **A catalog that will not parse is reported, not thrown.** Write `not json` over `harnesses.json`. Assert
   one `harness`/`harnesses.json` `Warn` whose detail contains the path, that no check has category
   `"doctor"` (which is what `DoctorService` emits when a check throws), and that agents are still judged by
   the kit alone.
8. **A hub with no standing agents says nothing.** Assert no check has category `"agent"` or `"harness"`, even
   with `Muthur:KitDir` pointing nowhere.
9. **Conductor-staffed sessions are not reported.** Get a staffed session into the ledger through whichever
   path the server tests already use for T-34's `ConductorStaffed` marker — find it, do not invent one.
   Assert it produces no `agent` check while a standing agent on the same harness does.
10. **Case matters.** An agent registered as `Claude` where the kit and catalog both say `claude` is reported,
    not silently accepted.

#### `tests/Muthur.Cli.Tests/KitInstallTests.cs`

Add one test: `muthur kit list` against this repository's own kit prints exactly
`["claude","codex","generic"]`. The class already points `MUTHUR_KIT` at the repository kit and is in the
`KitEnvironment` collection, so it is the right home. Invoke through a `RootCommand` as the class's existing
tests do and capture `Console.Out`. `kit list` had no test of any kind before this change and the refactor in
Design §2 is the reason to give it one.

## Verification

```
dotnet build
dotnet test
```

Passing is: no warnings (warnings are errors here), and every test green — in particular `DoctorTests`,
`KitInstallTests` and `KitManifestTests` unchanged and still passing.

End to end, from an installed build of this branch into a scratch directory, against a scratch hub — never the
live one:

```powershell
pwsh ./scripts/install.ps1 -Destination ./artifacts/t61
$env:MUTHUR_HOME = Join-Path ([IO.Path]::GetTempPath()) ("t61-" + [guid]::NewGuid().ToString("n"))
$env:MUTHUR_URL  = 'http://127.0.0.1:7461'
New-Item -ItemType Directory -Force $env:MUTHUR_HOME | Out-Null
./artifacts/t61/muthur.exe up
./artifacts/t61/muthur.exe agent register --name corner --harness cladue --model opus --tier mastermind --founder
./artifacts/t61/muthur.exe agent register --name straight --harness claude --model opus --tier mastermind --founder
./artifacts/t61/muthur.exe doctor --offline
$LASTEXITCODE
./artifacts/t61/muthur.exe down
Remove-Item -Recurse -Force $env:MUTHUR_HOME
```

Passing looks like: the registration of `corner` **succeeds** (exit 0 — registration is deliberately
unchanged); the report carries an `agent` check whose subject is `corner`, whose status is `fail`, and whose
detail names `cladue` and lists `claude, codex, codex-oss, generic`; an `agent` check for `straight` with
status `ok`; **no** `harness` check, because the installed layout puts the kit where the hub can find it —
which is the half of this task that could not be proved before it existed; and `$LASTEXITCODE` is `1`.

## Out of scope / follow-ups

- **A tier catalog entry naming a harness with no kit is only noticed when an agent is registered on it.** A
  standalone `harness` check over the catalog itself would catch a founder's typo in `harnesses.json` before
  any agent exists, and would catch the harness the conductor is about to fail to staff. Its own task.
- **`muthur harness tiers` cannot say which of its candidates have kits.** The hub can now answer that; the
  command does not ask. Adding it would make the Tiers panel on `/operations` show the same fact this check
  reports, at the point where a founder edits the catalog.
- **`kit install` is still the only consumer of a kit's contents.** Nothing yet validates that a kit named in
  the catalog installs cleanly; `doctor` only asks whether the directory is there.
