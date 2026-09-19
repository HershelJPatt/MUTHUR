# T-53 — `clean-test-temp.ps1` reads its own root literally

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task the root check in `scripts/clean-test-temp.ps1` treats its path as a name rather than as a
pattern, like every other path operation in the file. T-29 closed the bracket hazard with `[IO.File]::Exists`
and `Remove-Item -LiteralPath`; line 29 was missed, and a half-applied rule is worse than none — the next
reader sees `-LiteralPath` at line 132 and assumes the file was handled.

## Context

- `scripts/clean-test-temp.ps1` line 29: `if (-not (Test-Path $Root))`. `Test-Path` without `-LiteralPath`
  reads `[` and `]` as a wildcard character class.
- Line 114 is the pattern the file already settled on: `[IO.File]::Exists($log)`.
- Line 132: `Remove-Item -LiteralPath $dir -Recurse -Force`, with the comment saying why.
- Line 91: `[IO.Directory]::EnumerateDirectories($Root)` — the root is always a directory, and the wildcard
  match is what lets a *different* directory satisfy the guard before this line throws on the literal one.
- There is no test project for either script in `scripts/`; the file's own history records proofs by run in
  the spec, which is what this task does.

Constraints that are not obvious:

- The script's contract, stated in its own header, is that every run ends with its counts. Nothing here may
  change the summary, the counters, or any exit code on a path that works today.

## Non-goals

- **T-54**, the sibling finding from the same validator pass: with `-All` *and* `-OlderThanMinutes 0` the
  script half-deletes a live hub and reports it as `failed`. That is a different defect with an unchosen
  design decision attached, and merging the two would hide a one-line fix inside a judgement call.
- Adding a test harness for PowerShell scripts. Worth doing one day; not on the back of a one-line fix.
- Any other use of `Test-Path` — there is none left in the file.

## Design

Line 29 becomes:

```powershell
if (-not [IO.Directory]::Exists($Root)) {
```

**`[IO.Directory]::Exists` rather than `Test-Path -LiteralPath`**, which is the other one-token fix and the one
T-53 suggests. Both stop the wildcard. This one is chosen because:

- It is the pattern the file already uses at line 114, so the rule reads as applied rather than as applied
  twice in two dialects.
- It answers the question actually being asked. The next statement enumerates *directories* in `$Root`; a
  plain file sitting at that path satisfies `Test-Path` and then throws at line 91. `[IO.Directory]::Exists`
  returns false for it and the script says "does not exist" and stops, which is the honest answer.

Nothing else changes: same message, same `return`, same exit code.

## Units of work

### Unit A — the root check
- **Files:** `scripts/clean-test-temp.ps1`
- **Does:** line 29 per Design.
- **Depends on:** nothing.
- **Acceptance:** the four runs in Verification behave as stated.

## Verification

Every command here runs with no browser, no GUI and no human. Nothing in this task can only be seen by eye,
so it is not marked `attended`. `dotnet build` and `dotnet test` are unaffected by a change to a script that
nothing in the solution references, and are run to show exactly that.

```
dotnet build
dotnet test
```

Then the validator's own two shapes, plus the two that must not regress, against throwaway roots:

```powershell
# 1. A bracketed root that EXISTS: used to report "does not exist" and silently clean nothing.
$b = Join-Path $env:TEMP "t53-$(New-Guid)"
New-Item -ItemType Directory -Force (Join-Path $b 'ro[o]t\child') | Out-Null
./scripts/clean-test-temp.ps1 -Root (Join-Path $b 'ro[o]t') -All -OlderThanMinutes 0
#   expected: a real run — examined 1, deleted 1 — and the child is gone.

# 2. A bracketed root that does NOT exist beside a sibling that does: used to wildcard onto the sibling
#    and throw at EnumerateDirectories.
New-Item -ItemType Directory -Force (Join-Path $b 'sib\root') | Out-Null
./scripts/clean-test-temp.ps1 -Root (Join-Path $b 'sib\ro[o]t')
#   expected: "Nothing to clean: ...\sib\ro[o]t does not exist." exit 0, and sib\root untouched.

# 3. An ordinary missing root still says so.
./scripts/clean-test-temp.ps1 -Root (Join-Path $b 'nothing-here')

# 4. An ordinary root with one directory in it is still cleaned, summary and all.
New-Item -ItemType Directory -Force (Join-Path $b 'plain\hub') | Out-Null
./scripts/clean-test-temp.ps1 -Root (Join-Path $b 'plain') -All -OlderThanMinutes 0
```

Passing is: 1 deletes, 2 and 3 say "does not exist" and exit 0 leaving the sibling in place, 4 is unchanged
from today, and every run prints its counts.

## Proof

`dotnet build`: clean, 0 warnings. `dotnet test`: 3736 Core, 23 Launch, 77 Cli, 340 Server — all green,
which is the point: nothing in the solution references this script, and the change must not pretend to.

### The four shapes, run

```
1. bracketed root that EXISTS
   ...\ro[o]t: examined 1, deleted 1, kept 0, held 0, skipped 0, gone 0, failed 0.
   child still there? False
2. bracketed root that does NOT exist, beside a sibling that does
   Nothing to clean: ...\sib\ro[o]t does not exist.
   sibling still there? True
3. ordinary missing root
   Nothing to clean: ...\nothing-here does not exist.
4. ordinary root with one hub in it
   ...\plain: examined 1, deleted 1, ... failed 0.     hub still there? False
```

### Both defects reproduced on the line as it was

The fix is load-bearing, checked by putting `Test-Path $Root` back and running the same two shapes:

```
1 (pre-fix)  Nothing to clean: ...\ro[o]t does not exist.      child still there? True
             ^ the silent no-op: the root is right there, holding a child, and the script walks away.
2 (pre-fix)  ...\sib\ro[o]t: examined 0, deleted 0, ... failed 0.
             THREW: Could not find a part of the path '...'
             ^ the wildcard matched the sibling, so the guard passed and EnumerateDirectories
               threw on the literal path. The summary still printed first, as T-29 Amendment 4 requires.
```

Both match the validator's report exactly, including that neither loses data — which is why T-53 was filed
as non-blocking and why the reason to fix it is the half-applied rule rather than the damage.
