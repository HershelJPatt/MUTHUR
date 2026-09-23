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

Most specs need no launch requirements: leave them out, and dispatch works as it always has. Only an
assignment that must prove a harness can do something unusual (drive a browser, run a native agent tool) opts
in, with a standalone line that starts with the word capabilities, a colon, and the keys it needs, as
`docs/capabilities.md` describes. Do not write that line as a matter of course: every key on it demands recent
recorded evidence for the exact machine and harness, and a spec that names capabilities nobody has probed
refuses to dispatch at all. Evidence comes from `capability probe --task T-n`, never from writing it down.

Exact commands for the whole task, and what passing looks like. Each fenced line is run as one command, through
PowerShell on Windows and sh elsewhere, so write commands that work in both (tool invocations such as `git`,
`dotnet` or `npm`, not shell syntax such as `test -f`, `[ ... ]` or `&&`). A file a command reads must be in some
unit's **Files** or already in the repository; `task spec` refuses anything else:

```
dotnet build
dotnet test
git grep -q -e '^expected$' HEAD -- path/to/file.txt
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
