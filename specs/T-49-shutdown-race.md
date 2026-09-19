# T-49 — A hub disposed mid-request still logs an unhandled error at shutdown

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the hub's answer to "a request was in flight when I was disposed" is settled by a rule rather
than by which exception happened to be on top of the stack, and there is a test that produces that situation
with the host **actually disposed underneath the request** — which no test in the suite does today. T-41's
whole test surface holds disposal off with a `StopGate`, deliberately: its own amendment says holding
disposal off is what makes those tests deterministic. That leaves the case T-49 is named for — disposal
really happening — unexercised, and the middleware's guard for it unproven.

## What the measurement says, and why the task is still worth doing

T-49's premise was that after T-29 every retained hub directory is one instance of this defect, so the way to
work it is to run the suite and read what the cleanup kept. That has now been done three times, twice by me:

| Run | Tests | Directories released | Kept (logged an error) | Could not be removed |
|---|---|---|---|---|
| Previous holder, main at `a2474a2` (`35ed2f3`) | 349 | 3 | 0 | 0 |
| Mine, main at `15cad7a` | 508 | 415 | 0 | 0 |
| Mine, main at `15cad7a`, again | 508 | 415 | 0 | 0 |

**The detection method works and finds nothing.** The previous holder went further — 480 requests racing
disposal across twelve fresh hubs, zero errors — and released the task rather than change error handling on
the strength of a stack trace from an earlier build. That judgement was right, and this spec does not undo
it: nothing here widens a catch on a guess.

What a fourth run of the same measurement would add is nothing. What is missing is different, and it is
visible by reading rather than by sampling:

1. **No test disposes a hub with a request in flight.** Every T-41 test parks the hub in *asked to stop,
   nothing disposed yet*. The `ObjectDisposedException` in those tests is thrown by a fake channel, not by a
   disposed connection. So the guard that this whole task rests on has never met the thing it guards against.
   `HubDisposalTests` disposes hubs, but only after every request has been awaited.
2. **The guard matches the exception on top, not the cause underneath.**
   `catch (ObjectDisposedException) when (… ApplicationStopping.IsCancellationRequested)` does not match a
   `DbUpdateException` whose `InnerException` is an `ObjectDisposedException`, and EF Core wraps store
   failures that way inside `SaveChangesAsync` — which is where `Ledger.MutateAsync` spends most of a write.
   The rule the middleware's own comment states ("an `ObjectDisposedException` on a hub that is *not*
   stopping is a real defect") is about a *cause*; the code tests a *type*.

Item 2 is a hypothesis about a shape, not a sighting. This task does not act on it as a hypothesis: it builds
the harness from item 1 and then acts on what that harness actually produces. See **Design**.

## Context

- `src/Muthur.Server/Infrastructure/ErrorMiddleware.cs` — the only production file this task may change. Read
  all of it, including the comments: they are T-41's reasoning and this task is bound by it.
- `tests/Muthur.Server.Tests/SystemTests.cs` — T-41's tests, the `StopGate`, `DisposedChannel`, `Gated`,
  `Founder`, `ReadHubLog`. This is the file the new tests belong in and the style to copy. Note
  `ExpectsLoggedErrors = true` on its `HubFactory`: this class provokes logged errors on purpose, so its data
  directory is removed rather than kept as evidence.
- `tests/Muthur.Server.Tests/HubFactory.cs` and `TestHubDirectories.cs` — T-29's cleanup. A hub whose log has
  an `Error` or `Critical` line is kept, and the `muthur-tests:` line at process exit counts what was kept.
  That is the detection method; it is also an assertion available to a test.
- `tests/Muthur.Server.Tests/HubDisposalTests.cs` — the existing "dispose the hub and look at what survives"
  tests. Short, and the right size.
- `src/Muthur.Server/Services/OutboundService.cs` `DefineTargetAsync` — `channel.Validate(...)` runs
  **before** `ledger.MutateAsync`, so a test-registered `IOutboundChannel` is a seam that can hold a request
  still inside the pipeline while something else happens, and then let it walk into real database work.
- `src/Muthur.Server/Services/Ledger.cs` — `MutateAsync` is `CreateDbContextAsync` → `BeginTransactionAsync`
  → work → `SaveChangesAsync` → `CommitAsync`. Each of those four is a different place for a disposed
  connection to surface, and they do not all throw the same thing.
- Commit `35ed2f3` (on no branch now; `git show 35ed2f3`) — the previous holder's measurement and their
  `ShutdownRaceTests.cs`. Read it. Its one test is the closest thing to the harness this task needs.
