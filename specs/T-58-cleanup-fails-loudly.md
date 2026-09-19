# T-58 — A test run that cannot clean up after itself should fail, not whisper

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

`dotnet test` — the plain command `CLAUDE.md` documents, with no verbosity flag — **fails** when the server
suite could not remove a hub data directory it created. And it stops failing for a reason that is not really
a leak: a delete that loses a race with a virus scanner is retried briefly before it counts as a failure.

## The founder's decision (`muthur ask` #14)

Option **A — a cleanup failure fails the run.** As landed, T-29 writes its summary with
`Console.Error.WriteLine` and touches no exit code, so the suite is at option B: the line is visible at
`dotnet test -v n` and invisible at plain `dotnet test`.

Their reasoning, kept because it is the argument and not just the verdict:

> B is not a compromise, it is a decision not to meet the acceptance sentence. The task says a failed cleanup
> "must say so, at least once per run", and the documented command is plain `dotnet test`. A summary that is
> invisible at that command has not said so; documenting where to go looking for it records the gap rather
> than closing it.

> C makes `Muthur.Server.Tests` print all 349 passing tests on every run. That buries the one line it exists
> to surface and degrades the command every agent and every founder uses, to fix a line that fires
> approximately never. Trading permanent noise for occasional signal is the wrong direction.

> A is what "warnings are errors" already implies. A test run that leaked the state the suite promised to
> clean up is not a clean run, and this organization has spent real money on agents trusting a green suite
> that was hiding something.

**And the answer to the one real objection**, which is what makes A safe rather than flaky:

> Retry the delete a few times with a short backoff before counting it failed. That is real time spent as a
> budget against an external resource — a scanner holding a handle — **not as synchronization with the system
> under test**, so it does not touch the rule that tests never sleep to synchronize.

That distinction has to appear in the code's comment, not only here. `CLAUDE.md` says tests never sleep to
synchronize, and the next reader who finds a `Thread.Sleep` in `TestHubDirectories` will otherwise think the
rule was broken and remove it.

## Context

Everything is in `tests/Muthur.Server.Tests/TestHubDirectories.cs` (T-29, landed).

```csharp
private enum Disposition { Removed, Kept, Failed }

private static Outcome Attempt(string dataDir, bool expectsLoggedErrors)
{
    if (!expectsLoggedErrors && Evidence(dataDir) is { } note) return new(Disposition.Kept, note);
    try
    {
        Directory.Delete(dataDir, recursive: true);
        return new(Disposition.Removed, "");
    }
    catch (IOException ex) { return new(Disposition.Failed, ex.Message); }
    catch (UnauthorizedAccessException ex) { return new(Disposition.Failed, ex.Message); }
}

[ModuleInitializer]
internal static void ReportAtExit() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    foreach (var line in Summary()) Console.Error.WriteLine(line);
};
```

`Release` is called twice per hub — `WebApplicationFactory.Dispose()` reaches `Dispose(bool)` directly and
again through `DisposeAsync()` — and the outcome recorded for a path is the latest one. **That existing
double arrival is not the retry this task adds**, and must not be mistaken for it: it is free, it happens
once, and it already covers the common case of a handle released between the two. The new retry is for a
directory that is still locked after the host has fully shut down.

Expected failure count after T-29's handle fixes is **0**, measured repeatedly. If A starts firing regularly,
a red build is the correct response, because something is leaking again.

## The risk this spec is most likely to be wrong about

**`Environment.ExitCode` set inside a `ProcessExit` handler may or may not survive the VSTest host.** The
documented .NET behaviour is that it takes effect; whether `dotnet test` then reports failure depends on the
test host and the runner between it and the shell.

**Measure it before trusting it.** If it does not work, say so and report rather than inventing another
mechanism — this spec would rather be told it is wrong than have a substitute smuggled in. The same spec
chain on T-8 cost a round because a mechanism was asserted instead of measured, twice.

## Non-goals

- Any change to what is kept. A `Kept` directory is evidence the retention rule deliberately preserves; it
  must not redden anything. Only `Failed` does.
