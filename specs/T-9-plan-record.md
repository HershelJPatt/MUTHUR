# T-9 — docs/PLAN.md catches up with the ledger, in its own voice

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task `docs/PLAN.md` is an accurate record of the decisions this organization has made and why,
including the ones made since it was last touched. Nothing in it points at a task id that now means
something else, the decision it still lists as open is recorded as decided with what it cost, and the claim
§10 was written to make possible — that the hub is built by the process it implements — is stated as a fact
that happened rather than an intention.

**This is a design record, not a changelog.** Every addition says what was decided and why. If a sentence
would only tell a reader that something happened, it does not belong.

## What is wrong, verified against the ledger

The filed task names three things. There is a fourth, worse than the other three, found while reading the
document against the live board:

### 1. Every task id in the milestone table now names a different task

`§0`'s table cites `(T-1)`, `(T-2)`, `(T-6)`, `(T-7)`, `(T-9, T-10)`, and the status line says
`see specs/T-9.md`. The ledger was rebuilt and its ids reused. Today:

| The plan says | It means | `muthur task show` today gives |
|---|---|---|
| M3 dashboard, done (T-1) | the dashboard spec | "Fix the README's outbound-credential paragraph" |
| M4 roles/validation/landing, done (T-2) | `GitLander` | "Ship an on-call roster in the kit" |
| M6 multi-harness, done (T-6) | `Muthur.Launch` | "Agents rail on the dashboard" (still in the backlog) |
| M7 ingest, done (T-7) | GitHub issues polling | "Census: recurring sweeps" (still in the backlog) |
| M8 outbound gate, done (T-9, T-10) | the gate | **this task**, and "Discord ingest is a firehose" |

So the design record's own citations are not merely stale — they point a reader at unrelated work, and one
of them points at the task fixing them. This is the same defect as T-25, arriving in the document rather
than in `specs/`.

**Do not fix it by citing the renamed spec files.** T-25 renames them to `<id>-<slug>.md`, and T-25 is still
in validating — a plan that links `specs/T-9-outbound-gate.md` breaks if T-25 is failed or changed. Cite by
subject instead (see Design), which is true regardless.

### 2. The M7 row reads as though `gh` were the only adapter

`M7 ingest | done (T-7) | cursor polling, GitHub issues via gh, push API, claim/convert/dismiss. Azure
DevOps adapter: not yet`. Discord ingest landed since, and `IInboundSource` has now been implemented twice,
which is the fact that matters: the seam is proven, so each further channel is contained work rather than a
design question.

### 3. Open decision #3 is decided

> **Answer channel** — browser "Needs you" page only, or also Discord/WhatsApp so you can unblock agents
> from your phone (pulls part of M7 forward).

Decided: both. `discord:` ingest landed, and `discord-webhook` has been an outbound target since M8. The
task's instruction is explicit and must be followed: **record the decision and what it cost, rather than
deleting the question.**

### 4. The status line, and what has landed since M8

`Status: M0–M8 built and landed · 2026-09-17 · M8 passed validation in round 17 (see specs/T-9.md) · M9
(deployable, multi-machine) not started`.

M9 is still not started, and the milestones are still the milestones — none of the work since is an M9
deliverable. What is missing is that a substantial amount of work landed after M8 that the plan does not
acknowledge at all.

## Context

- `docs/PLAN.md`, 270 lines. Read all of it before changing a word; the voice is the deliverable.
- `§0 Where the build ended up` — the table, **What the process itself taught**, and **Differences from the
  plan below**. The last of these already says *"validation throughput, not implementation, is the
  bottleneck — more than one validator session is the first thing to add when a project gets busy."*
- `§9 Rules that keep it deployable`, `§10 Milestones` (its preamble makes the "project #1 is this repo"
  claim), `§11 Open decisions`.
- The ledger is the source of truth for what landed: `muthur task list --state done --founder`. As of
  2026-09-18 that is T-1, T-2, T-4, T-11, T-12, T-30 — **by today's ids**, which is exactly the confusion
  above, so describe them by subject in the document.
- `README.md` is the user guide and is not this task's to change.

## Non-goals

- **Open decisions #1 and #2 stay open, untouched.** The first external project after the hub, and what
  validation means for it, are the founder's and have not been settled. Do not narrow, answer or reword them.
- No new milestone, and no change to what M9 means. Nothing landed since M8 is an M9 deliverable, and
  inventing an "M8.5" is a roadmap decision, not a documentation one.
- No changes to `README.md`, `kit/`, or any spec.
- No changelog. No dated list of landed tasks. No table of everything that happened today.
- Not a rewrite. The document's structure, section numbering and tone stay as they are.

## Design

Six changes, all inside `docs/PLAN.md`.

### A. The status line

Keep its shape. Update the date to the day the change is made, drop the broken `see specs/T-9.md`, and say
that work has landed beyond the milestones. Something with this content, in this voice:

```
Status: M0–M8 built and landed · <date> · M9 (deployable, multi-machine) not started ·
the organization has since been running on itself, which §0 records
```

The exact wording is the writer's; the constraints are that it must not cite a task id, must not claim M9
progress, and must point the reader at where the post-M8 record lives.

### B. The milestone table's citations

Replace each parenthetical id with the subject of the work, so the citation survives the ledger. For
example `done (T-1)` for M3 becomes `done` with the "Built as" column already describing it, or `done (the
dashboard task)` — whichever reads better in the table's rhythm. **No bare `T-n` may remain in the table.**

