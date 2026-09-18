# T-14 — muthur doctor: find the hub that is silently misconfigured, not loudly broken

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the organization can ask the hub whether it can do its job, not merely whether it is
running. `muthur doctor` and a Doctor panel on `/operations` report one line per checked thing —
`ok`, `warn` or `fail`, each with a sentence saying what to do about it. `muthur doctor` exits 0 unless
something is `fail`, so a conductor can run it before it staffs anything.

The two failures that motivated this were both silent: a hub that inherited a shell without
`Muthur__DiscordBotToken` and failed every ingest poll while `muthur status` said "running", and a
project with no required validators auto-validating every task. Both become a line in this report.

## Context

Read before writing anything:

- `src/Muthur.Server/Services/IngestService.cs` — `IInboundSource`, `IngestService.SourcesAsync`, and
  `SaveCursorAsync`, which is where `LastError` is already remembered. `GitHubIssuesSource` and
  `DiscordChannelSource` are the two adapters.
- `src/Muthur.Server/Services/OutboundService.cs` — `IOutboundChannel` (`Validate` + `SendAsync`) and
  `ChannelException`, whose whole job is to carry a reason that names nothing of the target's address.
- `src/Muthur.Server/Services/OutboundChannels.cs` — `FileChannel`, `DiscordWebhookChannel`, `GitHubIssueChannel`.
- `src/Muthur.Server/Services/GitLander.cs` — how this codebase shells out to git through `IProcessRunner`.
- `src/Muthur.Server/Services/RoleService.cs`, `RoleLeases.cs` — role holds are leases; a lapsed hold means
  the role is open.
- `src/Muthur.Core/Entities/Entities.cs` — `Project`, `Role`, `RoleHold`, `IngestCursor`, `OutboundTarget`, `Agent`.
- `src/Muthur.Server/Components/Panels/HarnessPanel.razor` — the panel to copy: `@inherits LivePanel`,
  service injection, `IsRelevant`, `ClockInterval`, classes from `wwwroot/app.css` only.
- `src/Muthur.Cli/Commands/RoleCommands.cs`, the `brief --raw` action — the pattern for a CLI command that
  deserializes the response body before deciding what to return.
- `tests/Muthur.Server.Tests/HubFactory.cs` and `RoleTests.cs` — how server tests are written. `TestRepo`
  creates real temporary git repositories; `IProcessRunner` is **not** faked in `HubFactory`, so git checks
  run real git against a `TestRepo`.

Constraints that are not obvious from the code:

- **A secret's value never leaves this process.** Doctor reports presence or absence and nothing else —
  not the value, not a prefix, not the length, not in a log line, not in a ledger payload.
- **A target's address is often itself a credential** (a Discord webhook URL). No check may put an address
  in a `CheckDto`, an exception message, or a log. The subject of an outbound check is the target's *key*.
- Doctor performs no mutation and records no ledger event. It reads through `Ledger.ReadAsync`.
- Time comes from the injected `TimeProvider`.
- Warnings are errors; file-scoped namespaces; primary constructors.

## Non-goals

- **T-5** — making `project add` / `project set` say out loud that they left a project ungated, and marking
  it on `/projects`. Doctor reports the same condition; it does not change those commands or that page.
- Fixing anything doctor finds. Doctor reports; a human or another task acts.
- Any new secret, any new configuration source, any change to how secrets are read.
- Scheduling. Doctor runs when asked (CLI, endpoint, panel). No background worker, no alerting, no
  message on the bus when a check turns red.
- Checking harness CLIs, accounts or quota. That is `muthur harness tiers` and T-17.

## Design

### Contracts — new file `src/Muthur.Contracts/Doctor.cs`

```csharp
namespace Muthur.Contracts;

/// <summary>ok: nothing to do. warn: worth knowing, nothing is broken. fail: the hub cannot do this job.</summary>
public enum CheckStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("warn")] Warn,
    [JsonStringEnumMemberName("fail")] Fail,
}

/// <param name="Category">"secret", "ingest", "outbound", "project", "repo" or "role".</param>
/// <param name="Subject">What was checked, safe to show: a source, a target key, a project key, a role key.</param>
/// <param name="Detail">One sentence: what is true, and what to do about it.</param>
public sealed record CheckDto(
    string Category,
    string Subject,
    CheckStatus Status,
    string Detail,
    DateTimeOffset? LastSuccess = null);

public sealed record DoctorDto(
    DateTimeOffset At,
    bool Probed,
    int Ok,
    int Warn,
    int Fail,
    IReadOnlyList<CheckDto> Checks);
```

