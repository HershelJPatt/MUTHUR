---
name: muthur-oncall
description: Hold a standing MUTHUR role (comms, observability, release, knowledge, ...) - take the role, read its brief, then wait on the inbox and handle whatever arrives. Use when asked to go on call or to take a MUTHUR role.
argument-hint: "<role>"
---

Role: $ARGUMENTS

{{core:oncall.md}}

## In Claude Code

Run `muthur msg inbox --wait 600` as a foreground Bash call with a timeout above 600 seconds; it blocks without
using tokens and returns when a message for you or your role arrives.