- Any change to the summary's wording, the retention rule, `Evidence`, `LoggedAnError`, or `HubFactory`.
- `scripts/clean-test-temp.ps1`. It is a separate program with its own exit code.
- Any change to `Muthur.Cli.Tests`, `Muthur.Core.Tests` or `Muthur.Launch.Tests`. Only the server suite
  creates hub directories.

## Design

### 1. A bounded retry inside `Attempt`

Replace the single `Directory.Delete` with a small bounded loop. Three attempts, backing off 50 ms, then
100 ms, then giving up — a worst case of 150 ms per directory that genuinely cannot be deleted, and **zero
cost on the happy path**, which is every directory today.

```csharp
/// <summary>
/// Attempts before a delete counts as a failure, and how long to wait between them. This is the one place in
/// the suite that spends real time on purpose: a virus scanner holding a handle for a moment is an external
/// resource, and waiting on it is a budget, not synchronization with the system under test. The rule that
/// tests never sleep to synchronize is intact — nothing here waits for the hub to do anything.
/// </summary>
private const int DeleteAttempts = 3;
private static readonly TimeSpan[] DeleteBackoff = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)];
```

The loop returns `Removed` on the first success, and on the last failure returns `Failed` carrying **the last
exception's message**, as today. Catch the same two exception types and no others.

Use `Thread.Sleep`, not `Task.Delay(...).Wait()`, and not the injected `TimeProvider` — this is deliberately
real wall-clock time against a real external resource, and routing it through the fake clock would make it
instantaneous and therefore useless.

### 2. A failure reddens the run

The decision is a pure function so it can be tested without ending a process:

```csharp
/// <summary>
/// The process exit code this run has earned. A directory the suite could not remove is not a clean run —
/// the same reasoning as "warnings are errors" — while a kept directory is evidence the retention rule
/// preserved on purpose and reddens nothing.
/// </summary>
internal static int ExitCode() => Outcomes.Values.Any(o => o.What is Disposition.Failed) ? 1 : 0;
```

Take `Gate` while reading `Outcomes`, as `Summary()` does.

Wire it into the existing handler, after the summary is written so the operator sees the lines that explain
the failure:

```csharp
[ModuleInitializer]
internal static void ReportAtExit() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    foreach (var line in Summary()) Console.Error.WriteLine(line);
    if (ExitCode() is not 0) Environment.ExitCode = ExitCode();
};
```

Do not set `Environment.ExitCode = 0` when clean — another component may have set a failure code of its own,
and this handler has no business clearing it.

## Units of work

### Unit A — retry, and redden on failure
- **Files:** `tests/Muthur.Server.Tests/TestHubDirectories.cs`,
  `tests/Muthur.Server.Tests/TestHubDirectoriesTests.cs`.
- **Does:** both Design sections.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and tests that:
  - a directory held open with `FileShare.None` **for the whole retry budget** is recorded `Failed`, and
    `ExitCode()` is then non-zero;
  - a directory whose handle is released **after the first attempt but within the budget** ends `Removed`,
    and `ExitCode()` is zero — this is the case the retry exists for, and it is the one worth the effort of
    constructing;
  - a run whose only outcomes are `Removed` and `Kept` has `ExitCode()` zero — a kept directory must not
    redden anything.

  These assert the decision, not the process's real exit code, because a test that actually reddened the run
  would fail the suite it lives in. The real wiring is proven by hand in Verification.

## Verification

```
dotnet build
dotnet test
```

Both clean and green — the expected failure count is 0, so the suite must be unaffected.

Then the thing this task exists for, proven by hand because a test cannot prove it about its own process.
With `TEMP`/`TMP` on a private scratch directory:

1. Run `dotnet test tests/Muthur.Server.Tests` and record `$LASTEXITCODE`. It must be **0**, and the
   `muthur-tests:` summary must report `0 could not be removed`.
2. Make one directory genuinely undeletable for the whole run — the simplest honest way is a temporary
   `[Fact]` that creates a directory under `%TEMP%\muthur-tests`, opens a file inside it with
   `FileShare.None`, holds it for the life of the process, and calls `TestHubDirectories.Release` on it.
3. Run plain `dotnet test tests/Muthur.Server.Tests` — **no `-v n`** — and record `$LASTEXITCODE`.
   It must be **non-zero**.
