# MUTHUR

Control plane for an organization of coding agents. Design and milestones: `docs/PLAN.md`.

## Layout

- `src/Muthur.Contracts` — DTOs, enums, routes, source-generated JSON context. AOT-safe; every type that crosses HTTP is registered in `MuthurJsonContext`.
- `src/Muthur.Core` — entities and pure rules (state machine, leases, gate). No EF, no HTTP.
- `src/Muthur.Data` — EF Core model + migrations (`dotnet ef migrations add <Name> -p src/Muthur.Data -s src/Muthur.Data -o Migrations`).
- `src/Muthur.Server` — application services (`Services/`), minimal-API endpoints (`Api/`), Blazor Server dashboard (`Components/`).
- `src/Muthur.Cli` — `muthur.exe`, Native AOT, a pure HTTP client. No reflection-based JSON, ever.
- `src/Muthur.Launch` — harness adapters (claude, codex, local).
- `kit/` — agent procedures (`core/`) and per-harness adapters.

## Rules of the codebase

- Every state change goes through `Ledger.MutateAsync` and records a ledger event in the same transaction. Reads go through `Ledger.ReadAsync`.
- Services throw `MuthurException` via `Fail.*` with a stable `code`; never return error tuples. Rule violation → 422/exit 2, conflict → 409/exit 3.
- Time comes from the injected `TimeProvider`, never `DateTimeOffset.UtcNow`, in server and core code.
- Nothing in Contracts/Core/Data/Server names a model vendor. Harness knowledge lives in `Muthur.Launch` and `kit/<harness>/`.
- Dashboard components never touch `MuthurDb`; they call the same services the API uses. Live panels inherit `LivePanel`. Styling uses only classes from `wwwroot/app.css`; add tokens/classes there rather than inline styles.
- Warnings are errors. Match the surrounding style: file-scoped namespaces, primary constructors, expression bodies where they read well, XML doc comments only where they say something the name doesn't.
- Tests: `tests/Muthur.Core.Tests` for pure rules; `tests/Muthur.Launch.Tests` for harness adapters; `tests/Muthur.Server.Tests` drive the real HTTP API through `HubFactory` (isolated temp data dir, fake clock, fake PR opener and ingest source, real temp git repos via `TestRepo`). New behavior comes with tests.
- Anything that talks to the outside (git, gh, harness CLIs, HTTP) sits behind an interface (`IProcessRunner`, `ITaskLander`, `IPullRequestOpener`, `IInboundSource`, `IOutboundChannel`) so it can be faked.
- Never run a development build against the live hub: prefix every command with a scratch `MUTHUR_HOME` and `MUTHUR_URL`, and only run installed CLIs (`scripts/install.ps1 -Destination ./artifacts/<name>`).

## Commands

`dotnet build` · `dotnet test` · `pwsh ./scripts/install.ps1 -Destination ./artifacts/muthur`
