# T-3 — Internal bus, long-poll inbox, founder requests (services + CLI)

> Specialist-tier task: designed and built by the orchestrator tier directly. Design: `docs/PLAN.md` (M5).
> The dashboard side is T-4.

## Design (as built)

- `muthur msg send --to <agent|role:key|founder> "<body>" [--blocking] [--task T-n]`; unknown recipient → exit 5.
- `muthur msg inbox [--wait N] [--peek]`: unread messages for the caller and the roles it holds; reading marks them read
  (delivered once). `--wait` blocks server-side until a message arrives or N seconds pass (`timedOut: true`).
- `muthur ask "<question>" [--task T-n] [--option …]`: creates a founder request; the task moves to `blocked`
  (its claim cannot lapse while blocked). `muthur requests list|answer <id> "<text>" --founder|cancel <id>`.
  Answering unblocks the task (fresh lease) and messages the asker. Answering twice → exit 3.
- The hub itself messages validator roles when a task is ready, and owners when validation fails or fully passes.

## Verification

```
dotnet build && dotnet test      # MessagingTests covers every rule above
```

Against a scratch instance with two agents `a1`, `a2` and role `win-validator` held by `a2`:
- `a1` sends to `a2`; `a2`'s inbox returns it once, then is empty; `a1`'s inbox never shows it.
- Start `msg inbox --wait 60` for `a2` in the background, send from `a1`: the waiting call returns within ~1 s with the message.
- `msg inbox --wait 3` with nothing pending returns after ~3 s with `"timedOut":true`.
- `ask … --task` on a claimed task → `task show` says `blocked`; `requests answer … --founder` → `in_progress`, and the asker's inbox has the answer.
- `requests answer` as an agent (no `--founder`) → exit 6.

## Round 2 (after win-1's failed validation)

- A pending `msg inbox --wait` is linked to the host's `ApplicationStopping`: it returns `timedOut:true` at once when the hub stops.
  Check: start `msg inbox --wait 300` in the background, `down`, and watch the **server process by PID** — it must be
  gone within ~2 s (before the fix it lingered ~30 s holding `muthur.db`), and an immediate `up` must not overlap a dying server.
- Message bodies are capped at 64 KB (`body_too_long`, exit 2).
- Releasing or cancelling a blocked task withdraws its open founder requests (`requests list` shows none).
- `ask --task` on a task that is not in progress reports `not_in_progress` (it used to say `not_owner`).
