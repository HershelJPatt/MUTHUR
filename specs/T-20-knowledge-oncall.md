# T-20 — knowledge-oncall: fold what an agent learned the hard way into the brief it lives in

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

The organization gains a `knowledge-oncall` role: an agent that reads verdicts, evidence and landed work as
they arrive, notices what was *learned* rather than what was decided, and folds each lesson into the place the
next agent will actually meet it. It ships as a starter brief in the kit, registered in all three harness
manifests, installed into this repository's own `briefs/`, and guarded by a test that the three manifests
still agree on what a repository gets.

## Context

### The founder already settled the design question

The task body asks whether this is a role with a brief or a mechanism in the hub, and answers it in the next
sentence: *"The reference organization chose a role. Cheaper to start there and find out."* **This task builds
the role. It adds no endpoint, no command and no hub behavior.** If the role turns out to need a mechanism,
that is a later task with evidence behind it.

### The role already exists in the procedure; only the brief is missing

`kit/core/oncall.md:3` already lists the roles an agent may hold: *"comms, observability, release,
**knowledge**, a validator…"*. The loop in that procedure — take the role, read the brief, wait on the inbox,
handle what arrives, heartbeat — is the loop this role runs. **Do not edit `kit/core/oncall.md`.** It is
already correct and already anticipates this role.

### The three traps the task names are already folded in, by hand

Measured, not assumed. All three examples in the task body are already in `briefs/validator.md`:

| Trap | Where it already lives |
|---|---|
| `-Destination` defaults to the live hub's directory; `-RestartRunning` takes the organization down | `briefs/validator.md:26-27` |
| A hub started from a shell opened before an environment variable was set is blind to it, silently | `briefs/validator.md:114` |
| Discord's API refuses a bare `Invoke-RestMethod` (40333, then 403) without a `User-Agent` | `briefs/validator.md:116` |

They sit under a section already named **`## Traps this organization has already paid for`**
(`briefs/validator.md:112`). So the work this role does has been done once, manually, by whoever was bitten.
That section is the worked example of this role's output, and the brief must point at it by name. **This task
does not re-fold those three; they are done.**

### How a brief actually reaches an agent, and the founder gate

This matters because the brief must tell its holder the truth about what it can and cannot do:

1. `kit/briefs/<role>.md` is the starter the kit ships.
2. `kit install` writes it to `briefs/<role>.md` with `mode: create` — **it never overwrites a founder's
   edit** — and emits the command to register it:
   `muthur role define <role> --brief-file briefs/<role>.md --founder`
   (`KitCommands.RoleDefineCommand`, `src/Muthur.Cli/Commands/KitCommands.cs:147`).
3. `muthur role define` **requires `--founder`**. An agent cannot register or replace a brief.
4. `muthur role brief <role>` serves the copy stored in the hub (`Role.BriefMd`), not the file on disk.

So editing `briefs/<role>.md` changes nothing an agent reads until a founder re-runs `role define`. The brief
must say so plainly; a knowledge on-call that thinks it has filed a lesson when it has only edited a file is
worse than none.

**Checked, so the brief does not claim a problem that is not there:** every role's hub copy is byte-identical
to its file on disk today (`comms-oncall`, `observability-oncall`, `release-oncall`, `validator`, compared
ignoring line endings — a naive `diff` reports every line as differing, because the hub serves LF and the
files on disk are CRLF). Nothing enforces that, and nothing detects drift; it is a live risk, not a live
defect. It is listed as a follow-up, not fixed here.

### What a knowledge on-call can read

- `muthur log --limit <n>` and `--since <seq>` — the ledger, including validation verdicts.
- `muthur task show T-n` — the full evidence behind a verdict. The organization's own notifications say
  *"Full evidence: muthur task show T-n"* (`ConductorService.cs:445`), so this is the documented path.
- `muthur validate list` — what is waiting.
- Landed commits and the amendment sections of `specs/*.md`, which is where orchestrators record what a task
  turned out to be rather than what it was filed as.

## Non-goals

- Any change to `src/`. No new command, endpoint or hub rule.
- `kit/core/oncall.md` — already correct, already names the role.
- Re-folding the three traps in the task body — already done, see above.
- A doctor check for brief drift, or any automatic sync between `briefs/*.md` and the hub. Follow-up.
- Defining the role on the live hub. That needs `--founder` and is the founder's to run; this task ships the
  brief and names the command.

## Design

### 1. `kit/briefs/knowledge-oncall.md` — new

