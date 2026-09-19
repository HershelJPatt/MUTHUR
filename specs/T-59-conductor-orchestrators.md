# T-59 — The conductor staffs orchestrators too, with a ceiling that follows the work

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the hub can start its own orchestrator sessions from the backlog, the founder can raise or
lower how many sessions it may run without editing a file or restarting, a lower ceiling applies during
hours the founder is asleep, and a tier entry can say which model *and how hard it thinks* — so the ledger
stops recording `codex/default` for sessions that ran something else.

Turning the orchestrator half on is a separate switch, off by default. Landing this must not cause the hub
to spend a single session it does not spend today.

## Context

### What already works, and must not be redesigned

`ConductorService.RunPassAsync` walks `PlanAsync()` and starts sessions until `_running.Count` reaches
`options.ConductorMaxSessions`. **Concurrency is therefore already demand-driven**: if the plan asks for one
session, one starts. What is missing is not a formula over backlog depth — it is a ceiling the founder can
move at runtime, and a lower one for unattended hours. Do not add a depth heuristic.

`AgentLauncher` already runs a session that acts *as* a registered agent, with an identity and a hub token,
falling through to the next candidate when an account is rate-limited. Its own summary says "a validator,
today". Unit C is the second caller, not a new mechanism.

`HarnessService.ReadCatalog` re-reads `harnesses.json` on every call, so catalog edits already apply without
a restart. Keep that property.

### Files that matter

- `src/Muthur.Server/Services/ConductorService.cs` — `PlanAsync` (what to staff), `RunPassAsync` (start it,
  bounded), `_running` (keys `"T-n/role"`), `_stalls` (half-open cap, `ConductorStallProbeMinutes`),
  `EnabledAsync`/`SetEnabledAsync` (the on/off decision lives in `Meta`, not in config, so a restart cannot
  silently undo it — follow that pattern exactly for the new switches).
- `src/Muthur.Server/Services/ValidatorSessionLauncher.cs` — the template for Unit C: `CandidatesAsync`,
  `IdentityFor`, `IdentityName`, `Prompt`, `Allowed`/`Denied`, `EnsureSomethingRan`.
- `src/Muthur.Launch/WorkerLauncher.cs:6` — `HarnessCandidate`. `src/Muthur.Launch/Harness.cs` —
  `WorkerRequest`. `src/Muthur.Launch/CodexAdapter.cs:41-45` — where `--model` is added.
- `src/Muthur.Launch/HarnessDefaults.cs` — the catalog written to a *new* hub.
- `src/Muthur.Contracts/Harnesses.cs` — `HarnessCandidateDto`, `ConductorStatusDto`, `ConductorSwitch`.
- `src/Muthur.Core/Entities/MetaEntry.cs:12` — `ConductorEnabled`, the key pattern to copy.
- `src/Muthur.Server/Api/HarnessEndpoints.cs:25-29` — the conductor endpoints.
- `src/Muthur.Cli/Commands/WorkerCommands.cs:72-91` — the `conductor` command group.
- `tests/Muthur.Server.Tests/ConductorTests.cs` — how the conductor is tested, with a fake launcher that
  records what it was asked to start. Every new behaviour goes here or beside it.

Codebase rules that apply: warnings are errors; every state change goes through `Ledger.MutateAsync` and
records an event in the same transaction; time comes from the injected `TimeProvider`; nothing outside
`Muthur.Launch` and `kit/` names a model vendor; every type crossing HTTP is registered in
`MuthurJsonContext`; new behaviour comes with tests; tests never sleep to synchronize.

## Non-goals

- **No depth heuristic.** See above.
- **No weighted vendor mix.** Filed as a child task. The ordered candidate list plus
  `muthur harness limit <account> --until <t>` is the lever until then.
- Nested fleets (T-19). Any change to how validators are planned, beyond their new ceiling and settings.
- The `muthur ask` → Needs You → founder answer path. The conductor still must never answer a founder
  request and must never staff a task that is `blocked`.
- Rewriting an existing `harnesses.json`. `HarnessDefaults` is what a *new* hub gets; a hub that already has
  the file keeps it, and the founder edits it.

## Design

### Unit A — a candidate says how hard it thinks, and the ledger records what ran

`HarnessCandidate` gains a fourth positional member with a default so existing construction sites compile:

