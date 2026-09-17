# T-11 — Polish from validation reports

> Specialist-tier task: small fixes collected from win-1's non-blocking observations on T-4 and T-8.

## Design

- `muthur down`: a status response that is not our JSON (some other server on that port) is "a status it cannot read" → clean
  `not_my_hub` (exit 2), not an unhandled `JsonException`.
- Comms panel: a space between the recipient and the `blocking` tag / task link; messages that arrive while `/needs-you`
  is open are marked read once rendered (the badge no longer keeps counting what the founder is looking at). Prerender still marks nothing.
- When someone other than the asker withdraws a founder request (the founder, from the dashboard), the asker gets a message.
  Withdrawing your own request sends nothing.
- `muthur role release` prints the role (with `holder` absent) instead of nothing.

## Verification

```
dotnet build && dotnet test
```

Scratch instance + real browser:
- Any non-MUTHUR HTTP server on a scratch port returning 200 with HTML for `/api/v1/status`:
  `env MUTHUR_URL=http://127.0.0.1:<port> MUTHUR_HOME=$S ./artifacts/validate/muthur.exe down` → exit 2 `not_my_hub`, no stack trace, no POST sent.
- With `/needs-you` open: `msg send --to founder "hi" --blocking --task T-1` from the CLI → appears live, reads `a1 → founder BLOCKING T-1`
  with spaces; within a couple of seconds the tab badge stops counting it without any navigation. `curl /` repeatedly never clears a badge.
- Withdraw a request from the page → the asking agent's `msg inbox` returns the "withdrawn" message and its task is `in_progress`.
- `role take x` then `role release x` → JSON for role `x` with no `holder`, exit 0.
