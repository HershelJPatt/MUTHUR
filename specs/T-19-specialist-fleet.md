# T-19 — A specialist that can run its own fleet, not just work alone

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.
>
> The investigation that preceded this spec is preserved below under **Investigation**, because two of the
> task's premises turned out to be already settled and the next reader should not re-derive them.

## Goal

A specialist given a subtree too big to carry alone **returns a plan of units** in its report; the
orchestrator staffs them the way it staffs any other unit. Every worker run records **which run it came
from**, so a two-level fan-out is readable in the ledger and attributable in receipts. And a mastermind-tier
worker is finally handed a contract written for a specialist rather than the implementer's.

## The founder's decision (`muthur ask` #18)

Option **C — the specialist proposes, the level above staffs.** Their reasoning, kept because it constrains
the build:

- **A (native subagents) is disqualified by a rule this codebase already holds.** Nothing in
  Contracts/Core/Data/Server names a vendor; harness knowledge lives in `Muthur.Launch` and `kit/<harness>/`.
  A would make *the depth of delegation* a property of which vendor staffed the tier — a claude specialist
  could fan out and a codex one could not. That is vendor capability leaking into the organization chart.
  And it records nothing, in an organization that just built receipts to find out what it spends itself on.
- **B (a worker that launches workers) is not a small relaxation**, and the investigation is what proves it: a
  worker has no identity, and `worker run` needs the hub for the tier catalog, account limits and the run
  report. So B means identities, tokens, quota accounting and a lease model — *a worker that can spend the
  founder's accounts.* It also thins the boundary T-36 landed to protect.
- **C keeps both invariants and still produces a record**, for one field. The cost it names — the specialist
  designs its subtree rather than owning it — is *"smaller than it sounds and probably correct on the merits.
  A specialist's value is expertise about the problem, not process authority."*

And the instruction that is not optional:

> **Do not skip the field.** You found that `worker.finished` records no parent, so a tree is not
> reconstructable today. C must add it — the originating worker or unit — or a two-level fan-out is
> unreadable in the ledger and receipts cannot attribute what it cost. That field is most of the value of
> picking C over A.

## Context

### The specialist tier is half-wired, and C cannot work until it is fixed

`WorkerCommands.cs:127` reads the contract **regardless of `--tier`**:

```csharp
var contract = await File.ReadAllTextAsync(Path.Combine(kit, "core", "implementer.md"), ct);
```

There is no `kit/core/specialist.md`. So today:

- `muthur worker run --tier mastermind` staffs a stronger model and hands it the **implementer** contract —
  *"Implement exactly what the spec says"*, *"do not improvise and do not redesign"* — which is the opposite
  of what a specialist is for.
- The specialist's real instructions exist only in `kit/claude/agents/muthur-specialist.md`, reachable only
  through Claude's native subagent mechanism.

That is exactly the vendor-capability leak the founder disqualified option A for, sitting in the code
already. **C's plan instructions must reach a hub-launched specialist**, so the contract has to be chosen by
tier before any of this works.

### What the ledger records now

`HarnessService.RecordWorkerRunAsync` (`HarnessService.cs:68`) writes `worker.finished` / `worker.failed`
with tier, worker, account, branch, unit, seconds, cost — **and no parent**. `WorkerRunReport`
(`src/Muthur.Contracts/Harnesses.cs:10`) has no field for one, and `WorkerRunDto`
(`src/Muthur.Contracts/Receipts.cs:22`) has none either.

### Investigation — two premises that were already settled

**`worker run` has been exercised.** The task says it never has; it was filed 2026-09-18 04:12 and the runs
happened that evening. Six `worker.*` events: T-5 units A and B (claude, codex), T-15 units A and B (claude,
codex), and two `worker.failed` rows that are **both the same unit** — *"reconcile the billing ledger against
Stripe"*, for a product with neither. That is T-15 exercising the `spec-problem` path on purpose, one run per
vendor, declined in 38 and 22 seconds. Four units built, two impossible units correctly refused, on two
vendors. The precondition is met; do not treat those two rows as defects.

**A worker cannot call `worker run`.** Two independent entries in `WorkerCommands.cs:19` stop it —
`"muthur *"` and `"git worktree*"` — and `worker run` needs the hub besides. This is why B was expensive and
why C exists.

## Non-goals

