# Routing by measured quality

Routing decides which implementer candidate a `worker run` tries first. It is advisory: a recommendation never
grants capacity, account access or authority to start a process, and a run that cannot reach routing runs in
catalog order and says so in its report.

## What is measured

`muthur routing report` reads the last 14 days of the ledger (`--hours`, default 336, at most 720) through
`GET /api/v1/routing/history`: every `worker.finished` and `worker.failed` row and every `validation.failed`
verdict in the window. For each implementer candidate in the catalog it takes the runs on the same
`harness/model` whose recorded `workKind` equals the spec's `work-kind:` line, one per run id (a run reported
twice counts once, by its last report), and computes:

| Measure | Definition |
|---|---|
| comparable outcomes | runs of that work-kind on that harness and model |
| actual starts | comparable runs that were not refused before a process ran (`failureKind` other than `launch_unavailable`) |
| done rate | `worker.finished` per actual start |
| blocked rate | runs whose status was `blocked` per actual start |
| false-approval rate | done units on a task that has a later `validation.failed` in the window, per done unit |
| median seconds | of the actual starts |
| mean cost | over the runs that reported `costUsd`; null when none did |

The report carries these as `comparableOutcomes`, `actualStarts`, `independentSuccessRate` (done rate),
`reworkRate` (blocked rate), `falseApprovalRate`, `endToEndSeconds` (median) and `meanCostUsd`, with the run
ids they were taken from as evidence and a `missingData` line for anything that could not be measured. A spec
with no `work-kind:` line has no comparable outcomes at all.

## The ranking

Only candidates that are capability-eligible and whose account is not limited are ranked. Among those, when
every one has at least five comparable outcomes and every ranking dimension, the order is:

1. lowest false-approval rate
2. highest done rate
3. lowest blocked rate
4. lowest median seconds
5. lowest mean cost, with an unpriced candidate after any priced one
6. catalog position

Otherwise the recommendation is the first eligible, available candidate in catalog order and the rationale
says which condition was missing. Effort is not a ranking input: `RoutingPolicy.EffortFor` sets it from the
work-kind (mechanical low, general medium, complex-debugging and ui-interaction high) under the catalog's
`reasoningEffort` ceiling, and `worker run --effort` overrides both.

## The two commands

- `muthur routing report --task T-n --spec specs/T-n.md --base <branch> --default-branch main [--hours 336]`
  prints the report without recording anything. `record` with the same arguments posts it to
  `POST /api/v1/routing/T-n`, which only the identified owner of an in-progress task may do; the hub records it
  as `routing.recommended` and `muthur routing show --task T-n` reads the history back.
- `muthur worker run` fetches the task's snapshots, takes the newest one with a recommended position, and moves
  that candidate to the front if its harness, model and account are still in the catalog. The run report's
  `policyVersion` is the recommendation's (`measured-quality-v1`) when it was applied and `catalog-order-v1`
  otherwise. Every other admission still applies: capacity, the daily task budget, account limits, capability
  identity and independent validation.
