# T-36 — a CLI test that invokes a command action could post to the live hub

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, invoking a real command action from `tests/Muthur.Cli.Tests` is safe whatever the action
does, and it is safe without any test having to remember to make it so. Today no CLI test exercises an action
end to end, because doing it would aim the action at whatever hub the test process inherits.

## The defect, and why clearing the environment is not the fix

Raised by the implementer fixing T-31, which declined to write a test I had implied:

> Invoking the action means that any regression (or the mutation check itself) would run the post against
> whatever `$MUTHUR_URL`/`$MUTHUR_HOME` the test process inherits — on this machine that is plausibly the
> live hub with a real founder token, and the request under test is "clear the attended flag on T-1".

It was right, and a test written to prove a command is safe would, on failing, perform the unsafe act against
production.

**The important subtlety, and the reason this needs a fixture rather than a convention:** unsetting the
environment does not help. `MuthurEnvironment.Url` falls back to `DefaultUrl`, which is
`http://127.0.0.1:7420` — the live hub's own address. A test process with a completely clean environment
points at production by default. Safety therefore requires *setting* the variables to something harmless, not
removing them.

`HubClient`'s `BaseAddress` is read from `MuthurEnvironment.Url` in its constructor, and
`Globals.ResolveToken` reads `MUTHUR_TOKEN`, then falls back to the founder token file under
`MuthurEnvironment.Home`. So both the address and the credential come from process environment at call time.

## Non-goals

- Do not change anything under `src/`. The CLI is correct; the test project's environment is the problem.
- Do not weaken or rewrite T-31's existing structural test. It pins the refusal decision for real argv
  through the real command tree, and the implementer's reasoning for preferring it stands. This task adds a
  capability beside it; it does not replace it.
- Do not add a network mock or an HTTP interception layer. The point is that an action which *does* reach the
  network reaches nothing, not that it cannot try.
- Do not make the isolation opt-in per test class. A safety property that must be remembered will be
  forgotten, which is the whole lesson of the task that raised this.

## Design

### The fixture: a module initializer, so nothing can forget it

New file `tests/Muthur.Cli.Tests/HubIsolation.cs`:

```csharp
/// <summary>
/// Every CLI test runs with the hub environment pointed at nothing. A clean environment is not safe:
/// MuthurEnvironment.Url falls back to the live hub's own default address, so safety means setting these
/// variables rather than clearing them. A module initializer runs before any test in the assembly, so no
/// test class has to opt in and none can forget.
/// </summary>
internal static class HubIsolation
{
    /// <summary>Port 1 is reserved and never listened on: a connection attempt fails at once rather than hanging.</summary>
    public const string UnreachableUrl = "http://127.0.0.1:1";

    [ModuleInitializer]
    internal static void Isolate() { ... }
}
```

`Isolate()` must, using `Environment.SetEnvironmentVariable`:

- set `MUTHUR_URL` to `UnreachableUrl`;
- set `MUTHUR_HOME` to a fresh empty directory under `Path.GetTempPath()` (`muthur-cli-tests/<guid>`),
  created by the initializer, so no founder token can be found;
- set `MUTHUR_TOKEN` and `MUTHUR_AGENT` to `null` — these are the two that would supply a real credential.

It does not delete the directory. A few empty directories per run are worth less than the complexity of
tearing one down from a module initializer, and T-29 owns temp-directory hygiene.

### The tests that prove it

New file `tests/Muthur.Cli.Tests/HubIsolationTests.cs`:

1. `The_hub_environment_points_at_nothing` — assert `MuthurEnvironment.Url` equals
   `HubIsolation.UnreachableUrl`, and that `MuthurEnvironment.Home` is an existing directory containing no
   `founder.token`. This is what proves the initializer ran at all, and it is the test that fails if a future
   change removes it.

2. `An_action_that_reaches_for_the_hub_reaches_nothing` — build the real command tree the way `Program`
   does, invoke a **read-only** command that talks to the hub (`task list`), and assert the exit code is
   `ExitCodes.NotRunning` (4). Read-only deliberately: the assertion is that nothing was reached, and the
   first version of this test should not be the one that proves it by attempting a write.

   Use the real root command from `Muthur.Cli`. If it is not reachable from the test assembly, say so in
   your report rather than duplicating the command tree — a copy would drift and prove nothing about the
   real one.

3. `A_write_action_reaches_nothing_either` — the same, with a command that mutates
   (`task attended T-1 --clear --founder` is the exact request the T-31 implementer refused to aim at
   production). Assert exit 4. This is the capability the task exists to unlock, and it is safe **only**
   because of the fixture — which is why test 1 sits above it.

### What a validator should check, and it is not a test