```csharp
public sealed record HarnessCandidate(string Harness, string Model, string? Account, string? ReasoningEffort = null);
```

`WorkerRequest` gains a final member, also defaulted:

```csharp
    string ScratchDirectory,
    /// <summary>Harness-specific reasoning effort, e.g. "high". Null means the harness's own default.</summary>
    string? ReasoningEffort = null);
```

`HarnessCandidateDto` gains a final member (no default — update both construction sites in
`HarnessService.TiersAsync`):

```csharp
public sealed record HarnessCandidateDto(string Harness, string Model, string? Account, bool Limited, DateTimeOffset? LimitedUntil, string? ReasoningEffort);
```

`HarnessService.ReadCatalog` reads an optional `"reasoningEffort"` string per candidate, exactly as it reads
`"model"`: absent, null or empty becomes `null`.

`CodexAdapter.Build`, immediately after the existing `--model` block:

```csharp
if (request.ReasoningEffort is { Length: > 0 } effort)
{
    arguments.Add("-c");
    arguments.Add($"model_reasoning_effort=\"{effort}\"");
}
```

`ClaudeAdapter` is not changed: it has no equivalent and must ignore the field.

`HarnessDefaults.CatalogJson` — every `"harness": "codex"` entry becomes:

```json
{ "harness": "codex", "model": "gpt-6-astra", "reasoningEffort": "high", "account": "chatgpt-subscription" }
```

`codex-oss` and every `claude` entry are unchanged.

Both session launchers pass the candidate's `ReasoningEffort` into the `WorkerRequest` they build.

Why this matters beyond taste: `ValidatorSessionLauncher.IdentityFor` records `candidate.Model` or the
literal `"default"`, so today the ledger carries `codex/default` on 103 events while the founder's own
`~/.codex/config.toml` silently supplies `gpt-6-astra`. Receipts attribute spend to a model that does not
exist, and the conductor's cross-provider preference reasons over a record that does not describe what ran.
Naming the model in the catalog fixes both; no change to `IdentityFor` is needed or wanted.

### Unit B — a ceiling the founder can move, and a lower one while they sleep

Two new `Meta` keys beside `MetaEntry.ConductorEnabled`:

```csharp
public const string ConductorSessions = "conductor_sessions";          // "4"
public const string ConductorUnattended = "conductor_unattended";      // "22:00-07:00@1", or absent
```

Both are read through `Ledger.ReadAsync` and written through `Ledger.MutateAsync` with an event, exactly as
`SetEnabledAsync` does.

**Effective ceiling**, computed per pass:

1. `configured` = `ConductorSessions` if set and parses to an int ≥ 1, else `options.ConductorMaxSessions`.
2. If `ConductorUnattended` is set and **now** falls inside its window, the ceiling is
   `Math.Min(configured, windowSessions)`. Otherwise it is `configured`.
3. The window is local time, via `clock.LocalTimeZone`, and **wraps midnight**: `22:00-07:00` means
   `hh:mm >= 22:00 || hh:mm < 07:00`. A window whose ends are equal is treated as not set.

`RunPassAsync` uses this value in place of `options.ConductorMaxSessions`. Nothing else changes.

`ConductorStatusDto` gains two final members:

```csharp
    int Ceiling, string CeilingReason);
```

`CeilingReason` is one short sentence a founder reads on a card, exactly one of:

- `"Muthur:ConductorMaxSessions."` — nothing set, config default in force.
- `"Set by the founder."` — `ConductorSessions` is what applies.
- `"Unattended 22:00-07:00 caps this at 1."` — the window applies (substitute the real values).

`MaxSessions` on the DTO keeps its present meaning (`options.ConductorMaxSessions`) so nothing that reads it
today changes; `Ceiling` is what the pass actually enforces.

**Routes and CLI.** One new route:

```csharp
public const string ConductorSessions = Api + "/conductor/sessions";
```

`POST` it with `ConductorSessionsRequest`, founder only, returning `ConductorStatusDto`:

```csharp
public sealed record ConductorSessionsRequest(int? Sessions, string? UnattendedFrom, string? UnattendedTo, int? UnattendedSessions, bool Clear);
```

