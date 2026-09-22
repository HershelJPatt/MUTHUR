# MU/TH/UR

The control plane for an organization of coding agents. One hub per trust boundary (your company, your personal
projects); any number of repositories per hub; any agent harness that can run a shell command.

MUTHUR is a ledger and a gate, not an agent. It makes **zero model calls**. Agents (Claude Code, Codex, local models)
do the thinking; MUTHUR decides who owns what, when work counts as done, who may merge, and what may leave the machine.

```
 terminal panes: orchestrators, validators, on-calls            you
 ┌──────────────┐ ┌──────────────┐ ┌──────────────┐        ┌───────────┐
 │ claude       │ │ codex        │ │ claude       │  ...   │ dashboard │
 │  └ workers   │ │  └ workers   │ │ (validator)  │        └─────┬─────┘
 └──────┬───────┘ └──────┬───────┘ └──────┬───────┘              │
        └────────── muthur.exe (AOT, ~60 ms) ──────────┐         │
                                                       ▼         ▼
                                  Muthur.Server  ·  http://127.0.0.1:7420
                     tasks · leases · roles · validation · landing · bus · ingest · outbound gate
                                                       ▼
                                  %LOCALAPPDATA%\Muthur\muthur.db  (append-only ledger inside)
```

Design, rationale and milestones: [docs/PLAN.md](docs/PLAN.md).

## Install

Requires the .NET 10 SDK, git, and for the native CLI the Visual Studio "Desktop development with C++" workload.

```powershell
./scripts/install.ps1 -AddToPath        # muthur.exe + server + kit → %LOCALAPPDATA%\Muthur\bin
muthur up                               # start the hub; dashboard at http://127.0.0.1:7420
./scripts/install.ps1 -RestartRunning   # later: upgrade the running hub in place
```

The hub is a local process: `muthur up` / `muthur down` / `muthur status`. Everything it knows is in the database, so it
can be off for hours; on start it releases lapsed leases and catches up on ingest.

## Set up a repository

```powershell
cd C:\src\my-repo
muthur project add my-repo --repo . --founder                  # personal repo: the hub merges validated work itself
muthur project add monorepo --repo . --land pr --founder       # company repo: the hub opens a pull request, a human merges
muthur kit install --harness claude                            # or: codex, generic
```

`kit install` writes the agent procedures for that harness (`.claude/agents` + skills, or an `AGENTS.md` section and
`.muthur/procedures/`), `specs/_TEMPLATE.md`, starter role briefs under `briefs/`, and `muthur.project.json` — fill in its
build/test/run commands; agents read it. Re-running the install never overwrites a brief you have edited, and it prints the
`muthur role define` command for each one — a brief does nothing until you create its role.

