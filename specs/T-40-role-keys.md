# T-40 — A project's required validators are checked against the rule that makes them role keys

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

After this task, a project cannot be given a required validator that is not a legal role key, and the rule
deciding that lives in one place instead of being restated wherever someone needs it. `muthur project set
demo --validator "Not A Role!"` is refused at the moment it is asked for, rather than accepted and
discovered later by something downstream.

## The defect

`ProjectService.Normalize` trims, lowercases, drops empties and dedupes. It checks nothing:

```csharp
private static List<string> Normalize(IReadOnlyList<string>? validators) =>
    (validators ?? []).Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).Distinct().ToList();
```

So `Project.RequiredValidators` — and through `LifecycleService.ImplementedAsync`, every
`TaskValidation.ValidatorKey` — can hold a string of any length and any characters. A role key cannot:
`RoleService.KeyPattern` is `^[a-z0-9][a-z0-9-]{0,47}$`. Nothing reconciles the two.

The same file already does this correctly for the sibling field. `ValidSources` normalizes and then rejects
each entry that is not a source this hub can poll, with a `Fail.Rule` naming the offender. Validators got
the normalizing half and not the checking half.

**Not a live defect today.** It was found by the specialist working T-13, answering "is there a fourth door"
rather than by anything breaking: the only project on this hub requires `validator`, which is legal. What
makes it worth fixing is what stands between an unchecked key and the code that assumes a legal one — a
single line in `ConductorService` that skips a validation whose key names no `Role`. That line was written
for an unrelated reason ("a role that no longer exists") and is now load-bearing for something it never
mentions. One deletion away from a real defect, and nothing names the dependency.

## The decision, and why it is not the founder's

Two questions, both settled here:

- **Shape is checked; existence is not.** A malformed key can never name a role, so refusing it is certainly
  right. Whether a *well-formed* role exists yet is a timing question — a founder setting up a project
  before defining its roles is doing something reasonable, and `muthur doctor`'s project check already
  reports a required validator that is not a defined role as a `fail`. That is the T-5 pattern: say it out
  loud rather than refuse. Requiring roles to exist first would be a new ordering constraint, invented here,
  with no incident behind it.
- **The rule moves to one place rather than being copied.** Duplicating the literal regex into
  `ProjectService` would work and would drift. The whole point of this task is that a cross-module invariant
  was unnamed; restating it in a second file names it twice and reconciles it nowhere.

## Context

- `src/Muthur.Server/Services/ProjectService.cs` — `Normalize` (line ~101), its two call sites in
  `AddAsync` and `UpdateAsync`, and `ValidSources` (line ~89), which is the shape to copy.
- `src/Muthur.Server/Services/RoleService.cs` — `KeyPattern` at line 12 and its `invalid_key` message:
  `"Role keys are 1-48 chars of a-z, 0-9 or '-'."`
- `src/Muthur.Core/` holds pure rules with no EF and no HTTP — `LeasePolicy`, `TaskStateMachine`,
  `OutboundGate`, `SecretScanner`. A key-format rule belongs with them.
- `src/Muthur.Server/Services/DoctorProjectCheck.cs` already reports a required validator that is not a
  defined role, and one whose role exists but is not a validator. Neither changes.
- Both `ProjectService` and `RoleService` are already `sealed partial`, so a `[GeneratedRegex]` can live in
  either — but see the Design: it lives in neither.

Constraints that are not obvious:

- Services throw `MuthurException` via `Fail.*` with a stable `code`; rule violation → 422/exit 2.
- Every state change goes through `Ledger.MutateAsync`; `AddAsync` and `UpdateAsync` already are inside one,
  and the validation must throw **before** anything is written.
- Warnings are errors.

## Non-goals

- Requiring that a validator role exists before a project may name it. Settled above; `doctor` reports it.
- Any change to `RoleService`'s behaviour, messages or error codes. Only where its rule *lives* changes.
- Any change to `DoctorProjectCheck`.
- Migrating or rejecting projects that already hold an illegal key. None exists — the only project on this
  hub requires `validator` — and a migration for a hypothetical row is more risk than the row.
- ~~The relationship between role-key length and the conductor's agent-name budget.~~ **Now in scope** — see
  *The length relationship* below. The founder settled it as request #12 after it turned out to be live on
  `main` rather than a follow-up.

## The length relationship, which is live on `main`

This section replaces the original's deferral, and the number it deferred on was wrong in both directions
before two people measured it.

**Measured on a scratch hub, not reasoned:**

```
role key 38 chars -> agent name "conductor-<role>" is 48 -> accepted
role key 39 chars -> 49 -> invalid_name
role key 48 chars -> 58 -> invalid_name
and a project may require a 48-character validator key today, accepted in silence
```

So on `main` a legal role key of 39-48 characters produces a conductor identity the hub refuses, and the
session fails at registration. Live, not latent, and needing neither a deletion nor T-13.

**The 80 in the original task body was not imaginary — it is T-13's.** `corner` raised
`AgentService.NamePattern` from 1-48 to 1-80 *in* T-13, deliberately, as the thing that made removing
truncation safe. Both readings were right about different commits:

| | agent-name max | identity form | longest safe role key |
|---|---|---|---|
| `main` today | 48 | `conductor-{role}` | **38** |
| with T-13 landed | 80 | `conductor-{role}-{task}` | **62** |

Post-T-13 the existing 48-character cap already holds with 14 characters of headroom, so the invariant is
satisfied and needs no narrowing. Pre-T-13 there are ten characters of overflow.

**Founder request #12, answered: option 1 with option 4's test folded in** — cap role keys at a length
derived from the identity form, and assert the relationship so it cannot drift silently. On the collision:

> T-13 is the reason the margin shrinks, so whichever of you lands second inherits the derivation. Say in
> T-40's spec which identity form the cap is sized for, and if T-13 lands first, size it for the longer one.

**This spec is sized for `main` as it stands: `conductor-{role}` against a 48-character agent name, so
`RoleKey.MaxLength` is 38.** T-40 may land before T-13 or after it, and 38 is correct in both worlds — it is
required in the first and merely conservative in the second. Choosing the post-T-13 number instead would
leave `main` broken for as long as T-13 remains in validation, which is a real interval and not a
hypothetical one. The longest role key in this organization is 16 characters, so nothing existing is
affected either way.

If T-13 lands first, whoever lands second re-derives: 62, with the reasoning above.

### The assertion

A test that fails if either constant moves without the other:

> the longest legal role key, put through the identity form the tree actually uses, still yields a name that
> `AgentService`'s pattern accepts.

It must read both patterns rather than restating their numbers — a test that hard-codes 38 and 48 passes
happily on the day someone changes one of them, which is the failure it exists to prevent.

## Design

### The rule moves to Core

New `src/Muthur.Core/RoleKey.cs`:

```csharp
namespace Muthur.Core;

/// <summary>
/// What may name a role. Projects name required validators with these, and the conductor builds agent
/// identities out of them, so the rule has to be one rule rather than a literal repeated per caller.
/// </summary>
public static partial class RoleKey
{
    /// <summary>The longest a role key may be. Callers that build something out of a key budget against this.</summary>
    public const int MaxLength = 48;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? key) => key is not null && Pattern().IsMatch(key);

    /// <summary>The rule in the words the founder reads when they break it.</summary>
    public const string Rule = "1-48 chars of a-z, 0-9 or '-'";
}
```

`RoleService.DefineAsync` uses `RoleKey.IsValid(key)` and keeps its existing `invalid_key` code and message
**verbatim** — no behaviour change there, and its tests must pass untouched. Its own `KeyPattern` member
goes.

### The check

`ProjectService` gains a validator counterpart to `ValidSources`, and both call sites use it:

```csharp
private static List<string> ValidValidators(IReadOnlyList<string>? requested)
{
    var normalized = Normalize(requested);
    foreach (var key in normalized)
        if (!RoleKey.IsValid(key))
            throw Fail.Rule("invalid_validator",
                $"'{key}' cannot name a role, so no validator could ever hold it. Role keys are {RoleKey.Rule}.");
    return normalized;
}
```

`AddAsync` line ~37 and `UpdateAsync` line ~57 call it instead of `Normalize`. `Normalize` stays, because
`ValidSources` and `ValidValidators` both build on it.

The message says *why* rather than restating the regex twice: an operator who typed `Win-Validator` needs to
know that nothing will ever hold it, not only that a pattern failed.

## Units of work

### Unit A — the whole task
- **Files:** new `src/Muthur.Core/RoleKey.cs`; modified `src/Muthur.Server/Services/RoleService.cs`,
  `src/Muthur.Server/Services/ProjectService.cs`; `tests/Muthur.Core.Tests/`,
  `tests/Muthur.Server.Tests/`.
- **Does:** everything above.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` and `dotnet test` clean, plus:
  1. **The defect.** `project add` and `project set` both refuse a required validator that is not a legal
     role key — too long, a capital before normalizing is *accepted* (it is lowercased first, as
     `RoleService` does), a `.`, a `_`, a leading `-`, an embedded space — each with 422 `invalid_validator`
     naming the offending key. Mutation-check one of them: drop the `RoleKey.IsValid` call and confirm it
     fails.
  2. **Nothing is written on refusal.** After a refused `project set`, `project list` shows the project's
     previous validators unchanged — the throw happens inside the mutation, so this is worth pinning rather
     than assuming.
  3. **A legal key still works**, including one exactly `RoleKey.MaxLength` long, and a role that does not
     exist yet is still accepted — with `doctor` still reporting it as a `fail`, which is the behaviour this
     task deliberately keeps.
  4. **`RoleService` is unchanged in behaviour.** Its existing `invalid_key` tests pass untouched, and
     `role define win.validator` still gives `invalid_key` with its original message.
  5. `RoleKey.IsValid` covers the boundaries directly in `tests/Muthur.Core.Tests` — empty, 48, 49, leading
     digit, leading hyphen, and the character set.

## Verification

```
dotnet build
dotnet test
```

Both clean.

End to end, against an installed build and a scratch home, never the live hub:

```
pwsh ./scripts/install.ps1 -Destination ./artifacts/t40
$env:MUTHUR_HOME = "$PWD/artifacts/t40-home"; $env:MUTHUR_URL = "http://127.0.0.1:7452"
./artifacts/t40/muthur.exe up
```

- `muthur project add demo --repo <a real checkout> --validator "not a role!" --founder` → exit 2,
  `invalid_validator`, message naming the key. The project is **not** created.
- Create it with a legal `--validator win-validator`, then
  `muthur project set demo --validator "also bad!" --founder` → exit 2, and `muthur project list` still
  shows `win-validator`.
- `muthur role define win-validator --validator true --founder` was never required for any of the above:
  naming a role that does not exist yet is still allowed, and `muthur doctor` reports it as a `fail` with
  the message it always did.

## Out of scope / follow-ups

- The length relationship is no longer a follow-up; it is a section of this spec.
- `OutboundService.KeyPattern` and `ProjectService`'s own project-key rule are separate limits that happen
  to look similar. They are not this rule and should not be folded into it; `ProjectService` already
  diverges at 32 characters, which is the evidence they are independent by design.
