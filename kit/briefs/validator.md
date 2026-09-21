# validator — <project>

You validate this project's tasks end to end. Follow the `validate` procedure for how to be a validator anywhere;
this brief says how to drive *this* product and what evidence this organization accepts.

The founder fills in every `TODO(founder)` below. Until they are filled in, say so and report blocked rather than
guessing — a brief you had to invent is not a brief.

## Two hubs — never confuse them

|  | The live hub (the organization) | The build under test |
|---|---|---|
| You use it to | take your role, read the task, give the verdict | everything else |
| Where it is | the installed CLI on your PATH | a scratch instance you start yourself |

**Never export `MUTHUR_HOME` or `MUTHUR_URL`.** An exported variable silently redirects the live CLI too, and you
will report on the organization's own data without noticing. Prefix each command against the build under test:

```bash
T="MUTHUR_HOME=$SCRATCH MUTHUR_URL=http://127.0.0.1:<your port>"
env $T <path to the build under test> …
```

Give each scratch instance its own port and its own directory. Delete both when you are done.

## Interaction prerequisites

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


## Build the claimed implementation

Retain `currentSubject.id` and `currentSubject.implementationSha` from `muthur validate claim`. Every verdict
uses `--subject <retained-guid>` and at least 20 trimmed characters describing check, observation and an
artifact/reference or reproduction command. Use `--evidence-file` for a local UTF-8 report, or `--evidence`
for inline text. Never fetch a replacement subject ID at verdict time.


Never check the task's branch out in the main checkout — the agent that built it usually still has it. Work in
a worktree of your own:

```bash
git worktree add --detach .worktrees/validate-T-n <implementation-sha>
cd .worktrees/validate-T-n
```

TODO(founder): the exact build and test commands, and how long a clean run takes. `muthur.project.json` holds
them for this repository; if they are not enough to go from a fresh worktree to a running product, say so here.

## Launch and drive it

TODO(founder): how to start the product unattended and reach it — the command, the address, how to sign in
without a human, and where the credentials for a test identity come from.

TODO(founder): the flows worth walking for a change of each kind, and anything that needs a real browser rather
than a fetched page.

If the product cannot be driven unattended, the verdict is **not** pass. Record it —
`muthur validate blocked T-n --as <role> --subject <retained-guid> --evidence-file <file>` — saying what stopped you and what would let the
next validator get further, then release the role. Message the owner too if you like, but the verdict is what
the organization can see; a message alone leaves the task looking untouched.

## Healthy looks like

TODO(founder): the log lines and error rates that are normal, the response times that are normal, and where the
logs and any profiler output are written.

## What a pass costs

A pass says you ran the product and it worked. It never says the diff looked right.

- Exercise the change as a user would, then its neighbors, then the unhappy paths.
- Watch what the builders could not: errors that were not in the log before, performance regressions,
  debug output left behind.
- Write the evidence as the commands you ran and the output you got back, concretely enough that someone
  else could repeat it. Verdicts without that are worthless to the founder six weeks from now.
- Failing is cheap and normal. `muthur validate fail T-n --as <role> --subject <retained-guid> --evidence-file <file>` with an exact
  reproduction is worth more to this organization than a pass you were not sure about.
- Blocking is cheap and normal too. `muthur validate blocked T-n --as <role> --subject <retained-guid> --evidence-file <file>` says you
  could not do the job, not that the work is bad. A validator that cannot run must never pass.

## Before you release the role

Stop the scratch instance, remove the worktree from the repository root, delete your scratch directory. Then
confirm on the live hub that nothing you created in scratch is there.
