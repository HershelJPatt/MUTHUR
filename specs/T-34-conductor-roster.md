# T-34 — Conductor agents accumulate one roster entry per (task, role) pair

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, `muthur agent list` and the dashboard's agents rail show the agents a person registered,
and say in plain numbers how many conductor-staffed sessions they left out — `11 conductor hidden`, not a
silently shorter list. `muthur agent list --all` and an **All** button on the rail show every row again.
No agent row is ever deleted, retired or hidden by inference: a session is conductor-staffed because the
code that staffed it recorded so at registration, in a column of its own.

This is option A of the three the task body records, and the founder chose it in the answer to request #21.
The two rules that came with the choice are load-bearing and appear below as design constraints:

1. **The omitted count is shown, not implied.** A rail that quietly drops rows is worse than a long one,
   because the founder cannot tell a quiet organization from a filtered view.
2. **The marker is set by the two launchers at registration and by nothing else.** No prefix matching, no
   heartbeat-age heuristic, no inference of any kind.

## Context

**Why a name prefix is the wrong key.** T-59 gave the conductor a *second* name family.
`ValidatorSessionLauncher.IdentityName(task, role)` produces `conductor-win-validator-t-13`;
`OrchestratorSessionLauncher.IdentityName(task)` produces `orchestrator-t-59`, with no `conductor-` prefix
at all. Anything keyed on the name would have missed half the rows on the day it shipped, and the half it
missed is the expensive half. Hence a column.

**Why nothing is deleted.** `MuthurDb.cs` makes `WorkTask.Owner` `DeleteBehavior.Restrict`, so an
orchestrator agent — which by construction owns its task — cannot be deleted at all; and
`TaskValidation.Agent` is `SetNull`, so deleting a validator agent erases who validated the task. The roster
is ledger-adjacent record. This task hides rows from two views and destroys nothing.

**Files that matter:**

- `src/Muthur.Core/Entities/Entities.cs` — the `Agent` entity (line ~20).
- `src/Muthur.Data/Migrations/` — EF migrations. Most recent is `20260919140806_TaskHold`.
- `src/Muthur.Contracts/Agents.cs` — `AgentDto`, `RegisterAgentRequest`.
- `src/Muthur.Contracts/MuthurJsonContext.cs` — every type that crosses HTTP is registered here.
- `src/Muthur.Server/Services/AgentService.cs` — `RegisterAsync`, `ListAsync`, `GetAsync`.
- `src/Muthur.Server/Services/Mapper.cs` — `Agent.ToDto`.
- `src/Muthur.Server/Api/AgentEndpoints.cs` — `GET /api/v1/agents`.
- `src/Muthur.Server/Services/ValidatorSessionLauncher.cs` — `IdentityFor`, registration at line ~124.
- `src/Muthur.Server/Services/OrchestratorSessionLauncher.cs` — `IdentityFor`, registration at line ~80.
- `src/Muthur.Cli/Commands/AgentCommands.cs` — `muthur agent list`.
- `src/Muthur.Server/Components/Panels/AgentsPanel.razor` — the rail.

**Patterns to follow, by example:**

- The `--all` flag and its query parameter already exist once, for `muthur validate list`. Copy that shape
  exactly: `src/Muthur.Cli/Commands/RoleCommands.cs:97` (`ValidationListQuery`) and
  `src/Muthur.Server/Api/RoleEndpoints.cs:51` (`(HttpContext http, string? role, bool? all, …)`).
- A panel-head filter group already exists in `src/Muthur.Server/Components/Panels/StreamPanel.razor`:
  `<div class="filters">` with `<button class="filter @Active(…)">`. Both classes are already in
  `wwwroot/app.css`. **Add no CSS.**
- Every state change goes through `Ledger.MutateAsync` and records an event; reads go through
  `Ledger.ReadAsync`. `AgentService` already does both — do not introduce a third way.

## Non-goals

- **No delete, retire or revoke path for an agent.** Not a soft status either. See "Why nothing is deleted".
- **No new `AgentStatus` value.** `AgentStatus` stays `live | stale | limited`. The filter does not hang on
  a status and does not need one. If a terminal status is ever wanted, that is a different task.
- **No sweeper, no background job, no retention window.** Nothing periodically reclassifies anything.
- **`HeaderStats.razor` and `CommsPanel.razor` are not touched.** `HeaderStats` counts live agents as a
  health number and that number is accurate; `CommsPanel` needs every agent because it addresses messages to
  them. Both keep calling the unfiltered `AgentService.ListAsync`.
