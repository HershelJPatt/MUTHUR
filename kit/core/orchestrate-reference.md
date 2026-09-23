# Orchestrate — reference

The parts of the orchestrate procedure a session needs only sometimes. The procedure itself says when to
come here; nothing in this file changes the loop or the rules, it fills them in.

## Decision routing

At intake or after a failed run, explicitly check `muthur incident match --project <key>
--signature <text> --path <text> --configuration <text>` with the observed identifiers.
Matching is read-only, exact after trimming and advisory. Inspect each candidate using
`incident show I-n`; matching never proves a common root cause. Separate incident IDs
can share a tuple. If the facts support grouping, append independent evidence using
`incident observe I-n --task T-n --evidence <text> --signature <text> --path <text>
--configuration <text> [--run <id>]`. Differing observations are also retained.

Observation does not suppress work. Explicit `incident suppress` requires active exact
evidence for the current condition of a confirmed/mitigated incident and gates only
the named task/assignment (`#orchestrator` or one validator role). Only the founder or
task owner may change links/suppression; identified agents may link unowned tasks.
Correct grouping with `incident unlink` and a reason; history remains available.

Probe recovery attests a successful bounded probe for this exact condition; configuration
recovery records a different measured value. The hub runs neither probes nor workaround
commands. Recovery advances the condition version, releases only this incident gate and
requires fresh observations before suppression can be rearmed. Dependencies, human
requests, budgets, attended flags, holds and terminal state remain independent.
Workarounds are annotations with authorization references, never capability grants.
Do not periodically retry unchanged suppression; inspect the recorded recovery condition.

For frozen specs with standalone `capabilities:` requirements, inspect the exact committed spec/base,
candidate and launch path before dispatch. Unknown, stale, unavailable and temporarily failing evidence
refuses a full session; do not loop or widen permissions. Cache identity includes version, configuration
and revision. Remove only a selected stale record and explicitly re-probe once shared admission permits
it. Explicit worker-run probes require --task and shared catalog/account/budget/capacity admission; refusal starts no model. Existing specs without the
line retain their routing. Browser work remains T-94; native Agent-tool evidence remains T-67/T-97.
Fixture smoke results are simulated, never a real-use pilot. See `docs/capabilities.md` for limitations.

Ordinary `muthur ask` defaults to overseer triage. Use `--kind technical` for known engineering
judgment within established founder direction. Keeping documented compatibility, enforcing shared
rules, duplicate-scope decisions and consistent internal identifiers are technical; words such as
policy or compatibility alone do not make them founder preferences. The overseer should decide these.
Product commitments/preferences, new spending/concurrency, permission expansion, account access,
secrets and outbound approvals must explicitly use `--kind human`. Mixed human questions remain open.
Never relabel a human decision to bypass a gate. Legacy/missing-category records remain human-only.
Ask a blocking question with `--wait 900` so the same command waits for the answer in this session; it costs
nothing while it waits and is not polling. Only when it comes back still open, write your working notes
with `muthur task notes T-n --file <notes.md>` (what the task needs, what you decided and why, what is
left) and exit, so the session that resumes the task starts from your notes.

### Local coding

Local coding is disabled by default because the Codex/Ollama tool pilot failed. Use the implementer tier
until a successful pilot enables the local-implementer catalog. Once enabled, for a small mechanical unit
with objective checks, make one `muthur worker run --tier local-implementer --spec <spec> --unit <unit>
--task T-n --base <base-branch> --timeout-minutes 10` attempt. Review its diff and run checks. Escalate
failures with evidence to the implementer tier; do not retry locally in a loop.

## Protected agent definitions

Applying harness-protected installed agent definitions, including `.claude/agents/*.md`, requires an
attended session and explicit founder approval for the reviewed change. This holds even for tightening
rails. Agents may prepare, review and test source changes under `kit/` unattended without applying them
to installed definitions. Source approval, validation, task landing, earlier one-off permission and
successful authentication do not grant standing application permission.

Do not route around refusal using another harness, shell, copy command, installer or changed allow rule.
Stop the attempted application.

Before delegation, separate source-only work from installed application. If installed application is
required by the task, record exact target paths, the reviewed source commit/diff and verification in the
spec/task evidence, then mark the task attended:

```
muthur task attended T-n --reason "Protected agent definitions require founder-approved attended application"
```

If explicit approval of that concrete application is missing, ask, wait for the answer in-session, and if
the wait expires leave your notes and exit:

```
muthur ask "Approve attended application of the recorded protected agent-definition diff?" --task T-n --kind human
```

If approval already exists for that exact change, do not ask again. Keep the attended routing and
in an unattended session leave notes and exit for a founder-started attended session. An attended session
may proceed after confirming that the approval still covers the reviewed landed change; a changed diff
requires fresh explicit founder approval.

Where source preparation can land independently, keep that task source-only and create a separate
application task. Replace the body ellipsis with real target paths, reviewed source commit/diff and
verification evidence; mark the new application task attended immediately and record the source task
as its prerequisite:

```
muthur task add "Apply reviewed protected agent definitions" --parent T-n --body "..."
muthur task attended T-application --reason "Protected agent definitions require founder-approved attended application"
muthur task dependencies T-application --after T-n --reason "Apply only the landed reviewed source"
```

These task IDs and the body ellipsis are example placeholders, not literal values to execute. This leaves
an attended backlog application task waiting for the source to land, with no approval request yet. The
source owner records the application task ID in source evidence, completes only the source task and exits
after marking it implemented. Do not claim a second task or ask against the backlog application task.
Attended routing intentionally requires a founder-started attended session, not automatic unattended
conductor staffing.