- `specs/T-41-hub-stopping.md` — the decision this task extends, and the reason a blanket catch is forbidden.

Constraints that are not obvious:

- Warnings are errors. Tests never sleep to synchronize: real time is only ever a hang-detector budget
  (`Eventually.Budget`), and anything a test waits for is a condition it can observe or a step of the fake
  clock.
- `TestServer` does not drain in-flight requests when the host stops the way Kestrel does. That is not a bug
  to fix here; it is what makes a deterministic reproduction possible at all.
- A test must never leave a hub directory behind for a human to read unless it means to.

## Non-goals

- **Draining.** Letting in-flight requests finish before disposing anything is T-41's named follow-up and
  stays one. Kestrel already drains for the real hub up to the host's shutdown timeout; the race this task is
  about is what happens at the end of that window, and a drain does not remove it.
- **Changing the test suite so hubs are only disposed when idle.** Nothing in the suite currently leaks a
  directory, so there is nothing to fix, and T-41 already settled that the hub must answer correctly whoever
  made the request.
- **Any change to `MessageService.InboxAsync`,** the long-poll contract, the CLI, or the 503/exit-4 mapping.
  Those are T-41's and they work.
- **A blanket `catch (Exception) when (stopping)`.** Explicitly forbidden — see the rule below.
- Re-running the suite-wide measurement again. It has been run three times and it is recorded here.

## Design

### The rule (decided here; do not re-open it)

> When the response has not started and `ApplicationStopping` is signalled, a failure **whose cause is the
> hub's own teardown** is answered `503 hub_stopping` and is not logged as an error. "Cause is teardown"
> means the exception **is** an `ObjectDisposedException`, **or any exception in its `InnerException` chain
> is**. Every other exception keeps exactly today's behaviour — `500 internal_error`, logged at `Error` with
> its stack — whether the hub is stopping or not.

This is T-41's decision applied to a cause rather than to a type. It is deliberately not "anything that
fails during shutdown is forgiven": an `InvalidOperationException` from a real defect that happens to land
during shutdown still arrives as a 500 with its stack in the log, and `A_disposed_object_on_a_hub_that_is_not_stopping_is_still_an_internal_error`
still passes unchanged.

### What gets built, in order

**Step 1 — the harness, before any production change.** A test that:

- brings up a hub through `_hub.WithWebHostBuilder(...)` the way `Gated` does,
- gets a request **past** `ErrorMiddleware`'s front guard and holds it there, using a test-registered
  `IOutboundChannel` whose `Validate` blocks on a `TaskCompletionSource` (the request is then inside
  `DefineTargetAsync`, before `ledger.MutateAsync`),
- disposes that hub **for real** from another thread — no `StopGate`, nothing held off, so
  `ApplicationStopping` fires, hosted services stop, the server stops and the container is disposed,
- then releases the parked request, so it walks into `Ledger.MutateAsync` against a disposed factory,
  connection and service provider,
- and records what the request was answered and what the hub's log says.

Synchronisation is by observation, never by sleeping: the test knows the request is parked because the
channel says so (a `TaskCompletionSource` the channel completes on entry), and knows disposal finished
because `Dispose` returned. `Eventually` is the only real-time budget.

**Step 2 — catalogue what that produces.** Which exception reaches the middleware, from which of
`Ledger.MutateAsync`'s four steps, and what the client and the log get. Park in more than one place if the
first lands somewhere uninteresting: the seam can also be made to return normally and let the request reach
`SaveChangesAsync`, which is the step the rule's `InnerException` clause is about.

**Step 3 — act on what step 2 found, and only on that.**

- **If some shape escapes as a 500 / an `Error` line** — apply the rule. In `ErrorMiddleware`, replace the
  type-matched catch with a cause-matched one, e.g. a `private static bool IsTeardown(Exception ex)` that
  walks `InnerException` for an `ObjectDisposedException`, used in the existing `when` clause beside the two
  conditions that are already there. Keep the comment's argument intact and extend it to say why the cause
  and not the type. The test from step 1 then asserts 503 `hub_stopping` and no `Error` line, **and must fail
  without the production change** — say so in the report, having run it both ways.
- **If nothing escapes** — change no production code. Keep the harness as a regression test that asserts what
  it found (a request that met a fully disposed hub was not told the hub broke, and the hub's log has no
  `Error` line), and report that the rule needed no code because the code already satisfies it. That is a
  complete and acceptable outcome for this unit; a green harness over a real disposal is the thing T-49 is
  missing either way.
