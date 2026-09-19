# T-10 — Discord ingest: raw mentions in titles

> Frozen spec. An implementer completes this without making design decisions.
> If something here is wrong or missing, the implementer stops and reports; they do not improvise.

## Goal

A Discord message reading `<@1550341615472746526> Hi there!` ingests as `@MUTHUR Hi there!`. The mention ids
Discord sends on the wire are replaced with the usernames Discord sends alongside them, so an inbound item
reads the way the message looked to the person who typed it.

## Scope — this is half of T-10, on purpose

T-10 names two things. Only the first is built here.

1. **Raw mentions in titles.** A defect with one right answer: Discord sends mentions as ids and carries the
   usernames in the same payload. This task.
2. **No notion of being addressed.** The task's own words: *"A sketch, not a decision — the founder has not
   chosen"*, and it argues against its own sketch — *"an on-call that only ever hears what is aimed at it also
   never notices a problem being discussed in the room"*. That is a product decision, and it is with the
   founder as **`muthur ask` #17** with three options (opt-in filter / mark-but-ingest / not yet). It is not
   guessed at here, and this branch must not implement any of it.

The task also says the firehose is *"not urgent while the channel is the founder's own and quiet"*, so
shipping the title fix alone is the whole of what is urgent.

## Context

### Where it happens

`src/Muthur.Server/Services/IngestService.cs`, `DiscordChannelSource.Parse` — a pure `static` that takes the
raw JSON and returns a `SourceFetch`. It builds each item as:

```csharp
var content = (message.TryGetProperty("content", out var c) ? c.GetString() : null)?.Trim();
if (string.IsNullOrEmpty(content)) continue;
items.Add((snowflake, new IncomingItem(id, Title(content), content, …)));
```

`Title(content)` takes the first line and truncates at 120 characters. Both the title and the body are the raw
`content`, mention tokens and all.

Because `Parse` is static and pure, this whole task is unit-testable — `tests/Muthur.Server.Tests/DiscordChannelSourceTests.cs`
already calls it directly with hand-written payloads. **No browser, no scratch hub and no network are needed.**

### What Discord actually sends

A message with a user mention carries the token in `content` and the user in a sibling array:

```json
{
  "id": "100",
  "content": "<@1550341615472746526> Hi there!",
  "author": { "username": "hersh", "bot": false },
  "mentions": [ { "id": "1550341615472746526", "username": "MUTHUR" } ]
}
```

Two token spellings mean a user: `<@ID>` and `<@!ID>`, the second a legacy form Discord still emits for
mentions that displayed a nickname. Both must be handled.

**Two other token shapes exist and are deliberately left alone**, because the payload carries no name for
them: `<@&ROLE_ID>` (a role — `mention_roles` carries ids only) and `<#CHANNEL_ID>` (a channel). Substituting
only what the message actually names is the honest rule; inventing `@role-1234` would be worse than the id.
`@everyone` and `@here` are already literal text and need nothing.

## Non-goals

- Anything about filtering, marking, or "addressed to us" — see Scope, and `muthur ask` #17.
- Any change to `IncomingItem`, `IngestService`, the poll loop, cursors, or the bot/webhook skip rules.
- Role or channel token substitution.
- Any change to `GitHubIssuesSource`.
- Fetching usernames Discord did not send. No extra API call, ever — `Parse` is pure and stays pure.

## Design

### Substitute once, on the content

In `DiscordChannelSource`, add:

```csharp
/// <summary>
/// Discord sends a mention as "&lt;@id&gt;" and the user it means in the message's own "mentions" array, so the
/// wire form is unreadable and the fix needs no extra call. Ids the message does not name are left as they
/// are: a token we cannot resolve is less misleading than a name we invented.
/// </summary>
private static string Mentions(string content, JsonElement message)
```

- Return `content` unchanged when there is no `mentions` property, when it is not an array, or when it is
  empty. This is the common case and must not allocate a thing.
- Otherwise, for each element with a string `id` and a string `username`, replace both `<@{id}>` and
  `<@!{id}>` with `"@" + username`, using `string.Replace(..., StringComparison.Ordinal)`.
- An element missing either property is skipped, not an error.

Call it where `content` is first read, **before** the emptiness check, so the title and the body are both the
readable form and cannot disagree:

```csharp
var content = (message.TryGetProperty("content", out var c) ? c.GetString() : null)?.Trim();
content = content is null ? null : Mentions(content, message);
```

**The body is substituted too, not only the title.** T-10's headline is about titles, but the title is derived
from the first line of the body — leaving the body raw would make an item whose title and body disagree about
what the message said, and a reader of the body is no better served by `<@1550341615472746526>` than a reader
of the title.

### Order relative to the skip rules

Substitution happens after the bot and webhook skips (which do not look at content) and after the trim, and
before `Title(content)`. A message whose entire content is a mention becomes `@MUTHUR` — non-empty, so it
still ingests. Do not add a rule that drops it; that is filtering, which is question #17.

### Truncation is unaffected

`Title` truncates at 120 characters after substitution. Substitution only ever shortens (`<@1550341615472746526>`
is 22 characters, `@MUTHUR` is 7), so nothing that fits today stops fitting. Do not move or change `Title`.

## Units of work

### Unit A — resolve mentions from the payload
- **Files:** `src/Muthur.Server/Services/IngestService.cs`,
  `tests/Muthur.Server.Tests/DiscordChannelSourceTests.cs`.
- **Does:** everything under Design.
- **Depends on:** nothing.
- **Acceptance:** `dotnet build` clean (warnings are errors), `dotnet test` green, and tests covering:
  - `<@ID> Hi there!` with a matching `mentions` entry → title **and** body read `@MUTHUR Hi there!`;
  - the `<@!ID>` spelling → the same;
  - two different users mentioned in one message, both substituted;
  - a mention in the middle of a sentence, not only at the start;
  - an id in `content` that is **not** in `mentions` → left exactly as it was;
  - no `mentions` property at all → content unchanged (the existing tests already cover this shape; make sure
    they still pass rather than duplicating them);
  - `mentions` present but empty → content unchanged;
  - a role token `<@&456>` and a channel token `<#789>` → left exactly as they were;
  - a message whose whole content is one mention → still ingests, title `@MUTHUR`.

  Follow the file's existing helpers (`Payload`, `FromPerson`) and add one for a message with mentions rather
  than hand-writing JSON in each test.

## Verification

```
dotnet build
dotnet test
```

Both clean. `DiscordChannelSourceTests` is the verification: `Parse` is a pure static, so the unit tests
exercise the real parser against real payload shapes, and there is nothing a scratch hub would add.

For a validator who wants to see it end to end without a Discord account, the honest check is that the
existing shape still holds — a message someone typed becomes an inbound item, a bot message does not — plus
the new substitution cases above. **No browser is needed and none should be used**, and no network call is
made by any test.

## Out of scope / follow-ups

- **`muthur ask` #17** decides the second half of T-10. When it is answered, that half is its own task with
  its own spec; do not reopen this one.
- **Role and channel tokens stay raw.** Resolving them needs names Discord does not send with the message —
  a guild roles call and a channels call, both cacheable, both an extra API call from a parser that is
  currently pure. Worth its own task if the raw ids ever actually bother anyone.