- `Clear: true` removes **both** keys and records `conductor.ceiling_cleared`.
- `Sessions` below 1 → `Fail.Rule("sessions_invalid", "The conductor needs at least one session to do anything.")` (422/exit 2).
- Any of `UnattendedFrom`/`UnattendedTo`/`UnattendedSessions` given without the other two →
  `Fail.Rule("unattended_incomplete", "An unattended window needs --from, --to and --sessions.")`.
- A time that is not `HH:mm` (00-23, 00-59) → `Fail.Rule("time_invalid", "Times are HH:mm, 24-hour.")`.
- `UnattendedSessions` below 1 → the same `sessions_invalid`.
- Events: `conductor.sessions_set` payload `{ sessions }`, `conductor.unattended_set` payload
  `{ from, to, sessions }`.

CLI, inside the existing `conductor` group:

```
muthur conductor sessions <n> --founder
muthur conductor unattended --from 22:00 --to 07:00 --sessions 1 --founder
muthur conductor unattended --clear --founder
```

The CLI stays a pure passthrough: it builds the request, prints the returned `ConductorStatusDto`, and adds
no prose of its own.

### Unit C — the conductor staffs orchestrators

A third `Meta` key and its switch, defaulting to **off**:

```csharp
public const string ConductorOrchestrators = "conductor_orchestrators";
```

`muthur conductor orchestrators on|off --founder`, `POST` to `Routes.Conductor` with a second switch record
`ConductorOrchestratorSwitch(bool Enabled)` on a new route `Api + "/conductor/orchestrators"`. Events:
`conductor.orchestrators_on` / `conductor.orchestrators_off`. It is off on a hub that upgrades into this
build, for the same reason `ConductorEnabled` is: upgrading must never begin spending.

**The assignment and the launcher.** Beside the existing pair:

```csharp
public sealed record OrchestratorAssignment(int TaskId, string TaskKey, string TaskTitle, string Project);

public interface IOrchestratorSessionLauncher
{
    Task StartAsync(OrchestratorAssignment assignment, CancellationToken ct = default);
}
```

`OrchestratorSessionLauncher` mirrors `ValidatorSessionLauncher` member for member: same tier
(`"mastermind"`), same `Allowed`/`Denied` lists, same `AgentLauncher` call, same rate-limit fallthrough, same
`EnsureSomethingRan` split between "never reached a process" and "started and produced nothing", same
scratch directory convention (`{DataDir}/conductor/{TaskKey}-orchestrator`).

It differs in four places, and only these:

1. **Identity.** `IdentityName(string task) => $"orchestrator-{task.ToLowerInvariant()}"` — `"T-59"` becomes
   `"orchestrator-t-59"`. One agent per task, stable across retries of that task.
2. **No role.** It takes no role and claims no validation. It registers, and the session claims the task
   itself.
3. **Candidates.** No `avoid`: there is no prior author to differ from. All non-limited mastermind
   candidates in catalog order.
4. **Prompt**, verbatim:

```
You are a mastermind orchestrator in this MUTHUR organization, acting as the agent in $MUTHUR_AGENT.

Claim {TaskKey} ("{TaskTitle}") and take it from claim to landing:

    muthur task claim {TaskKey}

Then follow the orchestrate procedure in this repository. Exit code 3 on the claim means another session
already has it: stop immediately and do nothing else.

Rules that are not yours to bend:
- You own this task and no other. Do not claim a second one.
- Write a frozen spec before you delegate, and delegate the building; you do not write product code yourself.
- Never push, never merge into the default branch. `muthur task land` is how work lands.
- If the task needs a decision only the founder can make, `muthur ask` and stop. Never guess at a product
  decision, and never answer a founder request yourself.

You are running unattended on {candidate.Harness}. Leave nothing behind that a person would have to clean up.
```

**Planning.** A new `PlanOrchestratorsAsync` beside `PlanAsync`, returning `IReadOnlyList<OrchestratorAssignment>`,
selecting tasks where **all** hold:

- `State == TaskState.Backlog` (so never `blocked`, never already in flight);
- the project has a non-empty `RepoPath`;
- no entry in `_running` for `$"{TaskKey}/#orchestrator"`;
- the `_stalls` cooldown for that key has expired, on the existing half-open rule.

Ordered `OrderByDescending(Priority).ThenBy(Id)` — the same order `PlanAsync` uses. It returns `[]` when
either `EnabledAsync` or the orchestrators switch is off.