- **`GET /api/v1/agents/me` and `muthur agent whoami` keep their shape.** They gain the new `AgentDto`
  field like every other `AgentDto` consumer, and nothing else.
- No change to how conductor sessions are named, staffed, retried or torn down.

## Design

### 1. The column

`src/Muthur.Core/Entities/Entities.cs`, on `Agent`, after `Account`:

```csharp
/// <summary>
/// The conductor staffed this session, rather than a person registering a standing agent. Set by the two
/// session launchers at registration and by nothing else — never inferred from the name, which would miss
/// the orchestrator family entirely, and never from heartbeat age. Sticky: a conductor-staffed session that
/// re-registers itself through the public API is still a session the conductor staffed.
/// </summary>
public bool ConductorStaffed { get; set; }
```

No EF configuration is needed in `MuthurDb.cs`: a non-nullable `bool` maps by convention. The migration must
default existing rows to `false`, which is what EF's default for a new non-nullable `bool` column already does.
Migration name: **`AgentConductorStaffed`**.

### 2. Registration sets it, and never clears it

In `AgentService`, keep the existing public signature and add a conductor-only entry point beside it:

```csharp
public Task<RegisterAgentResponse> RegisterAsync(Caller caller, RegisterAgentRequest request, CancellationToken ct = default) =>
    RegisterCoreAsync(caller, request, conductorStaffed: false, ct);

/// <summary>
/// Registration for a session the conductor is staffing. Separate from <see cref="RegisterAsync"/> rather
/// than a flag on it so that the HTTP endpoint has no way to reach it: what marks a row is the code that
/// staffed the session, not anything a caller can send.
/// </summary>
internal Task<RegisterAgentResponse> RegisterConductorSessionAsync(RegisterAgentRequest request, CancellationToken ct = default) =>
    RegisterCoreAsync(Caller.Founder, request, conductorStaffed: true, ct);

private async Task<RegisterAgentResponse> RegisterCoreAsync(Caller caller, RegisterAgentRequest request, bool conductorStaffed, CancellationToken ct)
```

`RegisterCoreAsync` is today's `RegisterAsync` body, with exactly two changes inside the `MutateAsync`:

```csharp
// Sticky, never cleared. A conductor-staffed session that registers again through `muthur agent register`
// — which the orchestrate procedure tells it to do if its token is gone — is still a staffed session.
if (conductorStaffed) agent.ConductorStaffed = true;
```

and the recorded payload gains the field, so the ledger says why a row is hidden:

```csharp
m.Record(existing is null ? "agent.registered" : "agent.reregistered",
    payload: new { agent = name, agent.Harness, agent.Model, agent.Tier, agent.Account, agent.ConductorStaffed });
```

**`RegisterAgentRequest` does not change.** The marker never crosses the wire inbound.

Both launchers switch their one call:

- `ValidatorSessionLauncher.IdentityFor` (~line 124) and `OrchestratorSessionLauncher.IdentityFor` (~line 80)
  replace `await agents.RegisterAsync(Caller.Founder, new RegisterAgentRequest(...), ct)` with
  `await agents.RegisterConductorSessionAsync(new RegisterAgentRequest(...), ct)`.
  The `Caller.Founder` argument goes away; if that leaves an unused `using` or `Caller` reference, remove it
  only if the compiler says so.

These are the **only** two call sites of `RegisterConductorSessionAsync` in the codebase. Do not add a third.

### 3. The DTO

`src/Muthur.Contracts/Agents.cs`, `AgentDto` gains a final positional member:

```csharp
    IReadOnlyList<string> Roles,
    int OpenTasks,
    bool ConductorStaffed);
```

and a new record in the same file:

```csharp
/// <summary>
/// The roster as one view: the rows it shows, and how many it left out. The count is part of the payload
/// rather than a thing the reader recomputes, because a filtered list that does not say what it dropped
/// reads exactly like a quiet organization. Zero when `all` was asked for — nothing was omitted.
/// </summary>
public sealed record AgentRosterDto(IReadOnlyList<AgentDto> Agents, int ConductorHidden);
```

`MuthurJsonContext` gains `[JsonSerializable(typeof(AgentRosterDto))]`. Remove
`[JsonSerializable(typeof(IReadOnlyList<AgentDto>))]` **only if** nothing references
`MuthurJsonContext.Default.IReadOnlyListAgentDto` after the test updates in Unit A; if anything still does,
leave it.

`Mapper.ToDto(this Agent a, …)` passes `a.ConductorStaffed` as the new trailing argument.

### 4. The read

`AgentService.ListAsync` is **unchanged** and still returns every agent — `CommsPanel`, `HeaderStats` and
`GetAsync` depend on that. Add beside it:

```csharp
/// <summary>
/// The roster as a reader wants it: standing agents only, with a count of the conductor-staffed sessions
/// left out, or everything when <paramref name="all"/> is true. Nothing is deleted or reclassified — this
/// is a view over the same rows <see cref="ListAsync"/> returns.
/// </summary>
public async Task<AgentRosterDto> RosterAsync(bool all, CancellationToken ct = default)
{
    var agents = await ListAsync(ct);
    if (all) return new AgentRosterDto(agents, 0);
    var standing = agents.Where(a => !a.ConductorStaffed).ToList();
    return new AgentRosterDto(standing, agents.Count - standing.Count);
}
```

Order is whatever `ListAsync` gives (by name); filtering preserves it.

### 5. The endpoint

`AgentEndpoints`, replacing the current `MapGet(Routes.Agents, …)`:

```csharp
app.MapGet(Routes.Agents, (bool? all, AgentService agents, CancellationToken ct) =>
    agents.RosterAsync(all ?? false, ct));
```

`Routes.Agents` is unchanged. The response body shape changes from a bare array to
`{"agents":[…],"conductorHidden":11}`. That is the point: an array has nowhere to put the count.

### 6. The CLI

`muthur agent list` gains `--all`, mirroring `muthur validate list`:

```csharp
var listAll = new Option<bool>("--all") { Description = "Include the sessions the conductor staffed." };
var list = new Command("list", "List standing agents with status, roles and open task counts.") { listAll };
list.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(
    Routes.Agents + (parse.GetValue(listAll) ? "?all=true" : ""), ct)));
```

No deserialization, no reshaping: the CLI stays a pure HTTP client and prints what the hub sent.

### 7. The rail

`AgentsPanel.razor`. Add a `Standing | All` filter group to the panel head, copying `StreamPanel`'s markup
and `Active` helper verbatim in shape. Default is **Standing**.

- **Panel head sub**, when Standing is selected and rows were hidden:
  `@_live live of @_agents.Count · @_hidden conductor hidden`
  When `_hidden` is 0, or when All is selected: `@_live live of @_agents.Count`, exactly as today.
  The separator is ` · ` (space, U+00B7, space), as elsewhere in this file.
- **`_live` and `_agents.Count` describe the view**, not the database: both count post-filter rows. A sub
  that heads a filtered list must describe the filtered list.
- **Empty state.** Today: `no agents registered · muthur agent register --name …`. When the list is empty
  *because* rows were hidden (`_agents.Count == 0 && _hidden > 0`), show instead:
  `@_hidden conductor sessions hidden · All shows them`.
  With `_hidden == 0` the existing line is unchanged.
- `LoadAsync` calls `Agents.RosterAsync(_all)` instead of `Agents.ListAsync()` and stores
  `_hidden = roster.ConductorHidden`. Everything else in `LoadAsync` — the `_working` lookup, the ordering,
  the `Rank`/`Modifier` helpers — is untouched.
- Selecting a filter re-runs the load. Follow `StreamPanel.Select`; if `StreamPanel` filters in memory and
  this one must re-query, call the panel's existing reload path rather than inventing one — read
  `Components/LivePanel.cs` and use what is there.
- `wwwroot/app.css` is **not** edited. `.filters` and `.filter` already exist and `.panel-head .filters`
  already has `margin-left: auto`.

### 8. Tests to update

Four call sites read the old array shape and must move to the envelope:

- `tests/Muthur.Server.Tests/AgentTests.cs:37`
- `tests/Muthur.Server.Tests/ConductorTests.cs:1056` and `:1181`
- `tests/Muthur.Server.Tests/RoleTests.cs:63`

Note that the two in `ConductorTests` look for a *conductor-staffed* agent, so after this change they must
request `Routes.Agents + "?all=true"` or they will find nothing. `AgentTests` and `RoleTests` register
through the API, so those rows are standing and the default read still finds them — change only the type.

## Units of work

One unit. The change is a single vertical slice of about a dozen small edits, and every part of it fails to
compile without the others; splitting it across worktrees would cost more in merge and verification friction
than it saves.

