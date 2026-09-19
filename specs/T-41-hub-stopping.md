# T-41 — A request in flight when the hub stops is told the hub broke

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## What the task was filed as, and what is actually true

T-41 was filed as "`PUT /outbound/targets` returns 500 instead of 422 under parallel load", with a named
suspect: `FileChannel.Validate`, where T-14 added a `Directory.Exists` check, leaking a raw `IOException` on a
contended temp directory.

**That hypothesis is wrong, and the endpoint is incidental.** Both halves were checked rather than reasoned
about.

`FileChannel.Validate` cannot throw anything but a `MuthurException`:

```csharp
if (!Path.IsPathRooted(address)) throw Fail.Rule("invalid_address", ...);
if (Directory.Exists(address))  throw Fail.Rule("invalid_address", ...);
```

`Directory.Exists` is documented to return `false` rather than throw when anything goes wrong, and
`Path.IsPathRooted` does not touch the filesystem at all. There is no path through that method that produces
a 500.

The real cause is in the leaked test hub directories that T-29 is about — over 45,000 of them on this machine, each
with the hub's own `muthur.log`. Every unhandled error the suite has ever produced is still on disk. Searching
them for `ErrorMiddleware: Unhandled error on` finds the reported failure:

```
Muthur.Server.Infrastructure.ErrorMiddleware: Unhandled error on PUT /api/v1/outbound-targets
System.ObjectDisposedException: Cannot access a disposed object.
Object name: 'SQLitePCL.sqlite3'.
   at Microsoft.Data.Sqlite.SqliteConnection.Open()
   ...
   at Muthur.Server.Services.Ledger.MutateAsync[T](...) in Ledger.cs:line 23
```

and it finds **thirty-one more of exactly the same shape**, across at least twelve different endpoints:

| Endpoint | Disposed object |
|---|---|
| `POST /api/v1/agents/register` (×2) | `SQLitePCL.sqlite3` |
| `POST /api/v1/projects` (×3) | `SQLitePCL.sqlite3` |
| `POST /api/v1/tasks/T-1/claim` (×2) | `SQLitePCL.sqlite3` |
| `POST /api/v1/tasks/T-1/implemented` (×2) | `SQLitePCL.sqlite3` |
| `POST /api/v1/tasks/T-1/release` | `SQLitePCL.sqlite3` |
| `POST /api/v1/roles/win-validator/take` (×2) | `SQLitePCL.sqlite3` |
| `POST /api/v1/messages` | `SQLitePCL.sqlite3` |
| `POST /api/v1/outbound`, `POST /api/v1/outbound/O-1/send` | `SQLitePCL.sqlite3` |
| `PUT /api/v1/outbound-targets` | `SQLitePCL.sqlite3` ← **the reported one** |
| `GET /tasks/banana` (the dashboard) | `SQLitePCL.sqlite3` |
| `GET /api/v1/messages/inbox` (×15) | `IServiceProvider` ×13, `SQLitePCL.sqlite3` ×2 |

Every one is an `ObjectDisposedException`, and in every log the next line is
`Microsoft.Hosting.Lifetime: Application is shutting down...`.

**So the defect is not in any endpoint. It is that a request still in flight when the hub stops is answered
`500 internal_error` — the hub telling its own agents that it broke, when what happened is that it was asked
to stop.**

That `GET /api/v1/messages/inbox` is fifteen of the thirty-two is not a coincidence: it is the long-poll every
idle agent sits in, so it is the request most likely to still be open when anything stops the hub.

This is not test-only. `muthur down` while an agent is inside `muthur msg inbox --wait 900` is the same race
on a real hub, and the agent gets exit 1 with an `internal_error` it can do nothing with, instead of being
told the hub went away.

**Not the T-22 flakiness**, which landed and was a connection-pool problem, and **not the `EventLogInternal`
disposal** visible in the same logs — T-35 removed that provider, so that species is already gone from main.

## The decision, and why it is not the founder's

Two questions, both settled here because both are answerable from the code and the exit-code table.

**What status?** `503 Service Unavailable`, code `hub_stopping`. Not 500, because nothing failed. Not 422,
because the request was never invalid — T-41's title asks for a 422 and that would be wrong; the caller did
nothing to be told about.