Register `CheckDto`, `IReadOnlyList<CheckDto>` and `DoctorDto` in `MuthurJsonContext`.

`CheckStatus` goes on the wire as `ok` / `warn` / `fail`, and the explicit `[JsonStringEnumMemberName]`
attributes above are how it gets there. `UseStringEnumConverter` does **not** apply the context's camelCase
property policy to enum members, which is why every enum in `src/Muthur.Contracts/Enums.cs` — `TaskState`,
`Verdict`, `LandMode`, `AgentStatus` — names each member the same way. Do not add a converter and do not
add a `ToWire` helper; `CheckStatus` has no separator to spell, so the attributes alone are enough.

Add to `src/Muthur.Contracts/Routes.cs`, beside `Status`:

```csharp
public const string Doctor = Api + "/doctor";
```

### Schema — `IngestCursor.LastSuccessAt`

"When did it last succeed" is not recoverable today: `UpdatedAt` is the last *attempt*. Add to
`src/Muthur.Core/Entities/Entities.cs`:

```csharp
/// <summary>When this source was last read without error. Null means never, since this column existed.</summary>
public DateTimeOffset? LastSuccessAt { get; set; }
```

Set it in `IngestService.SaveCursorAsync` when `error is null` (`row.LastSuccessAt = m.Now;`), leaving
every other line of that method alone. Migration:

```
dotnet ef migrations add IngestCursorLastSuccess -p src/Muthur.Data -s src/Muthur.Data -o Migrations
```

Existing rows get `NULL`, which doctor reports honestly as no successful poll recorded.

### The check seam — `src/Muthur.Server/Services/DoctorService.cs`

```csharp
/// <param name="Probe">Whether checks may touch the network. False is the cheap read of what the hub already knows.</param>
public sealed record DoctorContext(bool Probe, DateTimeOffset Now);

/// <summary>One aspect of the hub's health. Never throws: a check that cannot run returns a fail that says why.</summary>
public interface IDoctorCheck
{
    Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default);
}

public sealed class DoctorService(IEnumerable<IDoctorCheck> checks, TimeProvider clock)
{
    private static readonly string[] Order = ["secret", "ingest", "outbound", "project", "repo", "role"];
    public async Task<DoctorDto> RunAsync(bool probe, CancellationToken ct = default) { /* below */ }
}
```

`RunAsync`:

- `now = clock.GetUtcNow()`, `context = new DoctorContext(probe, now)`.
- Runs every check. A check that throws anyway yields one
  `new CheckDto("doctor", <the check's type name>, CheckStatus.Fail, $"This check itself failed: {ex.Message}")`
  rather than taking the whole report down. Catch `Exception`; let `OperationCanceledException` through.
- Concatenates the results and sorts by `(Array.IndexOf(Order, Category), Subject, Detail)` with
  `StringComparer.Ordinal`. A category not in `Order` sorts last — `IndexOf` returns -1, so map -1 to
  `Order.Length`.
- Counts `Ok`/`Warn`/`Fail` and returns `new DoctorDto(now, probe, ok, warn, fail, sorted)`.

Register in `src/Muthur.Server/Infrastructure/Startup.cs`, in `AddMuthur`, beside the other services —
`Program.cs` registers none. `DoctorService` and every `IDoctorCheck` implementation are singletons, like
every neighbour there.

### The checks

Each is its own file in `src/Muthur.Server/Services/`, namespace `Muthur.Server.Services`.

#### `DoctorIngestCheck.cs` — categories `secret` and `ingest`

Constructor: `(Ledger ledger, IEnumerable<IInboundSource> sources, MuthurOptions options)`.

Reads every project's `IngestSources` and every `IngestCursor` in one `Ledger.ReadAsync`.