- **If a shape escapes that the rule does not cover** — for example an exception with no
  `ObjectDisposedException` anywhere in its chain, or a request answered before the middleware sees anything
  — **stop and report**. Do not widen the rule. That is the one case that comes back to me.

### Tests to end with

In `SystemTests.cs`, beside T-41's, named so the file reads as one argument:

1. **The real disposal.** Step 1's harness. Whatever the answer is, it is not `500 internal_error` and the
   hub's log has no `Error` line for it. If the fix was needed, assert 503 `hub_stopping` exactly.
2. **The cause, not the type** — *only if step 3 produced a production change.* On a hub that is stopping, a
   dependency that throws an exception **wrapping** an `ObjectDisposedException` (the shape EF Core produces
   from `SaveChangesAsync`) is answered 503 `hub_stopping`, deterministically, with the `DisposedChannel` +
   `StopGate` pattern T-41 already uses for the unwrapped case. Its mutation is a 500.
3. **Still not a silencer** — *only if step 3 produced a production change.* The mirror of test 2 on a hub
   that is **not** stopping: the same wrapped exception is `500 internal_error` **and logged**. Write it next
   to `A_disposed_object_on_a_hub_that_is_not_stopping_is_still_an_internal_error` so the pair reads as the
   rule.

No test may leave a hub data directory behind. `SystemTests`'s own `HubFactory` already sets
`ExpectsLoggedErrors`; if a new test needs its own hub, it decides that flag deliberately and says why.

### Amendment 1 — "assert no `Error` line" is a trap in this file, and the harness's shape

From the first specialist, who read the file properly before being stopped by a tooling failure, and was
right about both.

**The log is shared.** Every hub built by `_hub.WithWebHostBuilder(...)` uses `_hub.DataDir`, and
`FileLoggerProvider` opens `muthur.log` with `FileMode.Append` — so all of `SystemTests`' hubs append to one
file. `A_disposed_object_on_a_hub_that_is_not_stopping_is_still_an_internal_error` puts an `ErrorMiddleware`
line into it on purpose, and `Refusing_a_request_because_the_hub_is_stopping_is_not_logged_as_an_error`
asserts `DoesNotContain(nameof(ErrorMiddleware), ReadHubLog())` over the **whole** file — which holds only
because xunit happens to run it first within the class. A new test that copies that assertion inherits the
order dependence and will fail for a reason that has nothing to do with this task.

So: **every new log assertion takes the log's length before the request and asserts only on the text
appended after it.** If that makes the existing assertion look fragile beside the new ones, tighten the
existing one the same way; it is a test-only change and it is in scope.

**The harness, drafted.** The shape below compiles nothing and proves nothing yet — it is the instrument for
step 2, and step 3 turns it into the test that stays. `Assert.Fail` is how the measurement leaves the
process, and it goes away once the answer is known.

```csharp
var channel = new ParkedChannel();                       // IOutboundChannel, Validate parks on a TCS
var hub = _hub.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IOutboundChannel>(channel)));
var founder = /* Founder(hub) */;
var before = ReadHubLog().Length;
var request = founder.PutAsJsonAsync(Routes.OutboundTargets, new DefineTargetRequest("news", "parked", ...));
await Eventually.TrueAsync(() => channel.Entered, "the request never reached the channel");
await Eventually.CompletesAsync(Task.Run(() => { hub.Dispose(); return true; }), "disposal never finished");
channel.Release();                                        // now it walks into a disposed Ledger
// then: what the client got, and ReadHubLog()[before..]
```

Parking in `Validate` puts the request before `ledger.MutateAsync`, so it meets whichever of
`CreateDbContextAsync` / `BeginTransactionAsync` / `SaveChangesAsync` / `CommitAsync` fails first. If that
turns out to be `CreateDbContextAsync` — the shape T-41 already covers — park later as well, so the
`SaveChangesAsync` step the rule's `InnerException` clause is about is actually reached. A channel that
returns normally and a seam inside the mutation are both legitimate ways to get there; finding one is part of
step 2.

## Units of work

### Unit A — the whole task
- **Files:** `tests/Muthur.Server.Tests/SystemTests.cs`; `src/Muthur.Server/Infrastructure/ErrorMiddleware.cs`
  **only if step 3 calls for it**. No other production file changes. Do not add a new test file unless the
  harness genuinely does not belong beside T-41's tests — say which you chose and why.