`RunPassAsync` starts validator assignments first, then orchestrator assignments, both against the one
ceiling from Unit B: **validation drains before new work begins**, because a task in `validating` is closer
to done than a task in the backlog.

**The `#` prefix is load-bearing.** `PlanAsync` parses `_running` keys by splitting on `/` and calling
`ValidatorSessionLauncher.IdentityName(task, role)`. `RoleService`'s key pattern is
`^[a-z0-9][a-z0-9-]{0,37}$`, so `#orchestrator` can never be a role key and the two key spaces cannot
collide. `PlanAsync` must skip any `_running` entry whose role part starts with `#`.

**Unproductive sessions** reuse `UnproductiveAsync` and the existing `_stalls` map unchanged, so an
orchestrator that will not start stops being retried on the same rule validators already follow.

## Units of work

### Unit A — reasoning effort, and a catalog that names its model
- **Files:** `src/Muthur.Launch/WorkerLauncher.cs`, `src/Muthur.Launch/Harness.cs`,
  `src/Muthur.Launch/CodexAdapter.cs`, `src/Muthur.Launch/HarnessDefaults.cs`,
  `src/Muthur.Contracts/Harnesses.cs`, `src/Muthur.Server/Services/HarnessService.cs`,
  `src/Muthur.Server/Services/ValidatorSessionLauncher.cs`; tests in `tests/Muthur.Launch.Tests/`.
- **Does:** the Unit A section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean; `dotnet test` green.
  - A codex `WorkerRequest` with `ReasoningEffort = "high"` produces arguments containing `-c` immediately
    followed by `model_reasoning_effort="high"`; with `null` or `""` neither appears.
  - A claude `WorkerRequest` with `ReasoningEffort = "high"` produces arguments containing neither.
  - A catalog whose codex entry carries `"reasoningEffort": "high"` surfaces it on `HarnessCandidateDto`;
    one that omits it surfaces `null`.
  - `HarnessDefaults.CatalogJson` parses, and its codex candidates report model `gpt-6-astra` and effort
    `high`.

### Unit B — a ceiling the founder can move
- **Files:** `src/Muthur.Core/Entities/MetaEntry.cs`, `src/Muthur.Server/Services/ConductorService.cs`,
  `src/Muthur.Contracts/Harnesses.cs`, `src/Muthur.Contracts/Routes.cs`,
  `src/Muthur.Contracts/MuthurJsonContext.cs`, `src/Muthur.Server/Api/HarnessEndpoints.cs`,
  `src/Muthur.Cli/Commands/WorkerCommands.cs`; tests in `tests/Muthur.Server.Tests/ConductorTests.cs` and
  `tests/Muthur.Cli.Tests/`.
- **Does:** the Unit B section above, exactly.
- **Depends on:** nothing. (Touches `ConductorService` and `Harnesses.cs` alongside Unit C — integrate in
  the order A, B, C.)
- **Acceptance:**
  - With nothing set, `Ceiling` equals `options.ConductorMaxSessions` and `CeilingReason` names the config key.
  - `sessions 4` then a pass with five staffable pairs starts four, not five; `status` reports `Ceiling: 4`.
  - The value survives a hub restart (assert by reading `Meta` back through a new service instance).
  - With the fake clock inside a `22:00-07:00@1` window, `Ceiling` is 1 and the reason names the window;
    stepping the clock to 12:00 returns it to the configured value. Both times are driven by advancing the
    injected `TimeProvider`, never by sleeping.
  - `sessions 0` → 422 `sessions_invalid`; `--from 22:00` alone → 422 `unattended_incomplete`;
    `--from 9am` → 422 `time_invalid`.
  - `--clear` removes both and returns to the config default.

### Unit C — orchestrator sessions
- **Files:** created `src/Muthur.Server/Services/OrchestratorSessionLauncher.cs`; modified
  `src/Muthur.Server/Services/ConductorService.cs`, `src/Muthur.Core/Entities/MetaEntry.cs`,
  `src/Muthur.Contracts/Harnesses.cs`, `src/Muthur.Contracts/Routes.cs`,
  `src/Muthur.Contracts/MuthurJsonContext.cs`, `src/Muthur.Server/Api/HarnessEndpoints.cs`,
  `src/Muthur.Cli/Commands/WorkerCommands.cs`, `src/Muthur.Server/Program.cs` (DI registration beside
  `IValidatorSessionLauncher`); tests in `tests/Muthur.Server.Tests/ConductorTests.cs`.
