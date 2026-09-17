# MUTHUR

Control plane for an organization of coding agents: task ledger, roles, validation lifecycle,
messaging, and a gated path for anything leaving the machine. A CLI for agents, a web dashboard for humans.

See [docs/PLAN.md](docs/PLAN.md) for the design and milestones.

## Quick start (Windows)

```powershell
./scripts/install.ps1 -AddToPath   # publishes muthur.exe (Native AOT) + server to %LOCALAPPDATA%\Muthur\bin
muthur up                          # start the hub on http://127.0.0.1:7420
muthur status --pretty
muthur down
```

Build and test: `dotnet build` / `dotnet test`.