- **Does:** steps 1–3 above.
- **Depends on:** nothing.
- **Acceptance:**
  1. `dotnet build` clean (warnings are errors) and `dotnet test` clean — the whole suite, not just the new
     tests.
  2. The step-1 harness exists and really disposes the host while a request is inside the pipeline. Prove it
     in the report: say which exception the request met and from which line of `Ledger.MutateAsync`.
  3. Every new assertion that claims the fix works has been run with the production change reverted, and the
     report says what it did then. An assertion that passes both ways is not evidence and must be rewritten
     or dropped.
  4. The run's `muthur-tests:` line reports `0 kept` and `0 could not be removed`
     (`dotnet test tests/Muthur.Server.Tests -v n` prints it at process exit; a kept directory means a hub
     logged an error nobody expected — possibly yours).
  5. No sleep is used to synchronise anything.

## Verification

```
dotnet build
dotnet test
```

Both clean, and the `muthur-tests:` line at the end of a `-v n` run says `0 kept, 0 could not be removed`.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t49
$env:MUTHUR_HOME = "$PWD/artifacts/t49-home"; $env:MUTHUR_URL = "http://127.0.0.1:7492"
./artifacts/t49/muthur.exe up
```

- Register an agent, park it in `muthur msg inbox --wait 900`, and run `muthur down` in another shell.
  The waiting call returns promptly, with exit 0 (empty, timed out) or exit 4 (`hub_stopping`). Neither
  `internal_error` nor exit 1 is acceptable, on any round.
- Afterwards `artifacts/t49-home/muthur.log` contains no `Error` line.

This is T-41's end-to-end repeated, because the behaviour under test is the one T-41 established and this
task must not have regressed it. It is not expected to reproduce the defect: the window is milliseconds and
Kestrel drains.

## Amendment 2 — step 2 has been run, and what it found

The worker the previous session started did build the harness and did run it, and its measurement survived
the session that commissioned it (`t49-measure.txt`, a scratch file in its worktree, not part of the change).
Step 2 is therefore closed, and by sighting rather than by hypothesis. Three shapes reached `ErrorMiddleware`
with the response not started:

| # | What arrived | From | `RequestAborted` |
|---|---|---|---|
| 1 | `AggregateException` over `ObjectDisposedException` ("Cannot write to a closed TextWriter") | `SaveChangesAsync`, raised out of EF's own `SaveChangesFailedAsync` logging | signalled |
| 2 | `TaskCanceledException`, nothing in its chain | `Ledger.MutateAsync` line 19 (`CreateDbContextAsync`) | signalled |
| 3 | `AggregateException` over `ObjectDisposedException` | the test's channel, hub not yet aborted | not signalled |

Shape 1 is the one the rule's `InnerException` clause was written for, and it is now a measurement: a hub
disposed underneath a live write does not raise an `ObjectDisposedException`, it raises something wrapping
one, and the type-matched guard let exactly that through. **Step 3's first branch applies — make the
production change.**

**Shape 2 is not the escape case, and does not come back to me.** The rule is about what the hub *says*; a
request whose `RequestAborted` is signalled is told nothing at all, because the last catch has always
required `!http.RequestAborted.IsCancellationRequested` — no 500 on the wire, no `Error` line in the log,
nothing to forgive. T-41 settled that and this task does not reopen it. What would be the escape case is that
same chainless exception arriving with the response not started **and the request not aborted**: then the hub
really would be telling someone it broke over its own teardown, and the rule as written would not cover it.
That has not been seen. If it is ever seen, it is a new ledger task with the trace attached.

**What this means for evidence (acceptance criterion 3).** Because an aborted request is neither answered nor
logged, the real-disposal harness may well pass with the production change reverted — the abort hides the
defect from the client rather than the fix removing it. If it does, that harness is **not** evidence for the
change and must not be written as though it were: it stays as the regression test T-49 was missing (a request
that met a fully disposed hub was not told the hub broke, and nothing was logged), and the report says
plainly that it passes both ways and why. The failing-without-the-change evidence then comes from the
deterministic pair — the wrapped exception on a stopping hub, which without `IsTeardown` is a 500 with an
`Error` line, and its mirror on a hub nobody asked to stop, which must stay a 500. Both must be run reverted
and the report must say what they did.

## Amendment 3 — the draft has now been run, and amendment 2 guessed one thing wrong

Run by me (the orchestrator) on `task/T-49-a-real-disposal`, which carries the draft as the dead session left
it. Reviewing means building and running it myself, and this is what it did.

**With the change in.** `dotnet build`: 0 warnings, 0 errors. `dotnet test`: 4,487 passed, 0 failed, across
all four projects (511 in `Muthur.Server.Tests`). `muthur-tests: 418 removed, 0 kept (logged an error), 0
could not be removed`.

**With `ErrorMiddleware.cs` reverted to its pre-draft version** (tests untouched), both new claims fail:

- `A_teardown_failure_is_refused_even_when_it_arrives_wrapped` — *Assert.Equal() Failure: Expected:
  ServiceUnavailable, Actual: InternalServerError.*
- `A_request_inside_a_hub_that_is_really_disposed_is_never_told_the_hub_broke` — *a request that met a
  disposed hub was answered with the hub's own failure: System.AggregateException: An error occurred while
  writing to logger(s). (Cannot write to a closed TextWriter…)*

**So amendment 2 was wrong about the real-disposal harness, and this supersedes it.** It predicted that the
abort would hide the defect and the harness would pass either way. It does not: the type-matched guard lets
the exception out of `ErrorMiddleware` entirely, and what is outside the middleware — the test host's
diagnostics — hands it to the client. The abort suppresses the middleware's *own* last catch; it does not
suppress an exception that never reached it. The harness is evidence, and it is the strongest evidence this
task has: it is the only test in the suite that disposes a host underneath a live request.

**Acceptance criterion 2, answered from my run.** The parked request met an `AggregateException` over an
`ObjectDisposedException` ("Cannot write to a closed TextWriter", `FileLoggerProvider.Write`), raised from
`DbContext.SaveChangesAsync` at `OutboundService.cs:102`, inside `Ledger.MutateAsync` (`Ledger.cs:25`, the
work delegate, nested under `:29`) — the `SaveChangesAsync` step, as amendment 2 said. Note what the disposed
object is: the **log file's writer**, which EF reached through its own `SaveChangesFailedAsync` logging while
already on its failure path, not the store connection. The rule still classifies it correctly — a closed log
writer is the hub's own teardown — but no test reproduces that path, and one comment currently implies
otherwise. See below.

`A_wrapped_disposed_object_on_a_hub_that_is_not_stopping_is_still_an_internal_error` passes both ways, by
design: it is the silencer guard, and it claims that today's behaviour is *unchanged*, not that the fix
works. Criterion 3 does not apply to it.

### Unit B — what is left, and it is small

Test-only polish plus one question to answer by observation. No production file may change.

1. **Say exactly what the client gets.** `A_request_inside_a_hub_that_is_really_disposed_…` currently
   tolerates three outcomes (a non-500 response, `HttpRequestException`, `TaskCanceledException`). One of
   them is what actually happens. Find out which, deterministically, and assert *that* — per "Tests to end
   with" item 1, a 503 `hub_stopping` is asserted exactly if that is what arrives; if instead the connection
   is gone and there is no response at all, assert that, and say in the comment why a hub that answered
   correctly still cannot be heard. A test that accepts three answers cannot notice when the answer changes.
2. **Correct the `wrapped` XML doc on `DisposedChannel`.** It says EF raises the wrapper out of
   `SaveChangesAsync`; what the sighting shows is EF's *failure logging* raising it while inside
   `SaveChangesAsync`, over a disposed log writer. The pair is still the right evidence — the middleware's
   contract is over the shape of the exception, not its provenance, and reproducing the EF-logging path would
   test EF and `FileLoggerProvider` rather than the middleware — but the doc must say what it stands in for
   instead of claiming to be it.
3. **`ParkedChannel.Release()` belongs in a `finally`.** `Release` is reached only on the happy path today,
   so a failing budget strands a thread-pool thread inside an open transaction for the rest of the process.
4. **Report, do not fix: the teardown branch has no `RequestAborted` guard.** `StoppingAsync` will try to
   write 503 to a connection that may already be gone, and the cause-match now routes more shapes into it.
   The hole predates this task (the type-matched catch had it too) and the frozen rule says nothing about it,
   so **it is not changed here**. Say in the report whether that write throws in the real-disposal test, and
   what happens to the exception if it does. If it can escape `InvokeAsync` on a Kestrel hub — where an
   escaped exception is logged at `Error` by the server, which is the very thing T-49 is named for — that
   becomes its own ledger task with this evidence attached.

## Out of scope / follow-ups

- **Draining**, still. T-41 named it; this task's rule is what a drain's own deadline needs behind it.
- If step 3 finds a shape the rule does not cover, that becomes its own ledger task with the evidence
  attached — not a widening of this one. Shape 2 above is judged *not* to be such a shape; the reasoning is
  in amendment 2 so that a later reader can disagree with it on the record.