- **Does:** the Unit C section above, exactly.
- **Depends on:** Unit B (it uses the single ceiling). Build against Unit B's branch.
- **Acceptance:**
  - Orchestrators off (the default): a backlog task produces no orchestrator launch, and validator staffing
    is unaffected.
  - Orchestrators on: one unclaimed backlog task → exactly one launch, for that task, with a prompt naming
    its key and title, under agent name `orchestrator-t-<n>`.
  - A task with a live claim, a `blocked` task, and a task whose project has an empty `RepoPath` each
    produce no launch.
  - Two backlog tasks and a ceiling of 1 → one launch, not two.
  - A task in `validating` and a task in `backlog` with a ceiling of 1 → the validator is staffed, not the
    orchestrator.
  - The same task is never staffed twice concurrently, and a launcher that throws leaves the `_running`
    entry removed so the next pass may retry it, subject to the existing stall cooldown.
  - `ConductorTests` pins that no `_running` key parsed by `PlanAsync` is treated as a role when its role
    part starts with `#`.

## Verification

```
dotnet build        # 0 warnings, 0 errors
dotnet test         # all green, single run
pwsh ./scripts/install.ps1 -Destination ./artifacts/validate
```

End to end against a **scratch** hub — never the live one — with `MUTHUR_HOME` and `MUTHUR_URL` prefixed per
command and never exported. No browser and no GUI is used anywhere below; the dashboard is not part of this
task's acceptance.

```powershell
# the ceiling is readable and movable
muthur conductor status                      # Ceiling equals MaxSessions, reason names the config key
muthur conductor sessions 4 --founder        # Ceiling 4, reason "Set by the founder."
muthur conductor unattended --from 22:00 --to 07:00 --sessions 1 --founder
muthur conductor status                      # inside the window: Ceiling 1 and the reason names it
muthur conductor unattended --clear --founder
muthur conductor sessions 0 --founder        # exit 2, sessions_invalid
muthur conductor unattended --from 9am --to 07:00 --sessions 1 --founder   # exit 2, time_invalid

# the switch is off until asked, and the ledger says so
muthur conductor orchestrators on --founder
muthur log --limit 5                         # conductor.orchestrators_on
```

To exercise staffing without spending a subscription, put a stand-in `claude` on the scratch hub's PATH that
prints `{"result":"ok","is_error":false}` — the technique the validator brief documents. With one unclaimed
backlog task and orchestrators on, one pass registers an agent named `orchestrator-t-<n>` and records
`conductor.staffing`; `muthur agent list` shows it. With orchestrators off, neither happens.

A validator should also confirm the negative: with orchestrators **off**, this build staffs validation
exactly as the build before it did, and `muthur conductor status` reports the same numbers a founder saw
yesterday apart from the two new fields.

## Out of scope / follow-ups

- **Weighted vendor mix.** The founder wants to shift the Claude/Codex balance week to week for token
  budget. An ordered candidate list expresses "prefer Claude", not "about 70% Claude", because the first
  non-limited candidate takes everything. Until that exists the lever is
  `muthur harness limit <account> --until <t>`. Its own task.
- The dashboard shows neither the ceiling nor orchestrator sessions. T-23 owns the conductor's presence
  there and should pick these up.
- `harnesses.json` on an existing hub keeps whatever it has, so the `gpt-6-astra` / `high` correction
  reaches only new hubs. Whether `muthur doctor` should notice a codex candidate with no model is worth its
  own task.

## Amendment 1 — `muthur worker run` carries the effort too

Unit A's implementer reported two things about the spec as frozen. Both are recorded here rather than
fixed silently, because the spec is the record of what was asked.

**The spec said "both construction sites in `HarnessService.TiersAsync`". There is one.** Exactly one
`new HarnessCandidateDto(...)` exists in the repository, at `HarnessService.cs:34`. The sentence was wrong
and cost nothing; the implementer updated the one site. There are three `new HarnessCandidate(...)` sites,
which is probably what the sentence was reaching for.