That the isolation holds when the environment is hostile rather than absent. Run
`dotnet test tests/Muthur.Cli.Tests` with `MUTHUR_URL` and `MUTHUR_TOKEN` exported to real values — the live
hub's address and a real token — and confirm the suite still passes and the live hub records nothing. A
module initializer overwrites an inherited value; that is the claim, and it is only worth anything if someone
checks it with a hostile environment rather than a clean one.

## Units of work

### Unit A — the whole change
- **Files:** created `tests/Muthur.Cli.Tests/HubIsolation.cs`,
  `tests/Muthur.Cli.Tests/HubIsolationTests.cs`
- **Does:** the Design section above, exactly.
- **Depends on:** nothing.
- **Acceptance:**
  - `dotnet build` clean, `dotnet test` green. `Muthur.Cli.Tests` rises from 40 by the number of tests added.
  - `git diff --stat -- src/` prints nothing.
  - **Prove the fixture is load-bearing**: temporarily make `Isolate()` do nothing, and confirm
    `The_hub_environment_points_at_nothing` fails. Do not leave that state, and do not attempt the proof by
    aiming a write at a reachable hub.
  - Confirm from the test output that the two action tests exit 4 rather than passing for some other reason —
    an action that fails to parse also does not reach a hub, and would pass a careless assertion.

## Verification

```
dotnet build
dotnet test
```

Plus the hostile-environment run described above, which is the orchestrator's or the validator's, since it
involves the live hub's real address.

## Out of scope / follow-ups

- The same hazard exists for any future test project that references `Muthur.Cli`. If a second one appears,
  the initializer belongs in a shared test-support assembly rather than copied — copying it is how the two
  drift, and one of them being wrong is indistinguishable from both being right until something writes to
  production.
- `MuthurEnvironment.DefaultUrl` being the live hub is correct for the CLI and is the reason this fixture
  must set rather than clear. Whether a test-time default should exist at all is a larger question about
  `Muthur.Contracts` and is not this task's.

## Proof (2026-09-18)

```
dotnet build   0 warnings, 0 errors
dotnet test    Launch 23 + Cli 43 (was 40) + Core 3708 + Server 228, all passing
git diff --stat -- src/   empty
```

### The hostile-environment run, which is the only one that tests the claim

Run by the orchestrator, since it involves the live hub's real address and a real founder token. The claim
under test is that a module initializer **overrides an inherited value** — proving it against a clean
environment proves only that it fills an absent one.

```
live ledger seq before: 907
MUTHUR_URL=http://127.0.0.1:7420  MUTHUR_TOKEN=<real founder token>  MUTHUR_HOME=<the live home>
  dotnet test tests/Muthur.Cli.Tests   ->  43 passed, 0 failed
live ledger seq after:  907
```

The suite passed with the live hub's address and a real founder credential exported, and the live ledger did
not move. That is the property this task exists for.

### The fixture is not theoretical

The implementer checked, with existence probes only and no requests: its own shell had `MUTHUR_URL`,
`MUTHUR_HOME`, `MUTHUR_TOKEN` and `MUTHUR_AGENT` **all unset** — the clean-environment case this spec warns
is the dangerous one — while port 7420 was listening and `founder.token` and `muthur.db` existed. So before
this commit, `task attended T-1 --clear --founder` from that assembly would have cleared the flag on the
live hub, authenticated as the founder.

### Exit 4 means nothing was reached, not that parsing failed

Both, because the spec flagged that a parse failure would pass a careless assertion:

- **In the test**: `Assert.Empty(parse.Errors)` runs *before* `InvokeAsync`, so a command that fails to parse
  cannot reach the exit-code assertion at all.
- **Observed**: detailed output printed one body per action test —
  `{"code":"not_running","message":"MUTHUR is not reachable at http://127.0.0.1:1. ..."}` — a string produced
  only inside `HubClient.SendAsync`'s catch of `HttpRequestException`, i.e. only after a real connection
  attempt, and naming `127.0.0.1:1` rather than 7420.

### An honest gap in the coverage

`src/Muthur.Cli/Program.cs` is a top-level program, so its root command is built inside the compiler-generated
`<Main>$` and no test can call it. Rather than duplicate the tree — which would drift and prove nothing about
the real one — the helper calls the same real factories `Program` calls (`Globals.AddTo`, `TaskCommands.AddTo`)
onto a fresh `RootCommand`. Every command, option and action under test is the production object. **What is
not covered is `Program`'s composition order**, and adding a `BuildRoot()` to reach it would have meant
changing `src/`, which this spec forbids. Recorded rather than quietly accepted.

### The load-bearing proof, done at the safest moment

`Isolate()`'s body was emptied *before the two action tests existed in the file*, so nothing that could run
in the unsafe state was present. The environment test then failed with
`Expected: "http://127.0.0.1:1"  Actual: "http://127.0.0.1:7420"` — this spec's central claim demonstrated
literally — and was restored immediately.
