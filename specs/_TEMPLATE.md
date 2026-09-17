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

Exact commands for the whole task, and what passing looks like:

```
dotnet build
dotnet test
```

Plus anything a validator should exercise end to end (the user-visible behavior, not the unit tests).

## Out of scope / follow-ups

Known work this task exposes but does not do. Each becomes its own ledger task.