Add one sentence immediately under the table recording *why* the citations look like that, because it is a
real fact about this organization that will confuse every future reader:

> The ledger was rebuilt once and its task numbers reused, so a task id in this document would name
> different work today than it did when it was written; the milestones are cited by subject instead.

That sentence is a decision and its reason, which is what this document is for.

### C. The M7 row

Record that the ingest seam has been proven twice and what follows from it — one adapter is a channel, two
is evidence the seam is right, and each further channel is contained work. Azure DevOps remains not yet.

### D. What the process itself taught

This list is the most valuable part of the document — findings the builders' tests missed. Add the ones
learned since, each as a lesson with its cause, not as an incident report. The material, in the order they
matter:

- **A validator that correctly refuses is invisible unless refusing is a verdict.** A spec required a live
  browser; conductor-started sessions have none. Five sessions in twenty-eight minutes each did exactly what
  the brief said — messaged the owner, released the role, recorded nothing — and the conductor restaffed
  each time, because a task in `validating` whose role nobody holds is precisely what it exists to fill.
  Neither existing cap could see it: the launch succeeded, and no verdict was recorded. `blocked` is now a
  verdict, so the task leaves `validating` and the loop closes at the source rather than at a cap.
- **A spec that cannot be validated by the sessions the organization actually starts is a defect in the
  spec.** The same browser requirement was written twice, by the same orchestrator, after the first one had
  been diagnosed. The dashboard is Blazor Server and prerenders, so the honest check is an HTTP GET — except
  for cards inside `<Virtualize>`, which render nothing server-side.
- **A file the tooling reads without complaint is the most expensive kind of wrong.** Three instances in one
  day: `muthur.project.json` failed to parse and had therefore never been read; a spec was attached to a
  task whose id had been reused; and `install.ps1` published whatever working tree it was run from, which
  agents move constantly, so a reinstall silently produced the previous build. Each succeeded and said
  nothing.
- **The brief on a running hub is a copy.** `role brief` serves what is in the database, not what is on
  disk, so editing a brief changes nothing for a running organization until it is re-served — and re-serving
  it from a moving checkout can serve the old text back.

Keep each to the length of the entries already there. They are dense; match that.

### E. Differences from the plan below — the throughput line

That section already predicted the bottleneck. Record that it arrived, and what was decided about it: a
validator role now carries a concurrency limit, the exclusivity moved from the role to a claim on the
(task, role) pair, and roles that are genuinely one seat — the on-calls — stayed single-holder. Say why the
distinction holds: a validator role is a skill, not a seat.

Note in the writing that this is in validating rather than landed at the time of writing; if that is no
longer true when the change is made, say it plainly. **Check the ledger rather than trusting this spec.**

### F. §11, open decision #3

Do not delete it. Convert it to a recorded decision under the same number, keeping the question visible and
adding the answer and its cost:

- Both channels, not one: `discord:` ingest and a `discord-webhook` outbound target.
- What it cost: ingest is a firehose — every message in the watched channel becomes an inbound item, and
  mentions arrive as raw ids rather than names. That is filed and unfixed.
- The reason the question was worth asking: being able to unblock an agent from a phone is the difference
  between an organization that runs while the founder is away and one that does not.

Keep #1 and #2 exactly as they are, and keep the numbering.

### G. §10's preamble, and the claim it was written to make

`§10` opens: *"**Project #1 is this repo** — the hub is built by the process it implements, which keeps
company-repo policy out of the way until the loop is proven."* Add, in one or two sentences, that this
happened: tasks have been specified, built by delegated implementers, independently validated by agents on
other sessions and other vendors, and landed by the hub — and that the loop has now also caught its own
failures, which is the stronger claim. The conductor staffing validation unattended is the part worth
naming, because it is what "the loop is proven" was reaching for.

## Units of work

### Unit A — the whole task
- **Files:** `docs/PLAN.md` only.
- **Does:** A through G.
- **Depends on:** nothing. Do **not** depend on T-25 landing.
- **Acceptance:**
  - `dotnet build` and `dotnet test` still clean — nothing here is compiled, so the check is that nothing
    else was touched.
  - `grep -nE '\(T-[0-9]+' docs/PLAN.md` finds nothing in the milestone table.
  - `grep -n 'specs/T-9.md' docs/PLAN.md` finds nothing.
  - `§11` still has three numbered entries, #1 and #2 are byte-identical to before, and #3 contains both the
    original question and the decision.
  - The document is still 11 sections with the same headings, and no section was reordered or renumbered.
  - `git diff --stat` shows `docs/PLAN.md` and nothing else.

## Verification

```
dotnet build
dotnet test
```

Both clean, and `git diff --stat` naming only `docs/PLAN.md`.

Then read it. This task's real verification is a human or a docs validator reading `§0` and `§11` and
finding a record of decisions and their reasons — not a list of things that happened. Specifically:

- Every claim in the new text is checkable against the ledger (`muthur task list --state done --founder`,
  `muthur task show T-n`). Nothing asserts a task landed that has not.
- No task id in the document names work other than what the sentence is about.
- Open decisions #1 and #2 are untouched.
- The document reads as one voice. A reader should not be able to tell which paragraphs are new.

## Out of scope / follow-ups

- `README.md` may have the same reused-id problem; it was not audited here.
- If T-25 lands, a later sweep could point the plan at `specs/README.md` for the naming convention. Not
  worth coupling this task to that one.
- The plan has no record of the receipts question — what the organization spent itself on — because nothing
  answers it yet. That is T-17.
