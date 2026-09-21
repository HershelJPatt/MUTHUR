# T-n — <title>

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

One paragraph: what exists after this task that did not exist before, and why it matters.

## Context

What the implementer must know about the existing code: the files and types involved, the patterns to follow
(name a file that is a good example), constraints that are not obvious from the code.

## Non-goals

What is deliberately *not* part of this task, especially things that look adjacent.

## Design

The decisions, already made. Public surface (types, signatures, routes, CLI commands, schema), data flow,
error behavior, edge cases. Be exact: names, status codes, messages where they matter.

## Units of work

Independent pieces that can be built in parallel worktrees. For each:

### Unit A — <name>
- **Files:** created / modified (exact paths)
- **Does:** precise behavior
- **Depends on:** other units or "nothing"
- **Acceptance:** checks the implementer can run and must see pass

## Verification

Opt in to demonstrated launch requirements only when the assignment needs them. Put a standalone line
in the frozen spec, for example `capabilities: shell, build, test, worktree-base, commit`. Multiple lines
union. Other keys: `headless-interaction`, `connector-interaction`, `platform:<name>`, `native-agent-tools`.
Each requires recent evidence for the exact machine/harness/configuration/base/launch path; unknown or
stale evidence refuses launch. Specs without this line retain current routing. See `docs/capabilities.md`.
Explicit `capability probe --task T-n` uses shared catalog/account/budget/capacity admission; do not invent evidence.

Exact commands for the whole task, and what passing looks like:

```
dotnet build
dotnet test
```

Plus anything a validator should exercise end to end (the user-visible behavior, not the unit tests).

Choose HTTP-only assertions for response data and prerendered HTML, connector-driven UI for an available
browser connector, or installed headless interaction for clicks and live behavior. HTTP is render-only,
never interaction evidence. If the connector is unavailable, try the explicit headless probe under existing
permissions before expensive validation builds and before implementation submission. Probe success is not
proof of product behavior: run the spec's exact interaction commands and record their assertions.

Headless specs declare `needs: headless-browser` and exact probe/tool paths and interaction commands.
Legacy `needs: browser` retains attended semantics for compatibility and human visual needs; other needs
remain attended too. Replacing a spec does not clear existing or manual attended reasons.
This repository's supported runner is `scripts/browser-capability.ps1`; generic kit consumers supply their
own equivalent bounded runner. If neither permitted interaction path works, record exact missing tool or
permission evidence and a blocked verdict. Never silently substitute HTML, install tooling, expand permissions,
or start more harness sessions; preserve the shared two-session ceiling.

## Out of scope / follow-ups

Known work this task exposes but does not do. Each becomes its own ledger task.
