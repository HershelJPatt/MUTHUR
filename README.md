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
stops and asks you instead.

Workers have their hub credentials scrubbed because they have no authority; a validator is given an identity
because its verdict decides whether work ships. Those are two launchers on purpose.

## Any model, any vendor

`%LOCALAPPDATA%\Muthur\harnesses.json` maps tiers to an ordered list of `{harness, model, account}`. `muthur worker run`
staffs a unit with the first candidate whose account is not out of quota, in its own git worktree, with no hub identity,
and records which model did the work. Subscriptions work because MUTHUR only ever launches the vendors' own CLIs.

```bash
muthur harness tiers                          # who staffs what, which accounts are limited
muthur agent limited --minutes 120            # "my account hit its limit" → its account leaves the rotation
muthur worker run --tier utility --spec …     # local models (Ollama via `codex --oss`) belong in the utility tier
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

## Configuration

| Setting | Default | |
|---|---|---|
| `MUTHUR_URL` | `http://127.0.0.1:7420` | hub address, for CLI and server |
| `MUTHUR_HOME` | `%LOCALAPPDATA%\Muthur` | database, log, tokens, harness catalog |
| `MUTHUR_AGENT` | — | the agent a session acts as (or `--as-agent`, `--founder`) |
| `Muthur:ClaimLeaseMinutes` / `RoleLeaseMinutes` | 30 / 30 | appsettings or `Muthur__…` environment variables |
| `Muthur:IngestIntervalSeconds` | 180 | |
| `Muthur:RequireCrossProviderReview` | false | |
| `Muthur:ConductorEnabled` | false | the starting value only; `muthur conductor on --founder` is stored in the database and outlives a restart |
| `Muthur:ConductorMaxSessions` | 2 | validator sessions running at once |
| `Muthur:ConductorSessionMinutes` | 45 | a session that has not finished by then is killed |
| `Muthur:ConductorMaxAttempts` | 3 | failed verdicts on one task before it stops and asks you |
| `Muthur:ConductorIntervalSeconds` | 60 | |
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