4. Delete the temporary fact and confirm the suite is green and the exit code is 0 again.

Report the exit code from every step. Step 3 is the whole task: at plain verbosity, with every test passing,
the run must still fail.

**If step 3 comes back 0**, `Environment.ExitCode` does not survive the test host. Stop and report that —
it is a finding, not a failure of nerve, and it is worth more than a workaround invented under time pressure.

No browser is needed and none should be used.

## Out of scope / follow-ups

- **The other three suites have no such check**, because only the server suite creates hub directories today.
  If another one ever does, this mechanism is per-assembly and would need repeating.
- **`kept` still does not redden the run**, by design. If the two directories that legitimately log errors
  ever become many, the question of whether a growing kept count deserves attention is a different one.

## Amendment 1 — the named risk was the real one, and a mechanism that reaches the shell (2026-09-19)

The spec said `Environment.ExitCode` in a `ProcessExit` handler *"may or may not survive the VSTest host"* and
told the implementer to measure it and report rather than substitute. **Step 3 came back 0.** The implementer
stopped, which is exactly right, and measured the finding three ways rather than asserting it:

1. **The handler runs and sets the code.** Re-running step 3 with `-v n` printed, from that same lambda,
   `301 removed, 0 kept, 1 could not be removed` and the `failed` line naming the held directory. The summary
   write and `Environment.ExitCode = ExitCode()` are consecutive statements, so the assignment executed with
   a value of 1.
2. **`dotnet test` still exits 0**, plain and at `-v n`.
3. **The mechanism is not broken in general** — a minimal .NET app with the identical handler exits 1.

So the failure is specific to the runner: `dotnet test` derives its exit code from the **test results**, not
from the test host's exit code. A run where every test passed is reported as success whatever the host
returns. Design section 2 is wired exactly as frozen and is **inert**.

### The replacement: fail the test whose hub leaked

If the only thing that reddens `dotnet test` is a failing test, then a cleanup failure has to *be* one.

`TestHubDirectories.Release` is called from `HubFactory.Dispose`, and `HubFactory` is an `IDisposable` field
on each test class — so xUnit disposes it as part of that test's lifecycle. **Throw from `Release` when a
delete has finally failed**, and the test whose hub leaked fails.

That is better than the global exit code it replaces, not merely equivalent: an exit code says *this run
leaked something*, while a thrown exception says *this test's hub leaked, here is the path and the
exception*. The founder's sentence — *"A test run that leaked the state the suite promised to clean up is not
a clean run"* — is satisfied either way, and only one of the two tells you where to look.

Required shape:

- Throw only after the retry budget is spent, from the `Failed` path and nowhere else. `Kept` and `Removed`
  throw nothing.
- The outcome is still recorded before throwing, so the summary still names the directory and the count is
  still right.
- The exception message names the directory and carries the last exception's message.
- **Verify xUnit surfaces it.** A `Dispose` that throws is reported as a test failure by xUnit 2.9.3 — but
  that is a claim about the runner, and this spec has already been wrong once about exactly that class of
  claim. Measure it before relying on it, and report if it does not hold.

### `Environment.ExitCode` stays, and here is why that is not dead code

Keep the `ExitCode()` function and the assignment. It costs one line, it is correct, and it is the right
answer the moment anything runs this suite through a runner that honours a host exit code. Say in its comment
that it is currently inert under `dotnet test` and why, so the next reader does not spend the afternoon
rediscovering it — that comment is the most valuable line in this task.

### The acceptance criteria I wrote were unsatisfiable

The implementer found that two of the spec's own acceptance bullets contradict its Verification. `ExitCode()`
answers for the **whole run**, so a test asserting it is zero fails as soon as any other directory in that
run has legitimately failed — which Verification step 3 deliberately arranges. Their first step-3 run showed
exactly that: `Failed: 3, Passed: 368`, three `Expected: 0, Actual: 1`.

Their replacement is correct and is ratified: the two "zero" tests assert the outcome **did not change**
`ExitCode()`, which is the causal claim those bullets were reaching for and is immune to what other test
classes are doing in parallel.

### Also ratified

