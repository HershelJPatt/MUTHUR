# T-14 — The Operations page shows why a message was flagged; what needs the founder comes first

> Specialist-tier follow-up to T-9 (flags) and T-10 (Operations page), plus doc follow-ups from T-13's validation.

## Design

- `OutboundPanel`: a message with `Flags` shows a `flagged` tag, one line saying only the founder can let it leave, and each masked flag
  (kind + masked excerpt) above the body. Messages `awaiting_founder` are listed before everything else (newest first), then the rest of the gate, then the settled tail.
- Docs: the Claude validate skill says `git worktree add --detach`; `validate.md` says `validate-stack-<top task>` is a worktree name; the brief
  no longer suggests `mktemp`, says never `-RestartRunning`, and has the case-file / `head` / `--limit` notes.

## Verification

```
dotnet build && dotnet test      # DashboardOperationsTests.A_flagged_message_tells_the_founder_why_it_needs_them
```

Real browser, scratch instance, a `file` target WITHOUT `--founder-approval`:
- Draft `docker login registry.example.com -u deploy -p Sup3rS3cretValue9` as a1, approve as a2: on `/operations` the message appears live as
  `awaiting founder` at the TOP of the list, with the `flagged` tag and a masked flag line (the secret itself appears in the body — that is the
  text under review — but never in the flag line); the tab badge counts it. Approve from the page → `out send` by a1 succeeds.
- Draft several ordinary messages afterwards: they appear below the flagged one while it is undecided.
- An unflagged message to a `--founder-approval` target shows Approve/Decline and no `flagged` tag.