After the source prerequisite lands, a separate founder-started attended session reads the application
task and source evidence, then claims it with `muthur task claim T-application` (exit 3 means stop). Verify
the exact landed source commit/diff and target paths, then follow the approval routing above on the
now-in-progress application task. If exact-change approval is missing, ask with `--kind human`, wait
in-session, and leave notes and exit before any application if the wait expires. Do not request existing
exact-change approval again; perform application only in the attended session after confirming that
approval still covers the reviewed landed change. A changed diff requires fresh explicit founder approval.

A task genuinely blocked by that application records a dependency on the application task and exits.
Never release a blocked task into runnable backlog or ask the founder to poll it.

Clearing attended requires evidence that the attended application was completed or that installed
application is no longer in scope; it does not authorize later unattended protected writes. Source-only
testing must not be reported as installed application verification.

## Verification a conductor-started session can run

If another task must land first, use `muthur task dependencies T-n --after T-prerequisite --reason "why"`
and exit. This preserves the spec and branch, prevents restaffing and wakes when every prerequisite lands.
Use `--clear` only when the dependency no longer applies. Cancelled prerequisites do not count as landed.
Keep genuine founder questions in Needs You; dependencies do not answer or withdraw them.

Choose the verification path explicitly:

- **HTTP-only assertions** check response data or prerendered HTML. A fetch cannot prove clicks, live updates,
  or virtualized rows; never substitute HTML for interaction evidence.
- **Connector-driven UI** uses the session's browser connector. Inspect its availability separately; an
  unavailable connector does not establish that installed headless tooling is unavailable.
- **Installed headless interaction** declares `needs: headless-browser` and exact probe/tool paths and
  interaction commands in the spec. Run the bounded prerequisite probe before expensive validation builds
  and again before implementation submission. Probe success is not proof of product behavior.

If the connector is unavailable, try the explicit headless probe under existing permissions. This repository's
supported runner is `pwsh -NoProfile -File scripts/browser-capability.ps1 -PlaywrightPath <absolute installed module directory> -BrowserPath <absolute installed executable>`.
Generic kit consumers supply their own equivalent bounded runner. Record exact missing tools or denied
permissions and a blocked verdict when neither permitted interaction path works. Do not install tooling,
expand permissions or start more harness sessions; preserve the shared two-session ceiling.

Legacy `needs: browser` retains attended semantics for compatibility and human visual needs. Other needs
still require attendance, even alongside `needs: headless-browser`. Replacing a spec never clears an existing
or manual attended reason; `muthur task attended T-n --clear` is the explicit operation when it no longer applies.

## Delegation lessons

`worker run` works — T-5 was built entirely this way, one unit on claude and one on codex. Three things
it will not tell you, learned by exercising it (T-15):

- **A worker cannot run `muthur`.** Hub commands are denied and it has no identity, deliberately. So the
  `build` / `test` half of your spec's Verification is run by the worker, and **everything involving the
  CLI, a scratch hub or a browser is run by you**. Never hand work to validation assuming a worker
  executed an end-to-end block. It did not.
- **Check `committedByLauncher` on every report.** When it is true, the worker finished without
  committing and the launcher committed on its behalf. Nothing is lost, but the report still says
  `done`, so the worker's account of itself is already known to be incomplete — read that diff harder.
- **`skipped[]` does not list candidates passed over for being out of quota.** A limited account is
  filtered out before any attempt, so the report names the harness that ran and gives no sign that your
  first choice was skipped. If which vendor built a unit matters, record it yourself.

A **mastermind-tier** worker may come back with a `PLAN:` block instead of a finished subtree, when the
area turned out to be more than one agent should carry. That is the shape working, not a refusal: read the
plan as you would read your own units, correct it if it is wrong, and staff each line with an ordinary
`worker run` — passing `--parent <the specialist's branch>` so the ledger keeps the tree and receipts can
attribute what the fan-out cost. You stay accountable for the result; the specialist supplied the
expertise, not the authority.

Two levels is the reference shape. A third would mean a specialist's plan containing another problem area
rather than units — if you find yourself wanting that, the spec is not frozen enough yet.

A worker that cannot do what it was asked returns `success: false`, `status: spec-problem`, no commits
and a clean tree, and says what was missing — enough to choose between re-spec and retry without opening
the worktree. Its *inputs*, though, degrade silently: a `muthur.project.json` that does not parse leaves
the worker with no build or test commands, and the report will not mention it. Verify the work yourself
before you trust a green report.

Check every branch a worker names, and verify your own base before you dispatch. Then expect the worktree
your implementer gets to be somewhere else entirely: a natively-spawned worktree is
**cut from the repository's HEAD, not from yours**, so it comes up on whatever branch the shared main checkout is
standing on — usually the project's default branch — whatever branch you named. `muthur worker run`
resolves the base from where you are standing and gets this right; a harness's own worktree isolation does
not, and it is not MUTHUR's tooling to fix. Thirteen units in a single day, across two orchestrators,
arrived this way. Every one was caught by the implementer. None was caught by the orchestrator that
dispatched it.

So every delegation prompt must **carry the branch name and the commit sha** of the frozen spec: the
branch because that is the base, and an implementer reading the spec from a branch sees an amendment while
one reading it from a sha never can; the sha because it is the only thing that tells the implementer,
before it writes a line, whether the live base still matches the frozen assignment. Name the project's
default branch as well — the implementer's contract needs it to tell an inherited lineage from work of its
own. And say in the prompt that the worktree may have arrived somewhere else, and that checking is the
first thing it does.
If the named base no longer matches the full dispatch SHA, the worker must return `STATUS: blocked`.
Redispatch with an updated frozen assignment after verifying the current base and spec; do not tell the
worker to ignore the mismatch or use the old SHA instead of the live branch.
