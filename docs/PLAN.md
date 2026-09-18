# MUTHUR — Technical Plan

Status: M0–M8 built and landed · 2026-09-17 · M8 passed validation in round 17 (see specs/T-9.md) · M9 (deployable, multi-machine) not started

A local control plane for an organization of coding agents: task ledger, roles,
validation lifecycle, messaging, and a gated path for anything leaving the machine.
CLI for agents, web dashboard for humans. The [README](../README.md) is the user guide; this is the design record.

## 0. Where the build ended up

| Milestone | State | Built as |
|---|---|---|
| M0 skeleton | done | solution, AOT CLI, `up/down/status` |
| M1 tasks, leases, ledger | done | single-writer `Ledger`, append-only `events` (DB triggers) |
| M2 kit + orchestrated work | done | `kit/core` procedures, Claude adapter; T-1 was the first task run through the loop |
| M3 dashboard | done (T-1) | three implementer units from one frozen spec, reviewed and integrated by the orchestrator |
| M4 roles, validation, landing | done (T-2) | `GitLander`: merge in the checkout, plumbing merge without one, PR mode |
| M5 bus, inbox, founder requests | done (T-3, T-4) | long-poll inbox is the single wake-up channel; the hub notifies validators and owners |
| M6 multi-harness | done (T-6) | `Muthur.Launch`, tier catalog, account limits, `worker run`; same spec built by Claude and by Codex workers |
| M7 ingest | done (T-7) | cursor polling, GitHub issues via `gh`, push API, claim/convert/dismiss. Azure DevOps adapter: not yet |
| M8 outbound gate | done (T-9, T-10) | allowlist, secret scan, sha-bound peer review, founder approval, file/Discord/GitHub channels |
| M9 deployable | not started | the rules in §9 were followed throughout |

