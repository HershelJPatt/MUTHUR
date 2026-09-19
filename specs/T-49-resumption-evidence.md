# T-49 resumption evidence

Resumed as `orchestrator-t-49` on 2026-09-19. The ledger stopped before the branch:
`task/T-49-a-real-disposal` at `b1c4c28` already held the frozen spec, three amendments,
the implementation, and the previous orchestrator's build and mutation measurements.
The old Unit B branch had no additional commits or working-tree changes. Work continued
from that branch; the implementation was not restarted.

## Review

The production diff is confined to `ErrorMiddleware.cs`. It walks `InnerException`
for `ObjectDisposedException` and uses that classification only when shutdown has
been signalled and the response has not started. Existing domain-error, malformed
request, and ordinary internal-error behavior remains in place. The tests exercise
real host disposal, the wrapped failure during shutdown, and the same failure on a
running hub. The latter must remain a logged `500 internal_error`.

The original intermittent suite failure was not reproduced by the earlier suite
measurements. The deterministic harness instead establishes a specific uncovered
path: disposal closes the log writer, EF failure logging raises an aggregate over
the disposed-writer exception during `SaveChangesAsync`, and the old type-only
catch lets that wrapper escape. This evidence does not identify an original
offending test class and does not establish a need for request draining.

## Unit B and mutation evidence

The resumed worker's three focused tests passed with the fix. Reverting only
`ErrorMiddleware.cs` to the pre-change version made two tests fail and left the
running-hub control green:

- Real disposal: expected `TaskCanceledException`, received `AggregateException`
  over `ObjectDisposedException` naming the closed `StreamWriter`.
- Wrapped shutdown failure: expected 503, received 500.
- Wrapped disposal on a running hub: continued to return and log 500.

The temporary observation harness measured server status 503, `HasStarted=false`,
`RequestAborted=true`, and no exception escaping the middleware. The client raised
`TaskCanceledException` while buffering the aborted response. The final test checks
that exact client outcome; it no longer accepts any non-500 response or either of
two exception types. Its parked channel is released in `finally`.

The observed write does not escape `InvokeAsync`: `WriteAsJsonAsync` absorbs the
request-aborted cancellation. This measurement does not establish an additional
Kestrel error-logging defect, so no speculative follow-up or production change was
added for the existing absence of a `RequestAborted` guard on this branch.

## Installed hub

Published the recovered branch with:

```powershell
pwsh -NoProfile ./scripts/install.ps1 -Destination ./artifacts/t49-resume
```

Used only that installation, `MUTHUR_HOME=artifacts/t49-resume-home` resolved under
the task worktree, and `MUTHUR_URL=http://127.0.0.1:17492`. Cleared the live agent
token from the child environment and registered a scratch agent. For each round,
started `muthur msg inbox --wait 900`, observed its endpoint entry in the scratch
hub log, then called the installed CLI's `down` command. All processes were bounded
and the shutdown command also ran in a `finally` block.

| Round | Inbox exit | Response | Shutdown and inbox completion |
| --- | --- | --- | --- |
| 1 | 0 | `{"messages":[],"timedOut":true}` | 2.33 s |
| 2 | 0 | `{"messages":[],"timedOut":true}` | 2.34 s |
| 3 | 0 | `{"messages":[],"timedOut":true}` | 2.36 s |

No `Error` or `Critical` lines appeared in the scratch log. No T-49 scratch server
remained running after verification. This checks the installed long-poll contract;
the deterministic disposal tests provide the regression evidence for the fix.
