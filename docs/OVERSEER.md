# Organization overseer

The overseer is a short-lived technical delegate staffed by the conductor. It shares
the conductor's session ceiling; it never adds a third slot to a two-slot organization.
An eligible oversight run receives the next free slot before ordinary staffing. There
is at most one overseer. The existing conductor remains responsible for routine landing.
Deterministic checks also notice validated work unchanged for 15 minutes and in-progress
or validating work unchanged for 90 minutes. An acknowledged unchanged situation does
not repeatedly wake the model; use a durable time condition when a later recheck is needed.

A daily start limit of `0` means unlimited starts while staffing is enabled; positive values remain available as an optional cap. The default session limit is 20 minutes. Cooldown, quota limits, unchanged-work suppression and the shared concurrency ceiling still apply.

Configure it on the Board or Needs You page in the Overseer panel, or with:

```powershell
muthur overseer configure --file C:\absolute\overseer.json --founder
muthur overseer status
```

Example configuration (replaces the entire configuration):

```json
{
  "enabled": true,
  "harness": "codex",
  "model": "gpt-6-astra",
  "account": "chatgpt-subscription",
  "reasoningEffort": "medium",
  "sessionMinutes": 20,
  "maxStartsPerDay": 0,
  "cooldownMinutes": 10,
  "contextChars": 24000,
  "memoryChars": 6000
}
```

To switch providers, choose `claude`, the exact model ID supported by your installed
Claude CLI, its account key, and the desired effort. Model names are passed through;
the hub does not guess which current model a marketing name such as Fable identifies.
Effort is passed to both adapters. Codex uses standard service with fast mode disabled;
Claude's session settings explicitly disable fast mode. See the
[Claude CLI reference](https://code.claude.com/docs/en/cli-reference) and
[fast-mode settings](https://code.claude.com/docs/en/fast-mode).
Account limits apply before staffing; there is no automatic paid/provider fallback.
Switching the overseer does not switch implementers or validators. Configuration and
usage survive restarts. New model settings apply to the next fresh session; disabling
revokes decision authority immediately. The role defaults off on upgrade.

## Authority

Agents use `muthur ask --kind technical` for engineering decisions within the founder's
established direction. The default `human` kind and all older unclassified requests
remain founder-only. The author must not label product preferences, spending, permission
changes, account access, secrets or outbound approvals as technical. The overseer must
also defer mislabeled or ambiguous requests. Classification is a semantic judgment;
the server enforces the recorded category, not the meaning of arbitrary prose.

`muthur overseer decide <id> <answer> --reason <why> --evidence <references>` requires
the active enabled overseer's identity. It records the actual agent, rationale and
evidence, and uses the ordinary transactional request-unblocking path. The overseer can
file focused follow-up tasks after checking for duplicates, but cannot claim them,
change settings, cast validation verdicts, send outbound messages, or approve them.
Holding the ordinary role alone does not confer decision authority.

## Memory and conditions

Every run starts a new harness conversation. Its prompt contains a bounded checkpoint,
up to 30 relevant changed events, and a small current issue packet. Old transcripts
are never appended. Large questions are clipped and explicitly marked; the overseer
must fetch original evidence before deciding. The context setting bounds the starting
prompt in characters, not total runtime tokens. Timeouts and an optional daily start cap are additional
hard limits; instructions to checkpoint early and exit limit tool/context growth within
a run. This is not an exact subscription-token budget.

The agent saves JSON with `muthur overseer checkpoint --file <absolute-path>`:

```json
{
  "run": "the-run-id-in-the-prompt",
  "summary": "Decision, evidence references, outstanding obligations, and next action.",
  "complete": true,
  "waits": [
    { "kind": "task", "target": "T-67", "expected": "done", "reason": "Adapter must land", "group": "probe" },
    { "kind": "request", "target": "32", "expected": "closed", "reason": "Account access restored", "group": "probe" }
  ]
}
```

Waits can target a task state, a closed request, or a future ISO timestamp (`kind: time`,
empty expected). All conditions with the same nonempty group must hold before that
group wakes the overseer. Separate groups are alternative wake reasons. New unrelated
issues can still start a session; outstanding conditions remain in memory. Recheck the
evidence after waking: closed requests may have been cancelled rather than answered.

A checkpoint saves memory; it does not clear the running model's context. The agent
uses `complete: false` for interim saves, retaining the unprocessed event range. Only a
final `complete: true` checkpoint acknowledges the presented issues as handled or parked.
The agent
exits after checkpointing, and the next session starts fresh. The hub retains incomplete
condition groups even if a new checkpoint accidentally omits them. Saved memory and
waits survive process failure, model changes and hub restart. A run that dies without
checkpointing retains the previous memory and is retried only within cooldown and any configured daily
limits. Successful checkpoints consume only the event range presented to that run, so
events arriving during the session are not silently lost.

The event ledger is the historical record. Summaries should reference evidence there
or in task branches rather than copying complete logs. Pending conditions fit within
12 entries and 6000 memory characters by default; the UI shows memory, waits and usage.
