# T-6 — Multi-harness workers: Claude, Codex, local models

> Specialist-tier task: designed and built by the orchestrator tier directly. Design: `docs/PLAN.md` §2a and M6.

## Design (as built)

- **The hub makes no model calls.** Harness knowledge lives only in `src/Muthur.Launch` and `kit/<harness>/`.
- **Tier catalog**: `{MUTHUR_HOME}/harnesses.json` (created with defaults on first start, re-read on every call) maps
  `mastermind|implementer|utility` to an ordered list of `{harness, model, account}`. `muthur harness tiers [--tier t]`.
- **Account limits**: `muthur agent limited --minutes n` (also limits the agent's account) or `muthur harness limit <account> --minutes n|--clear`.
  A limited account's candidates are flagged and skipped until the limit passes.
- **`muthur worker run --tier <t> --spec <path> [--unit u] [--task T-n] [--harness h] [--base ref] [--note …]`**:
  creates `.worktrees/worker-…` on a new `worker/…` branch from the current branch; refuses if the spec is not committed
  there (`spec_not_committed`); composes the implementer contract + assignment; runs the first available candidate headless
  with `MUTHUR_AGENT`/`MUTHUR_TOKEN` removed from its environment; on an out-of-quota failure marks the account limited and
  falls through to the next candidate; a worker that ran and failed is not retried elsewhere. Afterwards it commits anything
  left uncommitted, derives success from the report's `STATUS:` line, prints a JSON result (branch, worktree, commits, report)
  and records `worker.finished|failed` on the task with the model that did the work.
- Adapters: `claude` (`--print --output-format json --permission-mode acceptEdits --settings <generated allow/deny file>`),
  `codex` (`exec --cd … --sandbox workspace-write --output-last-message …`), `codex-oss` (the same with `--oss --local-provider ollama`).
  Extra allowed shell commands per project: `workerAllowedCommands` in `muthur.project.json`.
- Kit: `codex` (a marked section in `AGENTS.md` + `.muthur/procedures/`) and `generic` adapters; `kit install` adds `.worktrees/` to `.gitignore`.

## Verification

```
dotnet build && dotnet test      # Muthur.Launch.Tests (adapters, fall-through, env scrubbing, STATUS parsing) + HarnessTests
```

Against a scratch instance, in a throwaway git repository that contains a committed tiny spec and a `muthur.project.json`
(a Python or shell one-file task keeps it fast; give it `"test"` and `"workerAllowedCommands"`):
- `harness tiers` lists three tiers; editing `$SCRATCH/harnesses.json` changes the output at once; invalid JSON → exit 2 `catalog_invalid`.
- `agent register … --account claude-subscription`, `agent limited --minutes 30` → that account's candidates show `limited:true`;
  `worker run --harness claude …` then → exit 2 `no_candidates`; `harness limit claude-subscription --clear` restores it.
- `worker run --spec <uncommitted file>` → exit 2 `spec_not_committed`.
- **Real workers (uses the machine's Claude and Codex logins; each takes about a minute):**
  `worker run --harness claude --spec … --task T-1` and `worker run --harness codex --spec … --task T-1` both exit 0 with
  `"status":"done"`, each on its own `worker/…` branch with a commit containing the implementation (for Codex:
  `"committedByLauncher": true`); the project's test passes on both branches; `log --task T-1` shows two `worker.finished`
  events naming `claude/<model>` and `codex/default`. If a login is missing or out of quota, say so in the evidence rather than failing the task for it.
- `kit install --harness codex` in a repository that already has an `AGENTS.md` keeps the existing content and adds one
  `<!-- BEGIN MUTHUR -->…<!-- END MUTHUR -->` section; running it again changes nothing (`unchanged`).
- Local models: `codex-oss` needs a pulled Ollama model that supports tool use; with none pulled this path cannot be exercised — note it, do not fail for it.
