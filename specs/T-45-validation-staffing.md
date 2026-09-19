# T-45 — Validation stops being staffed to sessions that cannot run the spec

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a task that two validator sessions in a row could not run stops being staffed, and the
founder hears about it once, named, instead of once per attempt. Today a browser-dependent spec is staffed
to a browserless session every round until `maxAttempts`: T-5 burned three sessions on the identical
prerequisite, and the third had no information the first did not.

This is the half of T-45 that does not depend on the founder's answer to request #15 (whether harness
candidates gain capabilities, whether a spec may require a browser at all, or whether the attended flag
carries it). Whichever direction is chosen, a repeat block must stop the loop and must not shout.

## Context

The machinery this needs already exists; nothing new is invented here.

- `src/Muthur.Server/Services/LifecycleService.cs` — `VerdictAsync`, the `outcome == Verdict.Blocked` branch
  around line 261. It returns the task to its owner as `InProgress`, records `task.validation_blocked`, and
  posts one message to the owner and one to the founder.
- `src/Muthur.Core/Entities/Entities.cs` — `WorkTask.AttendedReason`, added by T-31.
- `src/Muthur.Server/Services/TaskService.cs` — `SetAttendedAsync` is the shape for writing that field.
- `src/Muthur.Server/Services/ConductorService.cs` — `PlanAsync` already skips a task whose `AttendedReason`
  is set (T-31). That is the whole mechanism by which "stop staffing it" works; this task sets the flag, it
  does not add a second way to skip.
- `src/Muthur.Server/Services/Evidence.cs` — `Evidence.FirstLine`, used by the existing founder message.

Constraints that are not obvious:

- Every state change goes through `Ledger.MutateAsync` and records a ledger event in the same transaction.
- Time comes from the injected `TimeProvider`.
- The blocked branch runs inside an existing `MutateAsync`; the count of previous blocks must be read from
  `m.Db` inside that same transaction, not from a second read.
- Warnings are errors.

## Non-goals

- **Capabilities on harness candidates.** That is request #15's option A and waits for the founder.
- **A contract at freeze time that a spec must be headlessly verifiable.** Option B, same.
- **Classifying *why* a session was blocked.** The evidence is prose written by a validator; a hub that
  parsed it for "no browser" would be guessing. Two blocks is the observable signal, and it is the one
  T-45 itself argues from: the third session had nothing new available to it.
- Changing what `blocked` means, or the return to the owner. T-30 settled that.

## Design

### A second block flags the task attended

In `VerdictAsync`'s blocked branch, after the existing state change and before the messages, count this
task's prior `task.validation_blocked` events. If this block is the **second or later** one and
`task.AttendedReason` is not already set, set it to:

```
Blocked twice without reaching a verdict. Latest: <Evidence.FirstLine(request.Evidence)>
```

and record `task.attended` with `{ reason, source = "validation_blocked", blocks = <count including this one> }`,
so the ledger shows the flag was set by the machine rather than by an orchestrator.

The conductor already refuses to staff a task carrying `AttendedReason`, so this is what stops the loop — and
because T-31 made the flag reversible, `muthur task attended T-n --clear` resumes staffing the moment the
reason stops being true. Nothing here is a trap.

A first block changes nothing about staffing: one session discovering a missing prerequisite is the system
working, and the owner may well fix it.

### The founder hears it once, named

The blocked branch posts a founder message today, every time. Replace that single message with two shapes:

- **First block** — unchanged text, so nothing regresses:
  `<T-n> "<title>" needs something a validator could not supply: <first line>. It is back with <owner>. Full evidence: muthur task show <T-n>`
- **Second block** — the one that says a class of thing is happening and that the hub has acted:
  `<T-n> "<title>" has now been blocked twice without a verdict: <first line>. It is flagged for a human validator and will not be staffed again until that is lifted (muthur task attended <T-n> --clear). Full evidence: muthur task show <T-n>`
- **Third and later block** — **no founder message at all.** The flag is already set and the founder has
  already been told; a task that keeps being blocked while flagged is the flag working, not news.

The owner's message is unchanged in every case. The owner is the one who can act on a single block, and
T-30's wording is theirs.

### Edge cases

- `AttendedReason` already set by an orchestrator: leave it alone. The orchestrator's sentence is better
  than the generated one, and overwriting it would lose why a human said a human was needed. Still send no
  founder message from the third block onward, and send the second-block message only if this block is the
  second — the count decides the message, the flag decides nothing about it.
- A task blocked twice by two different validator roles still counts as two. Two sessions failed to reach a
  verdict on the same task; which role they held does not make the third more likely to succeed.
- The count is over `task.validation_blocked` events for that task, which are append-only, so it only rises.
  A task whose flag is cleared and which is then blocked again is on its third block and sends no message —
  correct, because the founder cleared the flag knowing the history.

## Units of work

### Unit A — the flag and the messages
- **Files:** `src/Muthur.Server/Services/LifecycleService.cs`
- **Does:** everything in Design, inside the existing blocked branch and the existing transaction.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean; the tests in Unit B pass.