Per configured source string `s` the subject is `s` itself — a repository name or a channel id is not a
secret. The first condition that matches wins; each source yields exactly one `ingest` check:

| Condition | Status | Detail |
|---|---|---|
| `s` has no `scheme:location` shape | `Fail` | `Not a source: expected scheme:location, e.g. github:owner/repo.` |
| no adapter with that `Scheme` | `Fail` | `No ingest adapter for '<scheme>:'. Known: <comma-separated schemes>.` |
| cursor row has `LastError` | `Fail` | `Last poll failed: <LastError>` |
| no cursor row, or `LastSuccessAt` is null | `Warn` | `No successful poll recorded. Run: muthur inbound poll` |
| probing and `ProbeAsync` threw | `Fail` | the exception's `Message`, verbatim |
| otherwise | `Ok` | `Reads.` |

`LastSuccess` on the DTO is the cursor's `LastSuccessAt` whenever a row exists, whatever the status.

Secrets: emit a `secret` check only for a secret some configured source actually needs. Today that is
exactly one — `Muthur__DiscordBotToken`, needed when any configured source's scheme is `discord`. The
subject is the literal `Muthur__DiscordBotToken`:

| Condition | Status | Detail |
|---|---|---|
| set (non-empty) | `Ok` | `Set in this hub's environment.` |
| not set | `Fail` | `Not set in this hub's environment, so discord: ingest cannot authenticate. Set Muthur__DiscordBotToken and restart the hub — a hub started from a shell opened before the variable was set does not see it.` |

No `discord:` source configured → no secret check at all.

#### `DoctorOutboundCheck.cs` — category `outbound`

Constructor: `(Ledger ledger, IEnumerable<IOutboundChannel> channels)`. The subject is the target's `Key`.
First match wins; one check per target:

| Condition | Status | Detail |
|---|---|---|
| no channel named `target.Channel` | `Fail` | `No channel named '<channel>'. Known: <comma-separated names>.` |
| `channel.Validate(address, "")` threw `MuthurException` | `Fail` | the exception's `Message` |
| probing and `ProbeAsync` threw `ChannelException` | `Fail` | `<channel> is not reachable: <message>` |
| otherwise | `Ok` | `<channel> target, address accepted.` and, when `RequiresFounderApproval`, ` Every message needs the founder.` appended |

No branch of this check may include `target.Address`.

#### `DoctorProjectCheck.cs` — category `project`

Constructor: `(Ledger ledger)`. The subject is the project key. A project yields up to two checks.

1. The validation gate:
   - `RequiredValidators` empty → `Warn`, `No required validators: every task in this project goes from implemented straight to validated with nobody looking. muthur project set <key> --validator <role>`
   - a required key that is not a defined `Role` → `Fail`, `Requires validator '<key>', which is not a defined role. muthur role define <key> --founder`
   - a required key whose `Role.IsValidator` is false → `Fail`, `Requires validator '<key>', a role that may not give verdicts. muthur role define <key> --validator true --founder`
   - otherwise → `Ok`, `Gated by <comma-separated required validators>.`
2. `muthur.project.json` at `Path.Combine(RepoPath, "muthur.project.json")`, read with `JsonDocument`:
   - the repository directory does not exist → emit nothing here; `DoctorRepoCheck` says so once.
   - file missing → `Warn`, `No muthur.project.json in <RepoPath>: agents have no build, test or run commands for this project.`
   - present but not valid JSON → `Fail`, `muthur.project.json is not valid JSON: <JsonException.Message>`
   - present, valid, missing any of `key`, `build`, `test` (absent, or an empty or whitespace string) →
     `Warn`, `muthur.project.json has no <comma-separated missing fields>.`
   - present with a `key` that is not the project's key → `Warn`, `muthur.project.json says key '<file key>'; this project is '<project key>'.`
   - otherwise → `Ok`, `muthur.project.json names how to build, test and run it.`

   `run` and `notes` are optional and are not checked.

#### `DoctorRepoCheck.cs` — category `repo`

Constructor: `(Ledger ledger, IProcessRunner processes)`. The subject is the project key. One check per
project; the first condition that matches wins. Each git call runs in `project.RepoPath` with a 30-second
timeout:

| Condition | Status | Detail |
|---|---|---|
| `!Directory.Exists(RepoPath)` | `Fail` | `<RepoPath> does not exist; nothing in this project can be built or landed.` |
| `git rev-parse --git-dir` not ok | `Fail` | `<RepoPath> is not a git repository.` |
| `git rev-parse --verify --quiet refs/heads/<DefaultBranch>` not ok | `Fail` | `Default branch '<DefaultBranch>' does not exist; MUTHUR has nothing to land onto.` |
| `git status --porcelain --untracked-files=no` produced output | `Warn` | `Working tree is dirty; MUTHUR refuses to merge in a checkout with uncommitted changes.` |
| `git rev-parse --abbrev-ref HEAD` is not `<DefaultBranch>` | `Warn` | `Checked out on '<branch>', not '<DefaultBranch>'. Normal while a task is in flight.` |
| otherwise | `Ok` | `Clean checkout of '<DefaultBranch>'.` |

#### `DoctorRoleCheck.cs` — category `role`

Constructor: `(Ledger ledger, MuthurOptions options)`. The subject is the role key. One check per defined
role. Needs, in one read: the roles, the unexpired `RoleHold`s with their `Agent`, each project's
`RequiredValidators`, and the number of tasks in `Validating` that have a `Pending` `TaskValidation` for
that key.

An agent is *stale* when `now - agent.LastHeartbeat > TimeSpan.FromSeconds(options.AgentStaleSeconds)`.

| Condition | Status | Detail |
|---|---|---|
| held by a live agent | `Ok` | `Held by '<agent>'.` |
| held by a stale agent | `Warn` | `Held by '<agent>', last seen <n> ago; the hold lapses at <LeaseExpires:u> unless it speaks.` |
| unheld, and `n > 0` tasks are waiting on it | `Warn` | `Unheld, and <n> task(s) are waiting on it.` |
| unheld, required by at least one project, nothing waiting | `Ok` | `Open. Required by <comma-separated project keys>.` |
| unheld, required by nothing | `Ok` | `Open.` |

`LastSuccess` stays null for role checks.

### Probes — the two new interface members

```csharp
// IInboundSource, in IngestService.cs
/// <summary>Can this source be read right now? Ingests nothing. Throws <see cref="InvalidOperationException"/> with a reason safe to show the founder.</summary>
Task ProbeAsync(string location, CancellationToken ct = default);
```

- `GitHubIssuesSource`: `gh api --method GET repos/<location>`, 60-second timeout. Not ok →
  `throw new InvalidOperationException($"gh api failed: {result.Message}")`.
- `DiscordChannelSource`: no token → the message it already throws from `FetchAsync`, verbatim.
  Otherwise `GET https://discord.com/api/v10/channels/<channel>` with the same `Bot <token>` header.
  Not successful →
  `throw new InvalidOperationException($"Discord returned {(int)response.StatusCode} for channel {channel}.")`.
- `FakeInboundSource` (tests): throws `new InvalidOperationException(FailWith)` when `FailWith` is set,
  and completes otherwise.

```csharp
// IOutboundChannel, in OutboundService.cs
/// <summary>Is the target reachable and its address still valid? Sends nothing. Throws <see cref="ChannelException"/>, whose message names nothing of the address.</summary>
Task ProbeAsync(string address, CancellationToken ct = default);
```

- `FileChannel`: the probe must establish what a send actually needs — that the **address itself** can be
  appended to as a file. Ensuring its parent directory exists does not, and a target whose address is an
  existing *directory* passes that check and then fails delivery with access denied. So:
  1. `Directory.CreateDirectory(Path.GetDirectoryName(address)!)` as before, for the parent.
  2. `if (Directory.Exists(address)) throw new ChannelException("the address is a directory, not a file")`.
  3. Otherwise prove it is writable the way a send does: note whether `File.Exists(address)`, open
     `new FileStream(address, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)`, dispose it without
     writing a byte, and — if it did not exist before — delete it again, swallowing any failure to delete
     (another process may legitimately have created it in between). A probe leaves no message and should
     leave no file.

  On `IOException`, `UnauthorizedAccessException`, `NotSupportedException` or `ArgumentException` →
  `throw new ChannelException("the address cannot be written")`.

  `FileChannel.Validate` also gains the directory check, so such a target is refused at `muthur out target`
  rather than accepted and then probed as healthy:
  `if (Directory.Exists(address)) throw Fail.Rule("invalid_address", "A file target needs a path to a file, not a directory.")`