**What exit code?** `ExitCodes.NotRunning` (4), which already exists and already means "MUTHUR is not
reachable at this URL". A hub that is stopping and a hub that has stopped call for the identical response from
the caller — wait, or start it — so a new code would be a fifth thing to handle for a one-second window. It
also makes `muthur down`'s own wait loop correct for free: it already treats `NotRunning` as "gone".

## Context

- `src/Muthur.Server/Infrastructure/ErrorMiddleware.cs` — the whole of the server-side change. Its final
  `catch` is the one that answers 500, and it already declines to answer when `http.RequestAborted` is
  cancelled, which is the same idea applied to a different signal.
- `src/Muthur.Contracts/Errors.cs` — `ExitCodes`; `NotRunning = 4`.
- `src/Muthur.Cli/Infrastructure/HubClient.cs` — `ApiResult.ExitCode`, the status-to-exit-code table. 503
  currently falls through to `ExitCodes.Error`.
- `src/Muthur.Server/Services/MessageService.cs` `InboxAsync` — already links `lifetime.ApplicationStopping`
  into its wait and returns `TimedOut: true` when the hub stops. **It is not the bug and does not change.**
  It is evidence: the long poll was written to end on shutdown, and it still 500s, because ending the wait is
  not the same as surviving the disposal that follows.
- `tests/Muthur.Server.Tests/HubFactory.cs` — `Dispose` calls `base.Dispose`, which stops the host
  (`ApplicationStopping` → `ApplicationStopped`) and only then disposes the container. So the lifetime signal
  is always raised before the objects go, which is what makes the fix reliable rather than a guess.

  **Amendment 1, from the implementer, and the one thing this spec got wrong.** That bullet is true and it
  invites a false inference, which acceptance 1 below then acted on: that a test can call
  `StopApplication()` to raise the signal and look around. It cannot. `WebApplicationFactory` drives
  `Program`'s `app.RunAsync()`, so `WaitForShutdownAsync` observes `ApplicationStopping` and tears the whole
  host down. Written that way, acceptance 1 fails **with the fix in place**, differently on consecutive runs
  — once a 500, once `ObjectDisposedException: 'Microsoft.AspNetCore.TestHost.TestServer'` with no HTTP
  response at all. By the time the request lands there is no server to answer it, and no middleware can fix
  that.

  The state this task is about — *asked to stop, signal up, nothing disposed yet* — has to be held open
  deliberately. A one-shot `IHostedService` whose `StopAsync` blocks on a `TaskCompletionSource`, registered
  through `_hub.WithWebHostBuilder` (the pattern `DashboardOperationsTests` already uses), does it with no
  sleep: hosted services stop before `GenericWebHostService`, so the gate parks the host in exactly that
  state. It must be one-shot, because disposal stops the host a second time and a gate that blocks twice
  deadlocks both calls into the shutdown timeout.

Constraints that are not obvious:

- `IHostApplicationLifetime` is already injectable in the server; `MessageService` takes it.
- The CLI is Native AOT and a pure HTTP client; it passes bodies through and must not learn to parse this one.
- Warnings are errors. **Tests never sleep to synchronize.**

## Non-goals

- The leaked test hub directories themselves. That is T-29, and this task depends on nothing in it — it only
  used its wreckage as evidence.
- Why the test suite has requests in flight at disposal at all. Worth knowing and named in the follow-ups, but
  a hub must answer a shutdown-race request correctly whoever made it, and fixing the suite would hide the
  defect rather than fix it.
- Draining: waiting for in-flight requests to finish before stopping. That is a real thing a server can do and
  it is a much larger change, with its own decision about how long to wait. Named in the follow-ups.
- Any change to `InboxAsync`, to the long-poll contract, or to `MaxWaitSeconds`.
- `muthur down` returning 0 while the process is still alive. Separately reported, separately owned.
- Retrying inside the CLI. Exit 4 already tells a caller what to do.

## Design

### The server: refuse early, and never call it an internal error

`ErrorMiddleware` gains `IHostApplicationLifetime` and two changes.