### Unit A — the conductor-staffed marker and the roster view
- **Files:**
  - modified: `src/Muthur.Core/Entities/Entities.cs`
  - created: `src/Muthur.Data/Migrations/<stamp>_AgentConductorStaffed.cs` (+ `.Designer.cs`), and
    `src/Muthur.Data/Migrations/MuthurDbModelSnapshot.cs` updated — all three by `dotnet ef`, not by hand
  - modified: `src/Muthur.Contracts/Agents.cs`, `src/Muthur.Contracts/MuthurJsonContext.cs`
  - modified: `src/Muthur.Server/Services/AgentService.cs`, `src/Muthur.Server/Services/Mapper.cs`
  - modified: `src/Muthur.Server/Services/ValidatorSessionLauncher.cs`,
    `src/Muthur.Server/Services/OrchestratorSessionLauncher.cs`
  - modified: `src/Muthur.Server/Api/AgentEndpoints.cs`
  - modified: `src/Muthur.Cli/Commands/AgentCommands.cs`
  - modified: `src/Muthur.Server/Components/Panels/AgentsPanel.razor`
  - modified: `tests/Muthur.Server.Tests/AgentTests.cs`, `ConductorTests.cs`, `RoleTests.cs`
  - created: tests per "New tests" below (put them in `tests/Muthur.Server.Tests/AgentTests.cs`, except the
    orchestrator one, which belongs beside its launcher's tests in `ConductorTests.cs`)
- **Does:** sections 1–8 above, exactly.
- **Depends on:** nothing.

The migration is generated, never hand-written:

```
dotnet ef migrations add AgentConductorStaffed -p src/Muthur.Data -s src/Muthur.Data -o Migrations
```

**New tests** (names are descriptions, not identifiers — follow the existing naming style in each file):

1. An agent registered through `POST /api/v1/agents/register` has `conductorStaffed: false` and appears in
   the default `GET /api/v1/agents`, with `conductorHidden` 0.
2. A validator session staffed through `ValidatorSessionLauncher.IdentityFor` (see the `RealLauncher()`
   helper already in `ConductorTests`) is absent from the default roster, is counted in `conductorHidden`,
   and is present with `?all=true`.
3. The same for `OrchestratorSessionLauncher.IdentityFor` — an `orchestrator-t-n` row is hidden too. This is
   the test that would have caught a name-prefix implementation, so write it even though it looks like a
   duplicate of 2.
4. **Sticky:** after a conductor-staffed session exists, re-registering that same name through the public
   HTTP endpoint (as that agent, presenting its token) leaves `conductorStaffed` true and the row still
   hidden from the default roster.
5. `?all=true` reports `conductorHidden` 0 and returns every row, standing and staffed.

- **Acceptance:** `dotnet build` and `dotnet test` both clean, with the five new tests present and passing,
  and no warnings (warnings are errors here).

## Verification

```
dotnet build
dotnet test
```

Both must be clean. Then, against a **scratch** hub only — never the live one:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t34
$env:MUTHUR_HOME = "<scratch dir>"; $env:MUTHUR_URL = "<scratch url>"
./artifacts/t34/muthur.exe agent list
./artifacts/t34/muthur.exe agent list --all
```

End to end, what a validator should see:

- `muthur agent list` returns an object, not an array: `{"agents":[…],"conductorHidden":N}`.
- On a hub where the conductor has staffed sessions, those names — `conductor-<role>-t-<n>` **and**
  `orchestrator-t-<n>` — are absent from `agents` and counted in `conductorHidden`.
- `muthur agent list --all` returns every row, each carrying `conductorStaffed`, and `conductorHidden` 0.
- `muthur agent whoami` still works and now shows `conductorStaffed` on the agent it returns.
- An existing hub's database upgrades in place: every row already in it reads `conductorStaffed: false` and
  stays visible. Nothing is deleted.

The rail is checkable without a browser, and this spec deliberately does **not** declare a need, because the
substance of the change is not eye-only. The dashboard prerenders on the server — `CommsPanel` relies on that
in a comment — so fetching the dashboard page returns the rail's markup:

- The panel head contains `· N conductor hidden` with the same N the CLI reported, and no
  `conductor-<role>-t-<n>` or `orchestrator-t-<n>` name appears in the rail's markup.
- With no standing agents and staffed sessions present, the empty line reads
  `N conductor sessions hidden · All shows them`.

The one residual an unattended session cannot exercise is **clicking the All button** and seeing the rail
re-render with every row. That is a single `@onclick` copied from `StreamPanel`, and the state it selects is
covered by the `?all=true` tests. It is recorded here as known-unexercised rather than hidden.

## Out of scope / follow-ups

- `AgentStatus` has no terminal value and nothing in the system ever writes an end-of-session fact, so
  "this session is finished" can still only be inferred from a cold heartbeat. Left alone deliberately; it is
  where option B would start if it is ever wanted.
- `HeaderStats` counts live agents across the whole roster, conductor sessions included. Accurate, but a
  founder reading "11 live" may now mean something different by it than the rail does. Worth a look if the
  numbers ever read as contradicting each other; not worth pre-empting.