- `DiscordWebhookChannel`: `GET` the webhook URL — a webhook URL answers with the webhook's own metadata
  and posts nothing. Not successful →
  `throw new ChannelException($"Discord answered HTTP {(int)response.StatusCode}")`.
- `GitHubIssueChannel`: `gh api repos/<repo>/issues/<number>` using the existing `Parse(address)`,
  60-second timeout. Not ok →
  `throw new ChannelException($"the GitHub CLI failed (exit {result.ExitCode}); is it logged in? gh auth status")`.

### Endpoint — `src/Muthur.Server/Api/SystemEndpoints.cs`

```csharp
app.MapGet(Routes.Doctor, (bool? probe, DoctorService doctor, CancellationToken ct) => doctor.RunAsync(probe ?? true, ct));
```

No authentication, exactly like `Routes.Status`: nothing it returns is a secret.

### CLI — `muthur doctor`

In `src/Muthur.Cli/Commands/SystemCommands.cs`, beside `status`:

```
muthur doctor [--offline] [--pretty]
```

- Description: `Check whether this hub can do its job: ingest, outbound, projects, repositories, roles. Exit 1 if anything failed.`
- `--offline`: `Skip the checks that touch the network; report only what the hub already knows.` Sends `?probe=false`.
- Prints the body exactly as `Output.Emit` does.
- **Timeout.** Probing is slower than any other command in this CLI: the hub allows each `gh` call 60
  seconds and each git call 30, and a hub with several sources can legitimately spend minutes. `HubClient`'s
  default is 30 seconds, and a client-side timeout comes back as `ApiResult(0, ...)` — which the CLI reports
  as `not_running`, exit 4. Telling a founder their hub is down because a check was slow is exactly the
  misleading answer this task exists to remove. So: `HubClient.For(parse, TimeSpan.FromSeconds(180))` when
  probing, and the default when `--offline` is passed, where nothing touches the network.
- Exit code: whatever `Output.Emit` returned when the result is not a success or that code is not
  `ExitCodes.Ok`; otherwise `ExitCodes.Error` (1) when the deserialized `DoctorDto.Fail > 0`, and
  `ExitCodes.Ok` otherwise. A `warn` never changes the exit code.

### Dashboard — `src/Muthur.Server/Components/Panels/DoctorPanel.razor`

`@inherits LivePanel`, `@inject DoctorService Doctor`, `@inject TimeProvider Clock`.

- `LoadAsync` calls `Doctor.RunAsync(probe: false)` — the panel refreshes on its own and must never poll
  Discord or GitHub on a timer.
- `ClockInterval => TimeSpan.FromSeconds(60)`.
- `IsRelevant`: events whose `Type` starts (ordinal) with `ingest.`, `role.`, `project.`, `outbound.` or `task.`.
- Head: title `Doctor`; sub reads `<fail> fail · <warn> warn`, or `all ok` when both are zero.
- Body: a `.section-label` per category, then one `.role-row` per check — a `.tag` carrying the status with
  the matching class (`check-ok` / `check-warn` / `check-fail`), the subject, and the detail in a
  `.role-holder`. Where `CheckDto.LastSuccess` is not null, the `.role-holder` ends with
  ` · last read <age>`, using `Format.Age(lastSuccess, now)` from `Components/Shared/Format.cs` — this is
  what the injected `TimeProvider` is for, and "when did it last succeed" is half of what the task asked of
  the ingest check. It must not be shown as a bare timestamp.
- `.empty` when there are no checks, reading `nothing to check`.
- A `Re-check` `.btn` inside a `.btn-row` that awaits `Doctor.RunAsync(probe: true)`, assigns the result and
  calls `StateHasChanged()`. It must not go through `ReloadNowAsync`, which would immediately discard the
  probed result.

Add to `src/Muthur.Server/wwwroot/app.css`, in the tag block beside `.tag-held` and `.tag-open`:

```css
.check-ok { background: var(--accent-bg); color: var(--accent); }
.check-warn { background: var(--amber-bg); color: var(--amber); }
.check-fail { background: var(--red-bg); color: var(--red); }
```

Place `<DoctorPanel />` in `src/Muthur.Server/Components/Pages/Operations.razor`, in the left aside,
below `<HarnessPanel />`.

## Units of work

### Unit A — contracts, schema, and the doctor seam
- **Files:** new `src/Muthur.Contracts/Doctor.cs`; modified `src/Muthur.Contracts/Routes.cs`,
  `src/Muthur.Contracts/MuthurJsonContext.cs`, `src/Muthur.Core/Entities/Entities.cs`,
  `src/Muthur.Server/Services/IngestService.cs` (`SaveCursorAsync` only), a new migration under
  `src/Muthur.Data/Migrations/`; new `src/Muthur.Server/Services/DoctorService.cs`; modified
  `src/Muthur.Server/Api/SystemEndpoints.cs` and `src/Muthur.Server/Infrastructure/Startup.cs`.
- **Does:** the DTOs, the route, `LastSuccessAt` and its migration, `IDoctorCheck` / `DoctorContext` /
  `DoctorService` with the ordering, counting and per-check exception containment described above, the
  endpoint, and DI registration. Registers no `IDoctorCheck` implementations.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus a new
  `tests/Muthur.Server.Tests/DoctorTests.cs` asserting that `GET /api/v1/doctor` returns 200 with zero
  checks and `ok`/`warn`/`fail` all zero, that `?probe=false` reports `probed: false`, and that a `CheckDto`
  serialized through `MuthurJsonContext.Default` carries `"status":"ok"` — lowercase, the form the CLI and
  the **Verification** section below both read.

### Unit B — ingest, secret and outbound checks, and the two probes
- **Files:** new `src/Muthur.Server/Services/DoctorIngestCheck.cs` and `DoctorOutboundCheck.cs`; modified
  `src/Muthur.Server/Services/IngestService.cs` (interface member and both adapters),
  `OutboundService.cs` (interface member), `OutboundChannels.cs` (three adapters),
  `tests/Muthur.Server.Tests/HubFactory.cs` (`FakeInboundSource.ProbeAsync`), and `Infrastructure/Startup.cs` registration.