- Any worker identity, token, or quota model. That is option B, and it was rejected.
- Any use of a harness's native subagent mechanism to get a second level. That is option A, rejected.
- Enforcing a depth limit in code. Under C, depth is structural: only an orchestrator staffs anything, so a
  third level would require an orchestrator to staff a specialist that returns a plan to another orchestrator.
  Say so in the procedure; do not build a counter.
- Any change to `ValidationQueuePanel`, the dashboard, or receipts rendering. The receipts **DTO** gains the
  field; what any page does with it is a later task.
- Changing what `--tier implementer` workers receive. Their contract is correct.

## Design

### 1. `kit/core/specialist.md` — new

The specialist's contract, as a core procedure so it reaches every harness. Start from the text now in
`kit/claude/agents/muthur-specialist.md` (the three paragraphs under `# Specialist`), which is good and stays
in substance, then add the plan section below. End the file with `{{core:implementer.md}}`, exactly as the
Claude agent file does today, so the boundaries and report format carry over.

The section that is the point of this task, in the file's own voice:

```markdown
## When the subtree is bigger than you

You were given a problem area because the judgment is expensive, not because the typing is. If the work you
have found is more than one agent should carry, **do not spawn anything** — you have no authority to, and the
organization has decided that authority stays with the orchestrator that owns the task.

Return a plan instead. Add a `PLAN:` block to your report, one line per unit, in the order they should be
built:

    PLAN:
      <unit name> | <files it owns> | <what it must do, in one sentence> | depends: <unit name or "nothing">

Write each line so an implementer could take it with no more context than the spec and that line — that is
the same bar the spec's own units are held to, and the reason the plan goes through the orchestrator is that
it can be read before anyone spends money on it.

A plan is not a way out of the work. If the subtree is one agent's worth, do it yourself and report no plan.
If you return a plan, say in your report what you have already established — the design you chose and why —
because that is the expertise the units are built on and it does not survive in the lines above.
```

### 2. `worker run` picks the contract by tier

In `WorkerCommands.cs`, replace the fixed read with a tier-dependent one:

```csharp
// A mastermind is given a problem area, not a frozen unit; handing it the implementer's "do not redesign"
// contract is the opposite of why it was staffed.
var contractFile = o.Tier.Equals("mastermind", StringComparison.OrdinalIgnoreCase) ? "specialist.md" : "implementer.md";
var contract = await File.ReadAllTextAsync(Path.Combine(kit, "core", contractFile), ct);
```

`Expand` is **not** applied to this read today and must not start being applied: `WorkerPrompt.Compose` puts
the contract straight into the prompt, so a `{{core:implementer.md}}` token inside `specialist.md` would
reach the worker unexpanded. **Therefore `kit/core/specialist.md` must not rely on the include when read this
way** — resolve it in `worker run` by reading `implementer.md` too and appending it where the token sits, or
by having `specialist.md` carry no token and `worker run` compose the two. **Choose the second**: it keeps
`Expand` a kit-install concern and makes the composition explicit at the one place that needs it.

So `kit/core/specialist.md` carries the specialist text **only**, no include token, and `worker run` composes
`specialist.md + "\n\n" + implementer.md` for the mastermind tier. `kit/claude/agents/muthur-specialist.md`
becomes `{{core:specialist.md}}` followed by `{{core:implementer.md}}`, which `kit install` expands as it
already does.

### 3. The parent field

- `WorkerRunReport` gains `string? Parent` as its **last** parameter, so existing positional construction is
  undisturbed: the branch of the run this one was planned by, or `null` for a run an orchestrator started
  directly.
- `muthur worker run` gains `--parent <branch>`, description: *"The worker run whose plan this unit came
  from. Recorded so a two-level fan-out is readable in the ledger."* Passed straight through to the report.
- `HarnessService.RecordWorkerRunAsync` adds `report.Parent` to the event payload as `parent`. Omit it when
  null rather than writing `parent: null` — the payload is an anonymous object, so build it with the field
  present only when set, matching how `TaskService` handles optional payload fields.
- `WorkerRunDto` gains `string? Parent` as its last parameter, and `ReceiptsService` fills it from the event
  payload. This is what makes the cost of a fan-out attributable, which is the founder's stated reason for
  the field.

Every one of these types crosses HTTP, so each must be registered in `MuthurJsonContext` — they already are;
confirm the added members serialize by running the existing receipts and harness tests.