### Unit B — the tests
- **Files:** `tests/Muthur.Server.Tests/` — beside the existing blocked-verdict tests (T-30's).
- **Does:**
  - A first block leaves `attendedReason` null, and sends the founder the original message.
  - A second block sets `attendedReason`, records `task.attended` with `source = "validation_blocked"`, and
    sends the founder the "blocked twice" message naming the clear command.
  - The conductor does not staff the task after the second block, and does again after
    `muthur task attended <id> --clear`.
  - A third block sends the founder no message at all, and does not overwrite an orchestrator's own
    attended reason.
- **Depends on:** Unit A.
- **Acceptance:** each test fails with Unit A reverted.

## Verification

Headless in full; no browser, which is the point.

```
dotnet build
dotnet test
```

Passing is 0 warnings, 0 errors, and every test green. The behaviour above is reachable entirely through the
HTTP API that `HubFactory` drives, so there is nothing here a conductor-started session cannot check.

## Open: request #15

The rest of T-45 — whether the conductor can know in advance that it cannot run a spec — is waiting on the
founder. This spec is deliberately complete without it and does not prejudge it: nothing here reads or
writes a capability, and option A, B or C can be built on top without undoing any of it.

## Founder request #15, answered: option C

> C: both halves of A, but the capability lives on the spec and the attended flag only. No harness model
> change: a spec declaring 'needs: browser' is set attended at freeze time and the conductor never staffs it,
> because today no candidate has a browser. If a browser-capable harness ever exists, A is the upgrade path.

Built. The half already on this branch — a second block flags the task, and the founder hears it once — is
what catches a need nobody predicted; this is what catches the one somebody did.

### The declaration

A line of its own, anywhere in the spec: `needs: browser`. Leading list markers, quote markers and emphasis
are allowed, so `- needs: browser` and `**needs:** browser` are the same declaration, and the match is
case-insensitive. Prose *about* a browser is not a declaration — only a line whose whole content is one.

**The value is not interpreted.** Nothing the conductor can staff has a browser, a GUI or hands, so
`needs: gui` and `needs: hands` mean what `needs: browser` means today. Matching a declared capability
against a harness that has it is option A, and is deliberately not built: there is nothing to match against.

### What happens at freeze time

`SetSpecAsync` already resolves the spec's content — T-50 made it read from the branch, the task's own
branch, or the working tree. That same content is now scanned, and a declared need sets `AttendedReason`:

```
The spec declares 'needs: browser'. No unattended session has one, so this waits for a human validator.
```

with `task.attended` recorded carrying `source = "spec_needs"`. The conductor already leaves an attended task
alone (T-31), so that is the whole mechanism — no second way to skip a task, and `muthur task attended <id>
--clear` still lifts it.

A reason somebody wrote by hand is never overwritten. A human's own sentence says more than the generated
one, and the generated one would replace *why* a human is needed with *that* one is.

### One thing the read had to change

The heading check reads the first 8KB on purpose — its question is about the first non-blank line, and a
mistaken path to something enormous should stay cheap to reject. A declared need can be anywhere in the
document, and one the hub silently failed to see would be the worst of both worlds: the orchestrator believes
it declared, and a session gets spent anyway. So the resolver now returns the whole spec (bounded at 1MB) and
the heading check takes its own 8KB window out of it. The existing cap test is untouched and still passes.

### The procedure and the templates

`kit/core/orchestrate.md` and both spec templates now name the declaration where T-46's headless bar already
lives. That is where an orchestrator meets the rule, and the rule was previously "mark it attended
afterwards" — which is exactly the step that kept being skipped.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 159 Cli, 379 Server — all green.

### Tests

Six declaration spellings, prose that is not a declaration, a declaration 20,000 characters in (past the
heading window), and a hand-written reason surviving a freeze that declares a need.

## Amendment after validation of the option-C half (2026-09-19, top-right)

`conductor-validator` failed `c36bc65` on the cap, and it is my own sentence turned against me:

> Real installed CLI accepted a spec with 1,050,000 padding characters followed by `needs: browser`, without
> attendedReason; `task implemented` succeeded and the conductor actually staffed it.

The section above says a declaration the hub silently failed to see "would be the worst of both worlds", and
then introduces a 1 MB cap that does exactly that at 1,048,577 characters. A cap that truncates does not
remove the silent miss; it moves it to a number nobody will think to test. The validator thought to test it.

**A spec too large to read in full is now refused, not read in part:**

```
{"code":"spec_too_large","message":"'specs/x.md' is larger than 1 MB. A spec that size cannot be read in
 full, and a 'needs:' line past the cut would be silently missed. Shorten it, or split what belongs
 elsewhere out of it."}
```

Both reads enforce it. The working tree asks for one character more than the cap, so "it filled the buffer"
and "there was more" are different answers; the branch read has the whole blob already and checks its length.
Nothing is attached when it refuses, so the task is exactly as it was.

The cap stays at 1 MB and keeps doing the job it was added for — a mistaken path to something enormous is
still cheap to reject. What changed is that rejecting is now what happens, rather than reading the first
megabyte and hoping.

### Tests

- `A_spec_too_large_to_read_in_full_is_refused_rather_than_half_read` — the validator's own shape, 1,050,000
  characters of padding then `needs: browser`: `spec_too_large`, and `specPath` still null.
- `A_spec_too_large_is_refused_when_it_comes_off_a_branch_too` — the branch read has its own path to the cap.
- `A_spec_just_inside_the_cap_is_read_in_full` — one byte under, and the declaration is still found. A rule
  that refused ordinary work would be a worse cure than the disease.

Both refusal tests fail with the truncating cap put back, which is the check that they are about this change
and not about the DTO.

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 159 Cli, 382 Server — all green.