Applying protected installed agent definitions requires explicit founder approval for the reviewed change
and an attended session. `kit install` is not a sanctioned unattended bypass. See the
[protected agent definitions workflow](kit/core/orchestrate.md#protected-agent-definitions) for approval and task routing.

## The organization

| Who | Runs on | Does | Never |
|---|---|---|---|
| **Founder** (you) | dashboard, `--founder` | defines projects, roles, briefs, outbound targets; answers questions | — |
| **Mastermind orchestrator** | top-tier model | claims a task, becomes the expert, writes the frozen spec, delegates, reviews, asks MUTHUR to land | writes product code, merges, pushes |
| **Implementer / specialist** (worker) | cheaper tier, any vendor | implements one unit of a frozen spec in its own worktree, reports | talks to the hub, merges, pushes |
| **Validator on call** | top-tier model | independently exercises implemented work end to end, passes or fails it with evidence | fixes what it finds, validates its own work |
| **Other on-calls** | any | hold a role (comms, observability…), wait on the inbox, triage inbound | decide founder-level questions |

Start an agent session with its identity in the environment, then tell it what to be:

```powershell
$env:MUTHUR_AGENT = 'top-left'; claude        # then: /muthur-orchestrate next     (or: /muthur-validate win-validator)
$env:MUTHUR_AGENT = 'corner';   codex         # then: "orchestrate the next MUTHUR task"
```

An agent registers once (`muthur agent register --name top-left --harness claude --model fable --tier mastermind`);
the token is stored under `%LOCALAPPDATA%\Muthur\agents\`, never echoed.

Harness identifiers have surrounding whitespace trimmed and case preserved; unknown identifiers are allowed.
Match kit/catalog spelling exactly for doctor checks. Existing rows are unchanged: to correct casing previously
folded to lowercase, re-register the existing agent as itself or with `--founder`.

## A task's life

```
backlog ─claim→ in_progress ─implemented→ validating ─every validator yes→ validated ─land→ done
                   ▲  │ ask founder → blocked           │ any "no" (with evidence)
                   └──┴─────────────────────────────────┘
```

```bash
muthur task add "Export invoices as CSV" --priority 1
muthur task claim T-12                      # exit 3 if someone else has it; a claim is a lease renewed by any hub call
muthur task spec T-12 specs/T-12.md         # the frozen spec, committed on the task branch
muthur worker run --tier implementer --spec specs/T-12.md --unit "Unit A" --task T-12     # any harness; or native subagents
muthur task implemented T-12 --branch task/T-12-csv-export      # → validators are messaged
muthur validate pass T-12 --as win-validator --evidence e.md    # by an agent holding that role, not the owner
muthur task land T-12                       # the HUB merges (or opens the PR). Conflict → back to the owner, exit 3
```

Rules the hub enforces (exit 2 names the rule): no `implemented` without a spec and a real branch; no `done` except
through `validated`; validators must hold the role and may not be the task's owner; only the owner or founder lands;
a dirty default-branch checkout refuses the merge instead of clobbering it.

## Roles, the bus, and questions for you

```bash
muthur role define win-validator --brief-file briefs/win.md --founder     # a brief is the role's job description
muthur role take win-validator            # one holder per role; a lease — go quiet and you lose it
muthur msg inbox --wait 600               # blocks server-side until something arrives: idle agents cost nothing
muthur msg send --to role:comms-oncall "user reports a crash" --blocking
muthur ask "Monthly or annual billing first?" --task T-12 --option monthly --option annual    # task → blocked
```

Questions and messages for you appear on the dashboard's **Needs you** page (one-click answers); answering unblocks the
task and wakes the agent.

## The conductor

A task that reaches `validating` waits for a validator to take the role. The conductor starts one, so a task that
passes needs nobody awake:

```bash
muthur conductor on --founder      # off by default: upgrading a hub never starts spending on its own
muthur conductor status            # running sessions, and the limits it staffs within
```

Each pass it looks for a task in `validating` whose required role nobody holds, and starts a session that takes
that role — an ordinary registered agent with an ordinary token, no more authority than a terminal you open
yourself. It prefers a harness *different* from the one that built the task, so one vendor checks another's work.
It never staffs a `blocked` task, never contends for a held role, and after three failed verdicts on one task it
stops and asks you instead. If it cannot *start* a session at all — no candidate, a harness missing — it gives up on
that task after the same three attempts and tells you why, then quietly tries once more every half hour: the cause is
usually something you fixed outside the hub, and you should not have to remember an incantation to resume.

It also **lands the work nobody is left to land**. A task that passed validation after its owner's session had
ended used to sit in `validated` for ever: nothing sweeps that state and nothing plans it. That is the ordinary
ending rather than an edge, because a session is capped at forty-five minutes and validation takes ten to thirty.
So each pass the hub merges every validated task whose owner has gone quiet and which it is running no session
for. A live owner still lands its own work — this is only the backstop — and landing is not a session, so it takes
no slot and every such task lands in one pass.

**What the hub checks before it merges: that every required validator said yes, and that the branch still merges
cleanly. Nothing else.** The hub has no test runner and must not grow one, so the validator's pass is the whole
gate — which is a real change, because until now a human looked at the default branch before work reached it and
now nobody need have. A merge conflict sends the task back to its owner; an environment that refuses (a dirty
checkout, a branch that is gone, `gh` missing) leaves it `validated` and is retried once every
`Muthur:ConductorStallProbeMinutes` rather than every pass — half-open, like a wedged validator, because the cause
is something you fix outside the hub and a new commit on the branch resumes it at once. Either way you are told
once per branch head, not once a minute, and `conductor.landed` in the ledger is what tells a hub land from a
human one.

Workers have their hub credentials scrubbed because they have no authority; a validator is given an identity
because its verdict decides whether work ships. Those are two launchers on purpose.

## Any model, any vendor

`%LOCALAPPDATA%\Muthur\harnesses.json` maps tiers to an ordered list of `{harness, model, account}`. `muthur worker run`
staffs a unit with the first candidate whose account is not out of quota, in its own git worktree, with no hub identity,
and records which model did the work. Subscriptions work because MUTHUR only ever launches the vendors' own CLIs.

```bash
muthur harness tiers                          # who staffs what, which accounts are limited
muthur agent limited --minutes 120            # "my account hit its limit" → its account leaves the rotation
muthur worker run --tier utility --spec …     # local models (Ollama via `codex --oss`) belong in the utility tier; shipped with "enabled": false
```

Adapters: `claude` (print mode, permissions via a generated settings file), `codex` (exec, workspace-write sandbox; the
launcher commits for it because the sandbox keeps `.git` read-only), `codex-oss` (Ollama). A new harness is one class in `Muthur.Launch`.

## What comes in, what goes out

```bash
muthur project set my-repo --ingest github:owner/repo --founder     # polled with a cursor: nothing is missed while the hub is off
muthur project set my-repo --ingest discord:<guild>/<channel> --founder    # needs Muthur__DiscordBotToken; only what a person typed
muthur inbound add "Customer: export is broken" --source email --external-id msg-42      # push from any script
muthur inbound claim I-7 --as-task                                  # one winner; becomes a backlog task linking to its source

muthur out target news --channel discord-webhook --address https://discord.com/api/webhooks/… --founder
muthur out draft --target news --file announce.md      # unknown target → refused. A credential → drafted, flagged, only you can send it
muthur out show O-3 ; muthur out review O-3 --approve --sha <sha256>      # a DIFFERENT agent, naming the bytes it read
muthur out send O-3                                    # the hub re-checks hash + secrets and transmits
```

`--founder-approval` targets also need you. Set `Muthur:RequireCrossProviderReview=true` to require the reviewer to run
on a different vendor than the author.

### Recurring local checks (census)

`census:<check-key>` runs a founder-configured deterministic check on the existing ingest schedule
(also on hub startup and `muthur inbound poll`). No check is enabled by default, and a sweep launches no
model or agent session. Findings enter inbound for comms on-call triage and normal claim/convert handling.

Census execution currently requires Windows. On other platforms execution and doctor probes report
that limitation without launching a check; the other ingest adapters remain available as before.

Configure the hub locally, for example in its `appsettings.json`, then restart it:

```json
{
  "Muthur": {
    "CensusChecks": {
      "backup-status": {
        "FileName": "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
        "Arguments": ["-NoProfile", "-NonInteractive", "-File", "C:\\checks\\backup-status.ps1"],
        "WorkingDirectory": "C:\\checks",
        "TimeoutSeconds": 30
      }
    }
  }
}
```

Environment settings also work: `Muthur__CensusChecks__backup-status__FileName`,
`Muthur__CensusChecks__backup-status__Arguments__0`, and so on. Configuration changes take effect on
restart. Keys must match `[a-z0-9][a-z0-9-]{0,63}` exactly. The executable must be nonblank, the working
directory must be an existing absolute directory, arguments must be a list of strings, and the timeout
must be 1–60 seconds (default 30). Arguments are literal; scripts require an explicit interpreter such
as `pwsh -File` with the script as an argument. Source locations contain only the key, never commands.

Register the source as founder (this replaces the project's ingest list; include any other sources to retain):

```powershell
muthur project set my-repo --ingest census:backup-status --founder
muthur inbound poll --founder
muthur inbound sources
muthur doctor
```

A tiny read-only `backup-status.ps1` could inspect an operator-maintained status file:

```powershell
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ([IO.File]::ReadAllText('C:\checks\backup-state.txt').Trim() -eq 'failed') {
    [Console]::Out.Write('[{"id":"backup-last-run","title":"Last backup failed","body":"Inspect the local backup log.","url":"https://example.com/runbook/backup"}]')
} else {
    [Console]::Out.Write('[]')
}
```

stdout must be one complete JSON array of **all currently active** findings, including `[]` when none
are active. Each finding requires a case-sensitive stable `id` matching
`[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}` and a nonblank `title` of at most 500 characters. Optional `body` is a
string of at most 16,000 characters (default empty); optional `url` is an absolute HTTP(S) string of at
most 2,048 characters (default absent). Nulls, duplicate or unknown fields, duplicate IDs, malformed JSON,
and more than 1,000 findings reject the entire snapshot. Findings are inert text, never commands or instructions.

Keep IDs stable when editing scripts or changing finding text. An active ID retains its incident and
original inbound text, including after conversion or dismissal. A successful snapshot omitting an ID
ends its recurrence tracking; this does not close inbound or tasks. Reappearance creates new inbound
with a fresh sequence. Failure, downtime, and removing/readding the same check key do not resolve an
incident. Cursors survive restart, and different sources have independent identities. Renaming a key
creates a new source and may repeat findings. Incidents entirely between polls cannot be observed.

Nonzero exit, startup failure, timeout (including stream draining), excessive output (1,048,576 characters
per stream), invalid configuration, invalid cursor, or invalid JSON fails the poll without partial ingest
or advancing the cursor. `inbound sources` exposes `lastError`; `doctor` reports the failed ingest until a
successful poll, including `[]`, clears it. Doctor probes validate configuration and executable resolution
without running scripts. Errors expose only fixed categories and exit codes; process output, stderr, and
arguments are not logged. Successful polls atomically record `census.completed` with source, finding
count and newly created count, including zero; failures use the existing `ingest.failing`/`ingest.recovered`
events. Inspect scripts locally when a safe category is insufficient to diagnose a failure.

These are **trusted operator programs running as the hub user, not a sandbox**. Scripts must be read-only,
must not invoke models, and must not perform outbound actions. `MUTHUR_AGENT`, `MUTHUR_TOKEN`, and
`Muthur__DiscordBotToken` are removed from their environment; this is not isolation from other user data
or credentials. Test against a scratch hub, never the live hub. Installed verification is available as
`pwsh -NoProfile -File scripts/verify-census.ps1 -InstallPath ./artifacts/T-7-verify`.

## Configuration

| Setting | Default | |
|---|---|---|
| `MUTHUR_URL` | `http://127.0.0.1:7420` | hub address, for CLI and server |
| `MUTHUR_HOME` | `%LOCALAPPDATA%\Muthur` | database, log, tokens, harness catalog |
| `MUTHUR_AGENT` | — | the agent a session acts as (or `--as-agent`, `--founder`) |
| `Muthur:ClaimLeaseMinutes` / `RoleLeaseMinutes` | 30 / 30 | appsettings or `Muthur__…` environment variables |
| `Muthur:IngestIntervalSeconds` | 180 | |
| `Muthur:CensusChecks` | empty | trusted local check definitions; restart after changes |
| `Muthur:RequireCrossProviderReview` | false | |
| `Muthur:ConductorEnabled` | false | the starting value only; `muthur conductor on --founder` is stored in the database and outlives a restart |
| `Muthur:ConductorMaxSessions` | 2 | the starting value only; `muthur conductor sessions <n> --founder` is stored in the database and outlives a restart |
| unattended window | — | `muthur conductor unattended --from 22:00 --to 07:00 --sessions 1 --founder` caps the ceiling during the hours nobody is watching; local time, and it may cross midnight |
| `Muthur:ConductorSessionMinutes` | 45 | a session that has not finished by then is killed |
| `Muthur:ConductorMaxAttempts` | 3 | failed verdicts on one task before it stops and asks you |
| `Muthur:ConductorIntervalSeconds` | 60 | how often it looks; floored at 15s, and `conductor status` reports the floored value |
| `Muthur:ConductorStallProbeMinutes` | 30 | after it gives up on a task, how long before it quietly tries once more |
| `Muthur:DiscordBotToken` | — | bot token for `discord:` ingest; set it as `Muthur__DiscordBotToken`, never in a file |

A build under test must never share the live hub's port or data: prefix **every** command with its own
`MUTHUR_HOME`/`MUTHUR_URL` (never `export` them). `muthur down` refuses to stop a hub that is not its own installation.

## Honest limits

- On one machine the gates are guardrails against error-prone agents, not a security boundary: every process runs as you.
  Real isolation arrives when the hub runs on its own host (tokens, role checks and "all access over HTTP" are already in place for that).
- Validation is only as good as your project's ability to be built, launched and driven unattended. That harness is the first thing to build for any project you bring in.
- `github:` ingest and the `github-issue` channel use the GitHub CLI's login (`gh auth login`). Azure DevOps is the next adapter on both sides.
- Discord is two-way: `discord:` ingest reads a channel, `discord-webhook` replies into one. Bot and webhook messages are never
  ingested, so the comms on-call cannot end up answering itself.

## Development

`dotnet build` · `dotnet test` · see [CLAUDE.md](CLAUDE.md) for the rules of the codebase. MUTHUR is built by the process
it implements: every change is a ledger task with a spec, validated by an independent agent, landed by the hub.

## Shared incidents

Incidents retain a shared diagnosis and independent task observations. CLI output and
`/api/v1/incidents` responses are JSON. Using your registered identity:

```text
muthur incident add "Repeated local failure" --project demo --signature failure-17 --path worker/start --configuration measured-v1 --recovery "A successful bounded probe"
muthur incident match --project demo --signature failure-17 --path worker/start --configuration measured-v1
muthur incident show I-1
muthur incident observe I-1 --task T-1 --evidence "Independent measured failure" --signature failure-17 --path worker/start --configuration measured-v1
muthur incident update I-1 --diagnosis "Shared diagnosis from retained evidence"
muthur incident transition I-1 --state confirmed --evidence "Compared the independent measurements"
muthur incident suppress I-1 --task T-1 --assignment "#orchestrator" --observation 1 --reason "Exact current condition prevents this assignment"
muthur incident recover I-1 --kind probe --evidence "Successful bounded probe for this exact condition"
muthur incident metrics I-1 --hours 24
```

Use returned incident and observation IDs. Matching is exact after trimming, case
sensitive and advisory. Distinct incidents can share a tuple. Observe differing
facts without suppressing, and correct a link with `incident unlink I-1 --observation
1 --reason "Grouping correction"`; all historical evidence remains available.
`incident list [--project demo]` includes every state.

A suppression gates only the specified task/assignment. A validator role key gates
only that validator. Task detail and conductor status explain effective gates;
new orchestrator claims return `incident_wait`, while same-owner renewal continues.
Recovery releases this gate without changing other gates or in-flight work. Probe
recovery is the caller's attestation and marks the incident mitigated. Configuration
recovery requires a different measured value and preserves the incident state.
Both advance the condition version; rearming needs a fresh observation. Reopening
resolved/disproven incidents also advances the version.

Workarounds require an authorization reference and remain annotations. The hub makes
no model calls, runs no probes and grants no capabilities. Metrics identify their
window and linked-task cohort, count recorded diagnosis updates and grouping
corrections, and distinguish staffing attempts from worker reports and outcomes.
Unavailable process/session attribution is null with an explanation; scratch counts
do not demonstrate a live speedup. `scripts/verify-T-103.ps1` exercises an installed
build in an isolated scratch home; the real pilot remains a separate follow-up.