### 4. `kit/core/orchestrate.md` — the orchestrator's side

In step 4 (*Split and delegate*), after the paragraph about `worker run`, add:

```markdown
   A **mastermind-tier** worker may come back with a `PLAN:` block instead of a finished subtree, when the
   area turned out to be more than one agent should carry. That is the shape working, not a refusal: read the
   plan as you would read your own units, correct it if it is wrong, and staff each line with an ordinary
   `worker run` — passing `--parent <the specialist's branch>` so the ledger keeps the tree and receipts can
   attribute what the fan-out cost. You stay accountable for the result; the specialist supplied the
   expertise, not the authority.

   Two levels is the reference shape. A third would mean a specialist's plan containing another problem area
   rather than units — if you find yourself wanting that, the spec is not frozen enough yet.
```

## Units of work

### Unit A — the specialist contract, and `worker run` choosing it
- **Files:** new `kit/core/specialist.md`; `kit/claude/agents/muthur-specialist.md`;
  `src/Muthur.Cli/Commands/WorkerCommands.cs`; `tests/Muthur.Cli.Tests/` (a new file).
- **Does:** Design 1 and 2.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean, `dotnet test` green, plus tests that the composed prompt for
  `--tier mastermind` contains the specialist text **and** the implementer text, that `--tier implementer`
  contains the implementer text and **not** the specialist text, and that neither contains a literal
  `{{core:` token. `WorkerPrompt.Compose` is a pure static in `Muthur.Launch`, so compose the contract string
  the way `worker run` does and assert on the result rather than launching anything.

### Unit B — the parent field, end to end
- **Files:** `src/Muthur.Contracts/Harnesses.cs`, `src/Muthur.Contracts/Receipts.cs`,
  `src/Muthur.Cli/Commands/WorkerCommands.cs`, `src/Muthur.Server/Services/HarnessService.cs`,
  `src/Muthur.Server/Services/ReceiptsService.cs`, `tests/Muthur.Server.Tests/ReceiptsTests.cs`.
- **Does:** Design 3.
- **Depends on:** nothing. It touches `WorkerCommands.cs` as Unit A does — **Unit A owns the contract read,
  Unit B owns the option and the report construction**; they are in different methods and must not be merged
  into one edit.
- **Acceptance:** `dotnet build` clean, `dotnet test` green, plus tests that a run reported with a parent
  records `parent` in the event payload and surfaces it on the receipts row, and that a run reported without
  one records no `parent` key at all.

### Unit C — the orchestrator's side
- **Files:** `kit/core/orchestrate.md`.
- **Does:** Design 4.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean, `dotnet test` green (it is prose; this is a regression check).

## Verification

```
dotnet build
dotnet test
```

Both clean. Then the install proof, since three of the four files are kit procedures that only matter once
installed — with a CLI built from this branch into a scratch directory:

```powershell
pwsh ./scripts/install.ps1 -Destination ./artifacts/t19
$env:MUTHUR_KIT = "<the task worktree>\kit"
foreach ($h in 'claude','codex','generic') { ./artifacts/t19/muthur.exe kit install --harness $h --repo "<scratch>\$h" }
```

Passing looks like: `When the subtree is bigger than you` present in the installed specialist procedure for
all three harnesses (`.claude/agents/muthur-specialist.md`, `.muthur/procedures/specialist.md`),
`--parent <the specialist's branch>` present in all four installed copies of the orchestrate procedure, and
**no `{{core:` token anywhere** under the scratch directory.

Then the CLI surface:

```
muthur worker run --help          # shows --parent
```

**No browser is needed and none should be used.**

## Out of scope / follow-ups

- **Nothing shows the tree.** The field is recorded and carried on the receipts row; no page draws a
  fan-out. Worth its own task once there is a real two-level run to look at.
- **`worker run` has never been exercised at mastermind tier.** T-5 and T-15 both used `--tier implementer`.
  The first real specialist run is the thing that will find whatever this spec got wrong.
- **A plan is not checked against the spec.** An orchestrator staffing a returned plan is trusting prose that
  no rule validates. Deliberate — the orchestrator reads it, which is the point of routing it through them —
  but if plans ever get long, a `--plan-file` that `worker run` could take unit-by-unit would remove the
  retyping.
