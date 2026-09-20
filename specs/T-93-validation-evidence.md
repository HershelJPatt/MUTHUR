# T-93 implementation evidence

Implementation commit: cf9be1a. Owner: codex-overseer-0919. These are owner checks,
not an independent validation verdict.

- `dotnet test tests/Muthur.Server.Tests --filter "FullyQualifiedName~OverseerTests|FullyQualifiedName~Request" --nologo -v quiet`: 41 passed.
- `dotnet test Muthur.slnx --no-restore --nologo -v quiet`: Core 3736, CLI 270,
  Launch 54, Server 666 passed; no failures or skipped tests.
- `scripts/install.ps1 -Destination C:\WorkSrc\MUTHUR\artifacts\t93 -Ref cf9be1a`:
  Native AOT CLI and server publish succeeded from a detached committed source.
- Installed scratch hub: absolute home `C:\WorkSrc\MUTHUR\.work\t93\scratch`,
  URL `http://127.0.0.1:7497`, background services disabled, outbound credentials
  removed. No model sessions launched. Scratch hub shut down after checks.
- Native `ask` with omitted kind returned `triage` plus route reason. Explicit
  human returned `human`. Native triage serialization reached the service and an
  ordinary agent was correctly refused with `unauthorized`.
- Headless Chrome through Playwright: `/needs-you` displayed all three route labels
  and reasons. A technical request created from the native CLI appeared without
  reload, then disappeared after cancellation. No page errors. Local reproducible
  driver: `C:\WorkSrc\MUTHUR\.work\t93\browser-check.cjs`; screenshot alongside it.

API tests exercise the actual authenticated current overseer without launching a
paid session: default ask → triage → decide → unblock for the three engineering
incidents; classification itself leaves the task blocked. Explicit/legacy human
routes cannot be downgraded. Human classification cannot be reversed or answered.
Ordinary/stale callers, missing evidence and closed requests are refused. Routes
persist in list output; classifications carry the overseer actor in the ledger.
Acknowledged human classification does not wake an unchanged overseer again.

Semantic judgment remains the agent's responsibility; this is not a keyword-based
human-decision classifier. The API enforces recorded routes and session authority.
No existing human request is migrated. Deployment must preserve current conductor
limits and refresh the built-in overseer brief through configuration; custom briefs
are preserved. Independent validation and green pinned-main checks precede landing.