**What the process itself taught (all found by the independent validator, none by the builders' tests):**

- T-3 failed validation: a client waiting in `msg inbox --wait` kept the server alive ~30 s after `down`, database locked.
  Every idle agent sits in that call, so it would have hit every restart. Fixed by tying the wait to `ApplicationStopping`.
- The live hub was stopped twice by a build-under-test CLI run without its scratch environment. T-8 made `down` refuse
  unless it can positively establish the running hub is its own installation; its first version failed validation too
  (a CLI with no bundled server skipped the guard).
- The Codex sandbox keeps `.git` read-only, so its workers cannot commit; `worker run` commits on their behalf and takes
  success from the report's `STATUS:` line rather than the process exit code.

**Differences from the plan below:** identity is `MUTHUR_AGENT` + a per-agent token file (environment variables do not
survive between an agent's shell calls, so a bare `MUTHUR_TOKEN` was not enough); timestamps are unix milliseconds;
`kit install` arrived in M2, not M6; validation throughput, not implementation, is the bottleneck — more than one
validator session is the first thing to add when a project gets busy.

## 1. Constraints and what they force

**Single dev machine, not always running, no deployment (for now).**

| Constraint | Design consequence |
|---|---|
| Hub is a local process that comes and goes | `muthur up` / `muthur down`; every piece of state is in the DB, nothing in memory that matters |
| Machine sleeps / hub is off for hours | Ingest is **pull with cursors**, never webhooks. On start the hub catches up from the last cursor |
| Agents die when terminals close | Claims and role holds are **leases** with heartbeats. Startup reconciliation releases expired ones |
| No separate host to hide secrets on | On one machine the gate is a **guardrail against error-prone agents, not a security boundary**. Same-user processes can read anything. Real isolation arrives with deployment (M9) |
| No runner service yet | Agents are Claude Code sessions you start in terminal panes and name yourself (`muthur agent register --name top-left`), exactly like the video's `mac.bottom-left` |
| Must become deployable later | See §9 — rules followed from day one so M9 is configuration, not a rewrite |
| Agents may run on Claude, OpenAI, or local models | The hub is **harness-agnostic**: anything that can run a shell command can be an agent. See §2a |

## 2. Architecture

```
 terminal panes (Claude Code sessions)          browser
 ┌──────────────┐ ┌──────────────┐          ┌────────────┐
 │ orchestrator │ │ validator    │   ...    │ dashboard  │
 │  └ workers   │ │  └ workers   │          └─────┬──────┘
 └──────┬───────┘ └──────┬───────┘                │ Blazor Server circuit
        │ muthur.exe (AOT)  │                        │
        └────── HTTP 127.0.0.1:7420 ──────────────┤
                         ▼                        ▼
                 ┌─────────────────────────────────────┐
                 │ Muthur.Server  (ASP.NET Core, 1 proc)  │
                 │  API · state machine · gate · ingest│
                 │  Blazor UI · background services    │
                 └──────────────┬──────────────────────┘
                                ▼
                  %LOCALAPPDATA%\Muthur\muthur.db (SQLite WAL)
```

- Workers (subagents) never call the hub. Only orchestrator-level sessions do.
- The CLI is a pure HTTP client. It never opens the DB.
- The UI reads through the same application services the API uses — no `DbContext` in components.

## 2a. Agent runtimes (Claude / OpenAI / local)

**Rule 1 — the hub makes zero LLM calls.** It is a ledger and a gate. Models are only ever reached
through an agent harness. This is what makes subscriptions usable: subscription auth is valid inside the
vendor's own CLI (Claude Code, Codex CLI), not as a token lifted out and used from our server. Anything
server-side that wants a model (e.g. a second-opinion secret scan) shells out to a harness in headless
mode like any other worker.

**Rule 2 — the contract with an agent is `muthur.exe` + markdown.** Role briefs, specs and procedures are
plain markdown served by the hub; the CLI is the only integration point. No harness-specific API in the core.

| Harness | Auth | Realistic tier | Notes |
|---|---|---|---|
| Claude Code | Claude subscription or API key | mastermind, implementer | native subagents + worktrees; headless `claude -p` |
| Codex CLI | ChatGPT subscription or API key | mastermind, implementer | `AGENTS.md`; headless `codex exec` |
| Local (Ollama via Codex `--oss`, OpenCode, or similar) | none | **utility only** | log triage, summarizing inbound, census jobs, cheap pre-review. Not orchestration or spec-writing — being wrong there costs more than the tokens saved |

Exact flags, subagent support and local-model wiring per harness are verified against current docs at M6, not assumed here.

**Tiers, not model names.** Skills and briefs refer to `mastermind` / `implementer` / `utility`.
`muthur.harnesses.json` maps each tier to an ordered list of `{harness, model, account}` candidates:

```json
{ "implementer": [
    { "harness": "claude", "model": "opus",  "account": "claude-personal" },
    { "harness": "codex",  "model": "<codex model>", "account": "chatgpt-personal" } ],
  "utility": [ { "harness": "codex-oss", "model": "<ollama model>", "account": "local" } ] }
```

**Harness-agnostic worker spawn.** `muthur worker run --tier implementer --spec specs/T-1.md`:
creates the git worktree, picks the first available candidate for the tier, launches that harness headless
with the implementer prompt + spec, captures the result, returns `{branch, summary, exit}`. The worker gets
no hub token. This lets a Claude orchestrator drive Codex workers and vice-versa, and is the same launch code
(`Muthur.Launch`) the M9 runner uses. Claude orchestrators may still use native subagents when that's simpler.

**Rate limits are a first-class state.** A dozen agents will exhaust a subscription window. Agents report
`muthur agent limited --until <time>`; the dashboard shows it; `muthur worker run` skips accounts that are limited
and falls through to the next candidate. Spreading tiers across providers is the practical way to keep
the org running.

**Cross-provider review.** Every event records `harness/model` of the actor. Optional project rule
`require_cross_provider_review`: outbound and code review must come from a different provider than the
author — different models have different blind spots.

**Policy caveat.** Personal subscriptions on company code is a company-policy question, separate from whether
it technically works. The company hub instance should use accounts the company has approved; `account` labels
exist so this is visible in the ledger.

## 3. Solution layout

```
Muthur.slnx
  src/Muthur.Contracts   DTOs, enums, route constants, JSON source-gen context (AOT-safe)
  src/Muthur.Core        domain: state machine, lease rules, gate rules. No EF, no HTTP
  src/Muthur.Data        EF Core model, migrations, SQLite provider (Azure SQL later)
  src/Muthur.Server      minimal API endpoints, background services, Blazor UI
  src/Muthur.Launch      harness adapters (claude, codex, codex-oss…): build command line, run headless, parse result
  src/Muthur.Cli         System.CommandLine, Native AOT → muthur.exe
  tests/Muthur.Core.Tests          pure unit tests (state machine, leases, gate)
  tests/Muthur.Server.Tests        WebApplicationFactory + temp SQLite, end-to-end via HTTP
  kit/                agent definitions, skills, spec template, role briefs (copied/linked into target repos)
```

.NET 10, central package management, `TimeProvider` injected everywhere (lease tests need a fake clock).

## 4. Data model

IDs: tasks get a short sequential display id (`T-142`) because agents and humans type them. Everything else GUID.
Timestamps stored as UTC unix milliseconds (SQLite cannot compare DateTimeOffset; integers order correctly on every provider).

| Table | Key columns | Notes |
|---|---|---|
| `projects` | key, repo_path, default_branch, land_mode (`merge`\|`pr`), required_validators | one hub instance, many projects |
| `agents` | name, token_hash, status (`live`\|`stale`\|`limited`), limited_until, last_heartbeat, summary, harness, model, tier, account | summary = the one-liner shown under each agent in the dashboard; harness/model copied onto every event the agent writes |
| `roles` | key, project_id, brief_md, singleton | brief is what a session reads when it takes the role |
| `role_holds` | role_key, agent_id, lease_expires | the "HELD" badges |
| `tasks` | display_id, project_id, title, body_md, state, priority, owner_agent_id, claim_expires, spec_path, branch, pr_url, parent_id, inbound_id | |
| `task_validations` | task_id, validator_key, verdict (`pending`\|`yes`\|`no`), evidence_md, agent_id | the `mac yes / win yes / web yes` pills |
| `events` | seq (autoincrement), at, actor, type, task_id, payload_json | **append-only**; SQLite triggers raise on UPDATE/DELETE |
| `messages` | from, to (agent\|role\|`founder`), thread_id, body, blocking, read_at | the bus/stream |
| `founder_requests` | task_id, agent_id, question, options_json, status, answer | "waiting on a founder" queue |
| `inbound` | source, external_id (unique with source), payload_json, claimed_by, task_id | "unclaimed inbound" |
| `outbound` | target_id, body, body_sha256, author, scan_result, reviewer, verdict, approval_id, status | reviewer ≠ author enforced |
| `targets` | channel, address, project_id | outbound allowlist |
| `ingest_cursors` | source, cursor | catch-up after downtime |

Every state-changing service method writes its row change and its `events` row in one transaction.

## 5. Task state machine

```
backlog ──claim──▶ in_progress ──implemented──▶ validating ──all required yes──▶ validated ──land──▶ done
                      ▲   │                         │
                      │   └──ask founder──▶ blocked │ any "no"
                      └─────────────────────────────┘ (back to in_progress, evidence attached)
```

- `claim` is an atomic conditional update (`WHERE state='backlog' OR claim_expires < now`). Race-tested.
- `implemented` requires `spec_path` and `branch` to be set.
- Entering `validating` creates one `pending` row per `required_validators` and notifies holders of those roles.
- `land` is only legal from `validated`. **The server performs it**: `land_mode=merge` runs the git merge itself; `land_mode=pr` opens a PR (company repos — a human merges). Merge authority lives in the hub, not in any agent's judgment.

## 6. CLI surface

JSON to stdout by default, `--pretty` for humans. Exit codes: 0 ok, 2 rule violation (message says which rule), 3 conflict/lost race, 4 hub not running.

```
muthur up | down | status
muthur agent register --name <n> --harness <h> --model <m> | heartbeat --summary "<text>" | whoami
muthur agent limited --until <time>
muthur worker run --tier <t> --spec <path> [--harness <h>]
muthur role list | take <role> | release <role> | brief <role>
muthur task add | list [--state --project --mine] | show T-1 | claim T-1 | release T-1
muthur task spec T-1 <path> | implemented T-1 --branch <b> | land T-1
muthur validate list [--role <r>] | pass T-1 --as <validator> --evidence <file> | fail T-1 ...
muthur msg send --to <agent|role> "<text>" [--blocking] | inbox [--wait 300]
muthur ask T-1 "<question>" [--option a --option b]      (founder request; task → blocked)
muthur inbound list | claim <id> --as-task
muthur out draft --target <t> --file <f> | review <id> --approve|--reject | send <id>
muthur log [--task T-1] [--since <seq>]
```

`muthur msg inbox --wait N` long-polls: an on-call session blocks on it instead of burning tokens in a poll loop.

Identity: `muthur agent register` returns a token stored per terminal session (`MUTHUR_TOKEN`). Roles gate commands server-side (only a holder of `win-validator` can `validate pass --as win`).

## 7. Dashboard (Blazor Server, same process)

Modeled on the Brilliant hub viewer:

- **Board** — Backlog / In progress / Validating / Done-24h. Cards: title, priority, owner, validator pills, age.
- **Agents** — live/stale by heartbeat, held roles, current summary, task count.
- **Needs you** — founder requests and outbound approvals, answerable inline. This is the page you actually work from.
- **Stream** — live tail of `events` + `messages`, filters: all / blocking / inbound.
- **Task detail** — body, spec, full event history, validation evidence.

Implementation rules: one component per panel; `<Virtualize>` on backlog and stream; an in-process `IEventFeed` (Channel) fans out new events, panels coalesce re-renders to ≤2/sec.

## 8. The kit (what goes into each target repo)

Canonical content lives once in `kit/core/` as harness-neutral markdown (orchestrate procedure, validate
procedure, on-call loop, implementer prompt, spec template). Thin adapters wrap it:
`kit/claude/` (`.claude/agents`, `.claude/skills`), `kit/codex/` (`AGENTS.md` + prompts), `kit/generic/`
(a single system-prompt file for any other harness). `muthur kit install --harness <h>` copies the right one.
The Claude adapter is built first (M2); the items below describe it.

- `.claude/agents/implementer.md` — Opus-level; implements a frozen spec in a worktree; tool permissions exclude `hub`, `git push`, `git merge`; ends by reporting branch + summary.
- `.claude/skills/orchestrate` — the mastermind loop: claim → research → write `specs/T-n.md` → fan out implementers → review diffs → iterate → `muthur task implemented` → later `muthur task land`. Never edits product code itself.
- `.claude/skills/validate` — take validator role, read brief, drive the app end to end, attach evidence, pass/fail.
- `.claude/skills/oncall` — generic role loop around `muthur msg inbox --wait`.
- `specs/_TEMPLATE.md` — goal, non-goals, exact files/interfaces, acceptance checks a worker can run, out-of-scope list. "Frozen" = worker may not reinterpret; it reports a spec problem back instead.
- `muthur.project.json` — project key, land mode, required validators, how to build/run/test.

Exact frontmatter/permission syntax for agent definitions gets verified against current Claude Code docs at M2, not assumed.

## 9. Rules that keep it deployable

1. All access over HTTP, even on localhost. No shared files between CLI and server.
2. No SQLite-specific SQL outside migrations; integration tests written so a second provider can run the same suite.
3. All config via `IConfiguration` (bind address, DB provider, connection string, secrets source).
4. Tokens and role checks on from M1, even though localhost doesn't strictly need them.
5. Outbound credentials only ever loaded by the server process (user-secrets now, Key Vault later).
6. No absolute paths in the DB — paths are relative to `projects.repo_path`.

7. Nothing in Core/Server/Contracts names a vendor or model. Harness knowledge lives only in `Muthur.Launch` and `kit/<harness>/`.

M9 then is: bind non-loopback + TLS, swap provider, run as a service/container, add `Muthur.Runner` per machine.

## 10. Milestones

Each ends with something used for real. **Project #1 is this repo** — the hub is built by the process it implements, which keeps company-repo policy out of the way until the loop is proven.

| # | Deliverable | Done when |
|---|---|---|
| **M0** | Solution skeleton, build/test script, `muthur up/down/status`, empty DB + migrations | `muthur status` returns JSON from a running server; AOT publish of `muthur.exe` works |
| **M1** | Agents, tasks, claims, leases, events | Two terminals race `muthur task claim` — exactly one wins; expired claim is reclaimable under fake clock; UPDATE on `events` fails |
| **M2** | Kit v1: implementer agent, orchestrate skill, spec template. Manual single-orchestrator loop | One real hub feature (M3's first panel) delivered spec → worktree worker → review → merge, tracked as a hub task |
| **M3** | Dashboard: board, agents, stream, task detail | Moving a task via CLI updates the open board in <1s without reload |
| **M4** | Roles + leases, validation lifecycle, server-side `land` | Task cannot reach `done` without every required validator `yes`; failed validation bounces it back with evidence |
| **M5** | Messaging, long-poll inbox, founder requests + "Needs you" page | An agent asks a question, blocks, you answer in the browser, it resumes |
| **M6** | Multi-harness: `Muthur.Launch` adapters (claude, codex, local), tier config, `muthur worker run`, `limited` state, Codex + generic kit adapters | Same spec implemented once by a Claude worker and once by a Codex worker via `muthur worker run`; a limited account is skipped; a local model completes a utility job (inbound summary) |
| **M7** | Ingest: GitHub issues (poll + cursor), then Azure DevOps work items | Issue opened while hub was down appears as unclaimed inbound after `muthur up` |
| **M8** | Outbound gate: allowlist, secret scan, peer review (optionally cross-provider), approval | Send to non-allowlisted target, self-review, and body-changed-after-review are all refused with exit 2 |
| **M9** | Deployable: second DB provider, service hosting, `Muthur.Runner` | Second machine's agents work against the hub |

M0–M1 are built directly (no org exists yet). From M2 on, the orchestrate loop builds the rest.
The `agents` harness/model/tier/account columns and per-event actor model exist from M1, so M6 adds
launch code and adapters, not schema surgery. M6 can move ahead of M3–M5 if subscription limits bite early.

## 11. Open decisions

1. **First external project after the hub itself** — a personal project or the monorepo? Determines whether `land_mode=pr` + Azure DevOps ingest move up from M6.
2. **What "validation" means for that project** — which platforms/validators are required, and can an agent already build, run, and drive the app unattended? If not, that harness is the real M4 work.
3. **Answer channel** — browser "Needs you" page only, or also Discord/WhatsApp so you can unblock agents from your phone (pulls part of M7 forward).