**First, a short circuit before the request runs at all:**

```csharp
// Asked to stop, so nothing new starts. A request that reached the database here would find the
// connection disposed underneath it and be answered "internal error" — the hub telling an agent it
// broke, when what happened is that it was told to stop.
if (lifetime.ApplicationStopping.IsCancellationRequested)
{
    await StoppingAsync(http);
    return;
}
```

**Second, the same answer for a request that was already past that point** — the race this task is about is
won and lost in microseconds, so the guard cannot be only at the front:

```csharp
catch (ObjectDisposedException) when (!http.Response.HasStarted && lifetime.ApplicationStopping.IsCancellationRequested)
{
    await StoppingAsync(http);
}
```

placed **above** the general `catch (Exception ex)`. Both conditions are required. An `ObjectDisposedException`
on a hub that is not stopping is a real defect and must keep arriving as a 500 with its stack in the log; that
is the difference between this being a fix and being a way to stop hearing about a class of bug.

```csharp
private async Task StoppingAsync(HttpContext http)
{
    http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    await http.Response.WriteAsJsonAsync(
        new ErrorResponse("hub_stopping", "The hub is shutting down and did not do this. Start it again with: muthur up"),
        json.Value.SerializerOptions);
}
```

It is **not** logged as an error. A hub that was asked to stop and then declined a request did nothing wrong,
and an error line here is the same false alarm in the log that the 500 is on the wire.

### The CLI: 503 is "the hub is not there"

`ApiResult.ExitCode` gains one arm:

```csharp
// A hub that is stopping and a hub that has stopped need the same thing from the caller.
(int)HttpStatusCode.ServiceUnavailable => ExitCodes.NotRunning,
```

Nothing else in the CLI changes. The body is already passed through to stderr, so the caller sees
`hub_stopping` and its sentence.

## Units of work

### Unit A — the whole task
- **Files:** `src/Muthur.Server/Infrastructure/ErrorMiddleware.cs`;
  `src/Muthur.Cli/Infrastructure/HubClient.cs`; `tests/Muthur.Server.Tests/SystemTests.cs`;
  new `tests/Muthur.Cli.Tests/ApiResultExitCodeTests.cs`. `DoctorExitCodeTests.cs` in the same project is the
  style to copy but **not** the place to put this: it is about `doctor`'s own extra rule, and 503 is a
  property of `ApiResult` that every command shares.
- **Does:** everything above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. **The policy.** Hold a `HubFactory` hub open in the stopping state with the one-shot gate described in
     Amendment 1, then issue an ordinary request — `PUT /api/v1/outbound-targets`, the endpoint T-41 was
     filed against. It answers **503** with code `hub_stopping`, not 500 and not `internal_error`.

     **Amendment 1 again, because it changes what the mutation check means here.** Mutating this test gives
     **200, not 500**, and that is correct rather than a weak test: the short circuit is a refusal policy,
     not a repair, so with disposal held off there is nothing for the request to trip over. This acceptance
     proves the policy. Item 6 proves the race.
  2. **The long poll.** `GET /api/v1/messages/inbox?wait=...` — the fifteen-of-thirty-two case. Use
     `HubFactory.Clock`'s record of what has started waiting to know the request is really parked before
     stopping the hub; **do not sleep**.

     Two halves, because `InboxAsync` already returns `TimedOut: true` on the stopping signal and so a poll
     that is *already inside its wait* is answered **200**, never 503. That is the Verification section's
     "both are correct" made concrete: assert the in-wait half is **never a 500** and is either
     200-`TimedOut` or 503-`hub_stopping`. Then assert the deterministic half — a **fresh** inbox request
     issued while the hub is stopping is 503 `hub_stopping`.
  3. **A real `ObjectDisposedException` is still a 500.** On a hub that is *not* stopping, an endpoint that
     throws `ObjectDisposedException` answers 500 `internal_error` and logs it. This is the test that keeps
     the fix from being a silencer; write it so its purpose is legible.
  4. **The 503 is not logged as an error.** Assert on the hub's log or its `ILogger` that the refusal in
     acceptance 1 produced no error-level line.
  5. **The exit code.** `new ApiResult(503, ...)` has `ExitCode` **4**, asserted beside the statuses already
     in the table (200, 0, 422, 409, 404, 401) so the whole mapping reads as one thing. Also assert that a
     503's body still reaches stderr through `Output.Emit`, since that is what carries `hub_stopping` and its
     sentence to the caller.
  6. **The race itself** — added by Amendment 1, and the only acceptance whose mutation is a 500. A request
     that is already **past** the short circuit when the stop arrives: a fake `IOutboundChannel` that raises
     the stopping signal and then throws `ObjectDisposedException`, so the failure lands squarely in the
     `catch` arm. 503 `hub_stopping` with the fix, 500 `internal_error` without it, deterministic both ways.
     This is the reported defect reproduced exactly, and it is why item 3's second condition is load-bearing:
     the same fake channel without a lifetime is item 3, so the two sit side by side and the reason for the
     gate on the `catch` is legible from the file.

     Items 1 and 6 cannot be one test. Holding disposal off is what makes item 1 deterministic; letting the
     disposal happen is what makes the 500 appear.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t41