A starter brief in the shape of the other three on-call briefs: an `# <role> — <project>` heading, a short
statement of why the role exists, a line handing the loop to the `oncall` procedure, `TODO(founder)` markers
for anything only the founder can supply, and a closing section that says when a shift is done. Compare
`kit/briefs/observability-oncall.md` (41 lines) for length, voice and the density of `TODO(founder)`.

Write it to say, in the brief's own words and structure:

- **Why the role exists.** A lesson that cost an agent real time lives in an evidence file nobody opens again,
  and the next agent pays for it a second time. Nobody else is turning what was learned into something the
  next agent will meet.
- **What to read**, naming `muthur log`, `muthur task show T-n` for the full evidence behind a verdict,
  `muthur validate list`, and the amendment sections of landed specs.
- **How to tell a lesson from a decision.** A verdict says what was decided. A lesson is the thing that had to
  be found out first. Fold something when all three hold: it cost someone real time, it will happen again, and
  the next agent has no way to discover it before paying for it. Most findings are none of these.
- **Where it goes — the decision rule, stated as a rule:**
  - true only of this product → the brief of the role that hits it, under
    `## Traps this organization has already paid for`, the section `briefs/validator.md` already uses;
  - true of any project an agent works on here → the matching procedure in `kit/core/`;
  - about how to build, test or run this project → `muthur.project.json`.
- **A file edit is not a filed lesson.** `muthur role brief` serves the hub's copy. Changing `briefs/<role>.md`
  reaches nobody until a founder runs
  `muthur role define <role> --brief-file briefs/<role>.md --founder`.
  The holder's job is to make that one command obvious and correct: land the file change through the ordinary
  task pipeline, then `muthur ask` the founder to run it, quoting the command.
- **Subtract as well as add.** A brief that only grows is a brief nobody finishes. A lesson that stopped being
  true comes out, and saying so is part of the job.
- **TODO(founder)** for: which roles exist in this project and who reads which brief; and whether there is
  anywhere other than a brief, a procedure or the project manifest that this project expects knowledge to land.
- **Done for today.** Everything learned since the last sweep is either folded, filed, or deliberately passed
  over for a reason the holder could defend — and `muthur agent heartbeat --summary "…"` says which.

The prose is the implementer's to write within that structure. Match the register of the existing briefs:
second person, short paragraphs, concrete commands in backticks, no bullet lists more than one level deep.

### 2. Register it in all three manifests

Add to `kit/claude/kit.json`, `kit/codex/kit.json` and `kit/generic/kit.json`, in each case immediately after
the `comms-oncall` entry so the three files stay in the same order:

```json
{
  "from": "../briefs/knowledge-oncall.md",
  "to": "briefs/knowledge-oncall.md",
  "mode": "create"
}
```

No `"validator": true` — this role gives no verdicts, and `RoleDefineCommand` adds no `--validator` flag for a
key that does not end in `-validator`.

### 3. Install it into this repository

This repository is itself a MUTHUR project and carries its own `briefs/`. Add `briefs/knowledge-oncall.md`
as a **byte-identical copy** of `kit/briefs/knowledge-oncall.md`, exactly as `kit install` would write it on
first run. Do not hand-edit one of the two afterwards.

### 4. `tests/Muthur.Cli.Tests/KitRosterTests.cs` — new

The risk this task actually carries is a brief added to one `kit.json` and forgotten in the other two, which
nothing would catch and which would silently give codex and generic repositories a different roster.

- `Every_harness_ships_the_same_briefs` — read all three `kit.json` files from the repository, take the set of
  `to` values that start with `briefs/`, and assert all three sets are equal. Name the harnesses and the
  difference in the failure message. Locate the repository root the way `ProjectManifestTests` does:
  `ProjectContext.FindFile(AppContext.BaseDirectory)`, then its directory.
- `The_knowledge_oncall_brief_is_in_the_roster` — assert that set contains `briefs/knowledge-oncall.md`, and
  that `kit/briefs/knowledge-oncall.md` exists and is byte-identical to this repository's
  `briefs/knowledge-oncall.md`.

Use `System.Text.Json` as `ProjectManifestTests` does. Give the class an XML doc comment saying why it exists:
three manifests hand-edited in parallel drift, and a repository that quietly gets a different roster than its
neighbours is the kind of thing nobody notices until a role cannot be taken.

**Do not touch `tests/Muthur.Cli.Tests/KitInstallTests.cs`.** It does not exist on `main`; it arrives with
T-46, which is in validation. Keeping these separate keeps the two tasks from colliding.

## Units of work

### Unit A — the brief, the manifests, the roster test
- **Files:** new `kit/briefs/knowledge-oncall.md`, new `briefs/knowledge-oncall.md`,
  `kit/claude/kit.json`, `kit/codex/kit.json`, `kit/generic/kit.json`,
  new `tests/Muthur.Cli.Tests/KitRosterTests.cs`.