**The CLI worker path drops the field, and that is a defect this task must not leave behind.**
`src/Muthur.Cli/Commands/WorkerCommands.cs:119` builds `new HarnessCandidate(c.Harness, c.Model, c.Account)`
from the tier DTO, and line 153 builds its `WorkerRequest` without the field. So `muthur worker run` would
launch codex workers at the harness's own default effort while the catalog says `high` — and the
**implementer** tier's codex entry is one of the two this task changes, so the tier most affected is the one
delegated units actually run on.

That is the same class of untruth as `codex/default`: a catalog the founder edits, silently ignored by the
code path that consumes it most. Unit A is therefore extended by two lines and one test:

- `WorkerCommands.cs:119` becomes `new HarnessCandidate(c.Harness, c.Model, c.Account, c.ReasoningEffort)`.
- The `WorkerRequest` at line 153 gains `ReasoningEffort: c.ReasoningEffort` as its final argument.
- A test in `tests/Muthur.Cli.Tests/` asserts that a tier whose candidate carries `"reasoningEffort": "high"`
  reaches the built `WorkerRequest` with that value, and that a candidate without one yields `null`.

`Muthur.Cli` is added to Unit A's file list. Nothing else about Unit A changes.

## Amendment 2 — the README must not keep saying the ceiling is config-only

Unit B's implementer reported that `README.md` line 171 documents `Muthur:ConductorMaxSessions` as
"validator sessions running at once", with no hint that the founder can now override it at runtime. Unit B's
file list does not include the README, so it was correctly left alone and reported.

It has to change, for the same reason Amendment 1 existed: a knob documented one way and implemented another
is a knob that lies. The row immediately above it already shows the exact phrasing this project uses for a
setting the database overrides:

> `Muthur:ConductorEnabled` | false | the starting value only; `muthur conductor on --founder` is stored in
> the database and outlives a restart

Unit B is extended by documentation only — no code, no tests:

- `README.md`: the `Muthur:ConductorMaxSessions` row becomes "the starting value only; `muthur conductor
  sessions <n> --founder` is stored in the database and outlives a restart", and the table gains a row for
  the unattended window naming `muthur conductor unattended --from --to --sessions`.
- `kit/briefs/` and `kit/core/`: wherever the conductor's session budget is described to an agent, say that
  the ceiling is readable from `muthur conductor status` as `ceiling` with `ceilingReason`, and that it is
  not the same number as `maxSessions`.

The three judgment calls Unit B's implementer flagged are all accepted as built, and recorded here so the
next reader does not have to rediscover them:

1. **`CeilingReason` substitutes the effective ceiling, not the window's own number.** When a window is open
   but wider than the founder's standing number, the sentence still reports what the pass will enforce. A
   reason that contradicted the `Ceiling` field beside it would be worse than a slightly loose sentence.
2. **A request carrying both a standing number and a window writes both**, and `Clear` wins over everything
   in the same request. The CLI never sends the combination; the API allowing it costs nothing.
3. **A malformed stored `conductor_unattended` reads as no window rather than throwing.** Only
   `SetCeilingAsync` writes that key and it validates first, so this can only be reached by editing the
   database by hand — and a hub that refuses to start because someone did is worse than one that ignores it.

## Amendment 3 — what Unit C found, and what it may not leave behind

Unit C's implementer raised one judgment call and three spec problems. All four are settled here.

### The judgment call is accepted, and it is the most important thing in this unit

A session that starts, runs its forty-five minutes, exits cleanly and **never claims its task** leaves that
task in `Backlog`, where the next pass stages it again — and the next, all night. The spec covered "an
orchestrator that will not start" and said nothing about one that starts and achieves nothing. That is the
money-burning failure mode this unit exists to avoid.

The implementer's answer stands exactly as built: on a clean return, read the task's state. Still `Backlog`
means charge it through the **unchanged** `UnproductiveAsync` and the **unchanged** `_stalls` map, so it
stalls half-open at `ConductorMaxAttempts` precisely as a validator reaching no verdict does. Any other
state — claimed, blocked, cancelled, landed — clears the stall, because the session did what it was for.
"Did it record a verdict" and "did it claim the task" are the same question asked of two tiers.

### `conductor status` must say whether this half is on

The spec added `Ceiling` and `CeilingReason` to `ConductorStatusDto` for Unit B and named no field for Unit C,
so today the only way to learn whether orchestrator staffing is on is to read the ledger. The spec's own
Verification section walks a validator through typing the command and then checking `muthur log`, which is
the workaround standing in for the missing field.

That will not do for the most expensive switch in the system. `ConductorStatusDto` gains one final member:

```csharp
    int Ceiling, string CeilingReason, bool Orchestrators);