$env:MUTHUR_HOME = "$PWD/artifacts/t41-home"; $env:MUTHUR_URL = "http://127.0.0.1:7462"
./artifacts/t41/muthur.exe up
```

- Register an agent, then start `muthur msg inbox --wait 900` in one shell and run `muthur down` in another.
  The waiting call returns promptly. Before this change it is exit 1 with `internal_error`; after it, it is
  either a clean empty inbox (the long poll's own shutdown path won the race) or exit 4 with `hub_stopping`.
  **Both are correct** — the point is that neither says the hub broke.
- Run it a few times. The race lands on different sides; no run produces a 500.

### What the end-to-end actually showed

Run on an installed build (`artifacts/t41`, published from `6255741`) against a scratch home on port 7462.
The live hub was never contacted.

- **`muthur msg inbox --wait 900` against `muthur down`, 5 rounds:** every round exit 0,
  `{"messages":[],"timedOut":true}`, empty stderr. The long poll's own shutdown path won every time — which
  is why acceptance 2 asserts "never 500" rather than a bare 503.
- **Four tight in-process pollers on `/api/v1/status` during `down`, 6 rounds:** every round caught
  `503 {"code":"hub_stopping","message":"The hub is shutting down and did not do this. ..."}` on the wire.
  The rest got connection-refused. **No 500 and no `internal_error`, in any round.**
- **Six parallel `muthur status` CLI hammers, 6 rounds:** never landed inside the window — always exit 4
  `not_running` from a refused connection. Correct, and the same exit code, which is the whole argument for
  mapping 503 to `NotRunning`.
- **The installed Native AOT CLI against a stand-in hub answering 503 `hub_stopping`:** exit 4, stdout empty,
  the body verbatim on stderr.
- **The scratch hub's `muthur.log` afterwards:** no error line at all.

The window is a few milliseconds and a CLI invocation costs about thirty, so a founder running `muthur down`
will keep seeing exit 4 `not_running`. The 503 protects the request that is **already inside** the hub — the
fifteen-of-thirty-two inbox case this task was filed from.
- The hub's `muthur.log` contains no error line for the refused request.

## Out of scope / follow-ups

- **Draining.** The hub could let in-flight requests finish before it disposes anything, which would make the
  race disappear rather than be answered politely. It needs a decision about how long to wait and what to do
  with a request that outlasts it, so it is its own task.
- **Why the suite has requests in flight at disposal.** Thirty-two occurrences in one day is a lot for a race
  that should be rare; a polling helper that keeps issuing requests after its assertion has already passed is
  the likely source, and it would be worth finding. It belongs with T-29, which is already in the same
  wreckage.
- The `GET /tasks/banana` occurrence is the **dashboard**, not the API, and it went through `ErrorMiddleware`
  rather than the developer exception page. Blazor's own error UI during shutdown is untouched by this task.
- Over 45,000 leaked hub directories is not a number T-29 predicted (it says 100-300 per run). Whoever takes T-29
  should know the real figure, and that each one holds a log worth keeping until it is.