- **Does:** sections 1–4.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and
  `Every_harness_ships_the_same_briefs` fails if the entry is removed from exactly one of the three manifests.
  Check that by removing it from `kit/codex/kit.json` alone, running the test, then restoring.

## Verification

```
dotnet build
dotnet test
```

Both clean. Then the install proof, from the task branch, into a scratch directory — never against this
repository, and per `CLAUDE.md` using an installed CLI rather than `dotnet run`:

```powershell
pwsh ./scripts/install.ps1 -Destination ./artifacts/t20
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("t20-" + [guid]::NewGuid().ToString("n"))
foreach ($h in 'claude','codex','generic') {
    $repo = Join-Path $scratch $h
    New-Item -ItemType Directory -Force $repo | Out-Null
    $out = & ./artifacts/t20/muthur.exe kit install --harness $h --repo $repo | ConvertFrom-Json
    "{0,-8} brief={1} role={2}" -f $h,
        (Test-Path (Join-Path $repo 'briefs/knowledge-oncall.md')),
        (($out.roles | Where-Object role -eq 'knowledge-oncall').define)
}
```

Passing looks like: all three rows report `brief=True`, and each prints the registration command
`muthur role define knowledge-oncall --brief-file briefs/knowledge-oncall.md --founder` with **no
`--validator` flag**. `kit install` reaches no hub, so this is safe, but run it against the scratch directory
regardless.

Then confirm re-running `kit install` into the same scratch repository reports the brief as `kept`, not
`updated` — `mode: create` is what stops a founder's edits being overwritten, and it is the one thing about a
brief entry that is easy to get wrong.

**No browser is required to validate this task, and none should be used.** It is all command line.

## Out of scope / follow-ups

- **Nothing detects drift between `briefs/<role>.md` and the hub's copy.** They are all in sync today; nothing
  keeps them that way, and `muthur role brief` serves the hub's. A `doctor` check comparing the two is the
  obvious fix and belongs in its own task.
- **An agent cannot register a brief.** `role define` is founder-only, so every lesson this role folds needs a
  founder in the loop. That is a deliberate gate today. If the role's first weeks show the gate is the
  bottleneck, the mechanism question the founder deferred comes back with evidence — which is the point of
  starting with a role.
- **Once T-46 lands**, asserting the knowledge brief arrives through a real `kit install` is one line in
  `KitInstallTests`. Left out here so the two tasks do not collide.

## Proof (2026-09-19)

Integrated branch, `TEMP`/`TMP` on a fresh scratch directory:

```
dotnet build   →  0 Warning(s), 0 Error(s)
dotnet test
  Muthur.Launch.Tests    23/23
  Muthur.Core.Tests      3708/3708
  Muthur.Cli.Tests       57/57      (55 before this task)
  Muthur.Server.Tests    233/233
```

`kit/briefs/knowledge-oncall.md` and `briefs/knowledge-oncall.md` are the same git blob, not merely equal on
disk.

The install proof, with a CLI built from this branch:

```
claude   brief=True status=created
         define: muthur role define knowledge-oncall --brief-file briefs/knowledge-oncall.md --founder
         validator: False
codex    brief=True status=created   (same define line, validator False)
generic  brief=True status=created   (same define line, validator False)
```

No `--validator` flag, as intended — the role gives no verdicts.

Re-running `kit install` over a founder's edit, which is the one thing about a `create` entry that is easy to
get wrong:

```
re-install status: kept
founder edit survived: True
```

The manifest-removal check, run by the implementer: deleting the entry from `kit/codex/kit.json` alone failed
`Every_harness_ships_the_same_briefs` with *"briefs/knowledge-oncall.md is missing from codex"*, and restoring
it returned the suite to green.

`muthur log --since <seq>`, the one command string in the brief that could not be checked by reading the
repository, is real: `--since  Only events after this sequence number.`

No browser was used to validate this task.

## Amendment 1 — two underspecified points, both resolved in the implementer's favour (2026-09-19)

- **Length.** The spec said to compare `kit/briefs/observability-oncall.md` "(41 lines) for length". That was a
  reference, not a target, and the brief is 53 lines. It carries eight required points against
  observability's four, and the implementer cut it twice rather than pad it. Nothing in it is filler; a line
  count is not a quality bar and should not have been written as if it were one.
- **Which roster the second test checks.** The spec said "assert that set contains", singular, without saying
  whose. Checking all three costs nothing and makes the test independent of the first one passing. Kept.