```

`true` when the `conductor_orchestrators` key says so. `muthur conductor status` therefore answers "is the
hub allowed to start orchestrators" without anyone reading events, and the Verification section's ledger
check becomes a corroboration rather than the only route.

### The founder must not be sent to the wrong place at 3am

Reusing `UnproductiveAsync` unchanged was right for the mechanism and wrong for the words: a stalled
orchestrator currently tells the founder *"The conductor could not start a validator for T-5
(#orchestrator) 3 times…"*. The validator brief this organization runs on says it plainly — "the wording of
a failure is a feature here; a message that sends the founder to the wrong place at 3am is a defect, not a
nit."

`UnproductiveAsync` picks the noun from the role: `"#orchestrator"` reads "an orchestrator", anything else
reads "a validator". No existing test's expected string changes.

### One deny list, not two

Unit C duplicated `Allowed` and `Denied` into `OrchestratorSessionLauncher` because the file list excluded
`ValidatorSessionLauncher.cs` and the spec said "member for member". The implementer flagged that they can
now drift. They must not: that deny list is what stops a launched session pushing, merging or checking out
the default branch, and two copies of a security boundary is how one of them quietly grows a hole.

Hoist both arrays to one `internal static` home shared by the two launchers — `ValidatorSessionLauncher` is
added to Unit C's file list for this. The lists' contents do not change.

### Accepted without change

- **DI is registered in `Infrastructure/Startup.cs:75`, not `Program.cs`.** The spec's file list was wrong
  in the same way Amendment 1's "two construction sites" was wrong.
- **`EnsureSomethingRan` is called rather than copied.** One fork is easier to keep honest than two.
- **`AttendedReason` is not consulted when planning orchestrators.** The implementer read it correctly:
  attended means this task needs a human *validator*, so the work is still worth orchestrating and it is the
  validation half that must skip it.
- `_lastAction` wording, the `ConductorWorker` log line, and orchestrators-on not clearing `_stalls`.
- The honest note on the `#` skip: it is defence rather than a live bug fix, because `liveHolders` is only
  read by `validation.ValidatorKey` today. Keeping the invariant local to the code that depends on it is
  worth the line, and the comment says exactly that rather than claiming more.

### Follow-up, not this task

`ValidatorLaunchException` and `ValidatorSessionException` now carry orchestrator failures too, so their
names under-describe them. Renaming touches several files for no behaviour change; it belongs in its own
task rather than in the diff a validator is about to read.

## Amendment 4 — the whole sentence, not just the noun

Amendment 3 asked for the noun. That was too small a change, and Unit C's implementer said so rather than
doing the literal thing.

Swapping only the noun would have left an orchestrator's no-verdict message reading: *"The conductor started
3 orchestrators for T-5 and none of them **reached a verdict**. … then re-spec the task, **validate it
yourself**."* Sound advice about validation, and nonsense about a task nobody has claimed — the same
send-the-founder-to-the-wrong-place defect Amendment 3 was written to fix, one layer below the word it fixed.

`Unproductive.NoVerdict` therefore has an orchestrator arm of its own: *"…and none of them **claimed it**.
They ran and exited cleanly, so something is stopping them from **starting the task** at all rather than
failing at it. Look at the bus for what they said, then re-spec the task, **take it yourself**, or raise
`Muthur:ConductorMaxAttempts`."* The validator arm beside it is byte-identical to what it was.

Recorded because the amendment that asked for it was mine and it was wrong by omission. The implementer
could only see it because it had the full message body in front of it; I had quoted myself only the noun.

Left as they are, deliberately: `_lastAction`'s strings still name the role key rather than the kind, and the
`(#orchestrator)` parenthetical is redundant beside "an orchestrator". Both are accurate, and one message
format is worth more than two that read slightly better.

## Amendment 5 — a session the hub started must not outlive the hub

Validation failed T-59 on a defect it reproduced exactly: stop the hub while a session is running, start it
again, and a second session is staffed for the same task. Two orchestrators, one task — the thing Unit C's
acceptance promises never happens, and the most expensive way this system can fail.