- **Does:** every `secret`, `ingest` and `outbound` row in the tables above, and `ProbeAsync` on both seams.
- **Depends on:** Unit A.
- **Acceptance:** `dotnet build` and `dotnet test` clean, with tests covering: a source with no adapter
  (`fail`); a source whose last poll failed (`fail`, the stored error in `Detail`); a source that has never
  polled (`warn`); a `discord:` source with no token configured (a `secret` `fail` whose report contains
  nothing but the variable's name); and an outbound target whose channel does not exist (`fail`). At least
  one test asserts that no `CheckDto` in a report contains a configured target's address.
  **And the case that failed validation:** a `file` target whose address is an existing directory is refused
  by `out target` with `invalid_address`, and a target whose address has become a directory since it was
  added probes as `fail`, not `ok`. Doctor reporting healthy about a destination a send cannot write to is
  the exact silent misconfiguration this task exists to surface — produced by this task.

### Unit C — project, repository and role checks
- **Files:** new `src/Muthur.Server/Services/DoctorProjectCheck.cs`, `DoctorRepoCheck.cs` and
  `DoctorRoleCheck.cs`; `Infrastructure/Startup.cs` registration.
- **Does:** every `project`, `repo` and `role` row in the tables above.
- **Depends on:** Unit A.
- **Acceptance:** `dotnet build` and `dotnet test` clean, with tests covering: a project with no required
  validators (`warn`); a project requiring a role that is not defined (`fail`); a project whose `RepoPath`
  does not exist (`fail`, and no `muthur.project.json` row for it); a `TestRepo` with a dirty working tree
  (`warn`) and a clean one on its default branch (`ok`); a role held by a live agent (`ok`); and a role that
  lapsed while a task waits on it (`warn` naming the count).

### Unit D — `muthur doctor`
- **Files:** modified `src/Muthur.Cli/Commands/SystemCommands.cs`.
- **Does:** the command, `--offline`, and the exit-code rule.
- **Depends on:** Unit A.
- **Acceptance:** `dotnet build` and `dotnet test` clean; verified by hand against an installed build per
  **Verification**.

### Unit E — the Doctor panel
- **Files:** new `src/Muthur.Server/Components/Panels/DoctorPanel.razor`; modified
  `src/Muthur.Server/Components/Pages/Operations.razor` and `src/Muthur.Server/wwwroot/app.css`.
- **Does:** the panel and its three status classes.
- **Depends on:** Unit A (it compiles against `DoctorService` and the DTOs).
- **Acceptance:** `dotnet build` and `dotnet test` clean; verified by eye per **Verification**.

## Verification

```
dotnet build
dotnet test
```

Both clean — warnings are errors, so a warning is a failure.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t14
$env:MUTHUR_HOME = "$PWD/artifacts/t14-home"; $env:MUTHUR_URL = "http://127.0.0.1:7431"
./artifacts/t14/muthur.exe up
./artifacts/t14/muthur.exe project add scratch --repo <a real git checkout> --founder   # no --validator
./artifacts/t14/muthur.exe doctor --pretty
```

What a validator should see:

- A `project` line for `scratch` at `warn` saying it has no required validators — the silent condition that
  motivated this task, now said out loud.
- A `repo` line for `scratch` reflecting the truth of that checkout: clean or dirty, on or off its default branch.
- `echo $LASTEXITCODE` is `0` while nothing is `fail`. Point the project at a directory that does not exist
  (`project set scratch --repo C:\nope --founder`) and run `doctor` again: a `repo` `fail`, and the exit code
  is `1`.
- `doctor --offline` returns the same report without touching the network, with `"probed": false`.
- Nowhere in the output — JSON, panel or hub log — is a secret's value or an outbound target's address.

### The dashboard, without a browser

**No browser is required to validate this task, and none should be used.** The dashboard is Blazor Server
and prerenders on the server, so the panel and its state are in the HTML that `GET /operations` returns:

```
(Invoke-WebRequest "$env:MUTHUR_URL/operations" -UseBasicParsing).Content |
    Select-String -Pattern 'Doctor', 'Re-check', 'check-ok', 'check-warn', 'check-fail'
```

With the `scratch` project above configured, that response contains `Doctor`, `Re-check`, and at least the
`check-warn` and `check-fail` classes — the same rows the CLI printed, colour-coded by class. On a hub with
no projects and no roles it contains `nothing to check` instead.

The `Re-check` button is deliberately **not** part of this verification. What it does is
`DoctorService.RunAsync(probe: true)` — the identical call `muthur doctor` makes without `--offline`, which
the steps above already exercise against the real hub. Clicking it would re-test the same service call
through a slower seam. That it is wired to that call, and not to `ReloadNowAsync`, is held by
`DashboardOperationsTests` in the server suite.

This section is written this way because the first version of it required a live browser, and a conductor
started five validator sessions in twenty-eight minutes that each correctly reported blocked and released
the role. A spec that cannot be validated by the sessions this organization actually starts is a defect in
the spec.

## Out of scope / follow-ups

- **T-5** remains: `project add` / `project set` saying so at the moment the gate is left empty, and the
  `/projects` page marking it.
- A conductor that runs `muthur doctor` before it staffs anything and refuses to staff while something is
  `fail`. This task makes that possible; it does not do it. File it.
- Checks for the harness catalog — which CLIs are actually on PATH. Related to T-15, not this.
- **An overall budget for a probing run, server side.** The CLI timeout above bounds what the *caller*
  waits; nothing bounds what the *hub* spends. A hub with many sources, each allowed 60 seconds, can hold a
  request open for minutes, and the dashboard's `Re-check` button has no timeout at all. The honest fix is a
  budget in `DoctorService` — probes race a deadline, and a check that does not answer in time reports
  `warn`, "did not answer within <n>s", rather than making the whole report wait. Out of scope here because
  it changes the `IDoctorCheck` contract; file it.
- `LastSuccessAt` is null for every source polled before this migration. It fills in on the first successful
  poll and needs no backfill.
