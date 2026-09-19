# T-13 — Kit docs: validators' procedure feedback

> Documentation-only task. No code changes.

## Design

`kit/core/validate.md` gains: evidence is uploaded into the hub (the file may be deleted); a section on what builders' tests usually
miss (failure paths, shutdown with work in flight, more than one of everything, attacking security tasks); how to validate a stack of
tasks from one build. `kit/briefs/muthur-win-validator.md` gains the practical notes collected over six validation runs (per-run
scratch subdirectory, throwaway repos outside the repository, absolute CLI path outside a repo, a second scratch project, tool notes).

## Verification

- `git diff main -- . ':!specs'` touches only `kit/core/validate.md` and `kit/briefs/muthur-win-validator.md`.
- `dotnet build && dotnet test` still green (nothing else changed).
- Install to `./artifacts/validate`; in a throwaway repo outside the repository run `<abs>/muthur.exe kit install --harness claude`
  (no hub needed): `.claude/skills/muthur-validate/SKILL.md` contains the new "What builders' tests usually miss" section; a second run reports every file `unchanged`.
- Read both documents as the person who has to follow them: anything wrong, contradictory or missing is a fail with the line quoted.