The validator's reproduction, on the installed product with a stand-in harness so nothing was spent: hub PID
57396 staffs `orchestrator-t-9` as PID 71032; `down` then `up`; hub PID 48708 staffs `orchestrator-t-9`
again as PID 26832. Both children alive, both told to claim T-9, `status` reporting `running=1`.

### Root cause, and it is one argument

`ConductorService.RunSessionAsync` launches with **`CancellationToken.None`**:

```csharp
try { await launcher.StartAsync(assignment, CancellationToken.None); }
```

`_running` is in-memory, so a restart begins with an empty set and re-plans a task whose process is still
alive. The child is not merely unaccounted for — it is deliberately detached from the host's lifetime.

`ProcessRunner` already does the right thing when its token is cancelled:

```csharp
catch (OperationCanceledException)
{
    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    if (ct.IsCancellationRequested) throw;
```

So the machinery exists and is simply not wired to anything.

### The fix

`ConductorService` owns a `CancellationTokenSource` for the sessions it starts, created with the service and
cancelled when the host stops. Both `RunSessionAsync` and the orchestrator equivalent pass **that** token to
`StartAsync` in place of `CancellationToken.None`. `ConductorWorker` cancels it as it stops, before the host
exits, so every child is killed with its process tree.

One ledger event when it fires — `conductor.sessions_terminated` with `{ count }` — because a founder
restarting a hub should be able to see that work was cut off rather than infer it from a gap.

Nothing else changes. In particular the conductor still does not persist `_running` across restarts and does
not need to: with the children dead, an empty set after restart is *true*.

### Why killing is right, rather than adopting or persisting

A session that outlives the hub cannot be accounted for by anything — not `status`, not the ceiling, not the
stall counter — so letting it run is strictly worse than ending it. And ending it loses less than it appears
to, because the rest of the machine already handles what follows: the killed session's task claim lapses,
`LeaseSweeper` returns the task to the backlog inside thirty seconds keeping its branch and spec, and T-60
then plans it with `Resuming: true` so the next session is told it is continuing rather than starting. The
work in the branch survives; only the process is lost.

### Acceptance

- A fake launcher that blocks until its token is cancelled: cancelling the conductor's session lifetime ends
  the call, and the ledger records `conductor.sessions_terminated`.
- After that cancellation `RunningCount` is 0, so a subsequent pass plans the task again exactly once.
- A session that finishes normally records nothing about termination.
- Driven by the fake clock and observable conditions, never a sleep.

The validator's end-to-end reproduction is the real acceptance: `down` and `up` with a stand-in harness
holding a session must leave no surviving child, and must not produce two `conductor.staffing` events for one
task.

## Amendment 6 — three things Amendment 5 should have asked for

Amendment 5 named four changes. The implementer made three more, each in service of those four, and each
accepted. The first is not optional: without it Amendment 5 would have introduced a worse defect than the
one it cured.

**A cancelled session is not charged to its pair.** With sessions now cancelled on shutdown, `StartAsync`
throws `OperationCanceledException`, which `RunSessionAsync` would have classified as
`Unproductive.NeverStarted`. Every session in flight would take a strike and write `conductor.failed` during
shutdown, and three restarts would stall a pair that was never given the chance to fail — a hub that
punishes the founder for turning it off and on again. Both runners therefore return early when the session
lifetime is already cancelled, before classification, with the `finally` still clearing `_running`.

**The stop waits for the kills it ordered**, bounded at ten seconds. `CancellationTokenSource.Cancel()`
returns before `ProcessRunner`'s `catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }`
runs, so without the wait the host can exit first and the ledger event records an intention rather than an
outcome. This amendment's acceptance is "`down` and `up` must leave no surviving child", and probably-killed
is not that. The bound is real time because it is a hang detector that nothing in a test reaches; a stop
that never returns is worse than a child that lingers a moment.

**`RunPassAsync` returns 0 once the stop has begun.** A pass already in flight would otherwise launch a
child with nobody left to cancel it — the shutdown creating the very orphan it exists to prevent.

Accepted without change: `ConductorService` is deliberately **not** `IDisposable`. Cancelling a disposed
source throws, the class already holds an undisposed `SemaphoreSlim`, and disposing here would add a class
of shutdown-ordering bug rather than remove one.