- **A lower-bound timing assertion** (`spent.Elapsed >= 40ms`) in the released-within-budget test. Without it
  the test passes vacuously whenever the handle happens to be released before the first attempt, silently
  degrading into a happy-path test. It can only fail in the direction a loaded machine cannot cause.
- **The deterministic retry fixture.** A recursive `Directory.Delete` that throws still removes every file it
  can, so a marker file's disappearance is an observable "one attempt was made and lost" — the handle is
  released on that condition rather than on a clock, which is what the house rule requires. The watcher gets
  a dedicated `Thread` because the pool is saturated by the rest of the suite.

## Amendment 2 — the mechanism works; where I put it is wrong (2026-09-19)

Unit B reported `spec-problem` and is right. **Do not land its branch as it stands.**

### What is now proven

Amendment 1's runner claim **holds**, measured rather than assumed. A `[Fact]` whose body is
`Assert.True(true)` but whose class leaks on `Dispose` is reported by xUnit 2.9.3 through
`ReflectionAbstractionExtensions.DisposeTestClass`, and **plain `dotnet test` exits 1**. So a thrown
exception does reach the shell where `Environment.ExitCode` does not.

### What is wrong, and it is mine

Amendment 1 said throw *"from the `Failed` path and nowhere else"*. The spec's own **Context**, two sections
earlier, says:

> the outcome recorded for a path is the latest one: a first attempt that fails and a second that succeeds is
> one directory, removed.

**`Failed` is provisional, not a verdict.** Throwing at the first failure destroys the later attempt that was
going to succeed. Amendment 1 changed the mechanism and never re-read the invariant the old one relied on.

Two manifestations, both measured:

**(a) Within one disposal.** The stack shows the *inner* arrival throwing, so the outer arrival — the one
Context says succeeds, after the host releases `muthur.log` while unwinding `DisposeAsync` — never runs.

**(b) A restart test no retry budget can ever win.**
`ConductorTests.The_founders_decision_outlives_the_process_that_heard_it` does
`using var restarted = new HubFactory { DataDir = _hub.DataDir }`, and `_hub` is a class field that stays
alive holding `muthur.log` for the rest of the class. `restarted` disposes first, cannot win, and now throws.
**Deterministic, 3/3**, and green 3/3 with Unit A's files restored at the same commit.

That test is not doing anything wrong. `HubFactory.DataDir` is documented as *"settable so a test can bring a
second hub up over the same database, as a restart does"*. The pattern is intended; the mechanism is what
does not fit it.

### Why this is now a founder question and not a spec edit

The founder chose option A on a stated cost: *"It is a handful of lines in `TestHubDirectories`."* Two rounds
of measurement have falsified that. A throw that is correct has to know the directory is genuinely orphaned,
which means knowing **both**:

- that no other live `HubFactory` shares this `DataDir` — knowable only if `HubFactory` says which are live;
- that this is the final arrival of the double disposal — knowable only from `HubFactory`'s own disposal
  depth.

Both live in `HubFactory`, which this spec lists as a Non-goal, and together they are perhaps twenty lines of
test infrastructure carrying real state, for a signal that fires approximately never. That is a different
trade from the one the founder agreed to, so it is asked rather than assumed: **`muthur ask` #19**.

The retry from Unit A is unaffected by any of this — it is built, green, and independently valuable.

### The lesson worth more than the rule

Unit B's own words, and they generalise past this task:

> This spec has now been wrong twice about the same class of claim, in opposite directions. T-58 named
> `Environment.ExitCode` as "the risk this spec is most likely to be wrong about" and was right; Amendment 1
> told me to doubt the xUnit claim, and that one held. The defect was in the part nobody flagged — an
> interaction with the spec's own Context two sections earlier. **The lesson is less "measure runner claims"
> than "when an amendment changes a mechanism, re-read the invariants the old mechanism relied on."**

Naming a risk is cheap and I did it twice, correctly once. Neither guess was where the defect was.

### Ratified from Unit B

- `HubDirectoryLeakException : IOException` rather than a bare `IOException`, so an asserting test cannot
  pass on an unrelated IO failure and the type name leads the xUnit report.
- The outcome is recorded before the throw, so the summary still names the directory and the count is right.
- The two test renames matching the new mechanism.
