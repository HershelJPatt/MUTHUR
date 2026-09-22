# Local work and cloud usage

The conductor remains deterministic code. Do not launch a model to wait, heartbeat,
poll a board, or run a test command. Read a completed result or a changed event once.

Cloud orchestration and final validation use the mastermind tier. The shipped Codex
default is GPT-6 Astra with medium reasoning; every Codex child explicitly selects
standard service and disables fast mode, including workers launched from a fast parent.

## Local models are off

Both Ollama candidates in the utility tier ship with `"enabled": false` (2026-09-22): the local hardware could
not keep up with the organization, so the cheaper fast cloud tier does that work for now. Flip the flag in
`{MUTHUR_HOME}/harnesses.json` to bring one back; a disabled candidate is never offered to `utility summarize`
or `worker run`, and the command reports `no_local_model`.

## Local evidence summaries

`muthur utility summarize --file failed-tests.log --task T-123 --as-agent NAME`

This reads a bounded excerpt (beginning and end), makes one request to local Ollama,
and returns an advisory summary, source path, truncation flag, elapsed time and actual
input/output token counts. It has no shell tools or hub credentials, stops after three
minutes, caps output at 512 tokens and unloads the model afterwards. It does not retry
or fall back to a paid model. Original evidence stays authoritative. Extract relevant
sections of files larger than 4 MiB before invoking it.

Use `--model gemma4:26b` or `--model qwen3.8:27b` to compare models listed in the local
utility tier. The default catalog prefers Gemma for summaries; benchmark results can
change that order. The Ollama endpoint must be loopback HTTP (default port 11434).
Runs, failures, duration and token counts are recorded in the ledger and receipts.

September 20, 2026 smoke test on the same captured Git-timeout log:

| Model | Elapsed seconds | Input tokens | Output tokens | Result |
| --- | ---: | ---: | ---: | --- |
| gemma4:26b | 17.7 | 1,789 | 346 | Correct failure, test name and timeout; inferred causality labeled |
| qwen3.8:27b | 22.7 | 1,603 | 369 | Correct timeout; overstated causal certainty and said stderr was empty despite the timeout message |

These are single local runs, not a general quality benchmark or a subscription-savings
estimate. Gemma stays first for summaries; review the original evidence for decisions.

## Small implementation units

`muthur worker run --tier local-implementer --spec specs/T-123.md --unit A --task T-123 --base TASK_COMMIT --timeout-minutes 10`

The local-implementer catalog is empty by default. The September 20 pilot with
Qwen through Codex's Ollama provider loaded entirely on the GPU but failed with
`unsupported call: shell` and `no user query found in messages`; it made no edits.
Do not enable automatic local coding until a real edit-and-check pilot passes.

Once enabled, use only for a bounded mechanical edit with a committed frozen spec and objective
checks. A candidate uses the codex-oss harness and local account. It gets
its own worktree and no hub identity, runs at most ten minutes, and has no paid fallback.
Review the resulting diff and run its checks. If unsuccessful, hand the evidence to
the cloud implementer once; do not loop local retries. Architecture, ambiguous work,
complex debugging and final validation remain cloud work. A local success report is
not a validation verdict and cannot land a task.

Local summaries and local worker commands share an exclusive lock in MUTHUR_HOME:
one local job at a time. A busy response is a reason to wait, not to spend cloud quota.

## Repeat staffing budget

`Muthur:ConductorSessionsPerTaskDay` defaults to three sessions per task/role within
24 hours without new evidence. The ledger enforces it across restarts, including
sessions that claimed a task and later lost their lease. Heartbeats, repeated claims
and repeated specs do not reset it. A new implementation head or answered founder
request resets that task; explicitly turning the conductor off/on resets all budgets.
The conductor status lists exhausted budgets and when the oldest counted attempt
expires. A value of zero disables this additional budget.

Measure accepted results per local run, escalation frequency, wall time, and cloud
usage per completed task. Session registrations and staffing counts are not token
bills; native harness subagents may not appear as MUTHUR worker runs.
