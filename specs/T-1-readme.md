# T-1 — The README describes what the outbound gate does with a credential

> The README says a credential in an outbound draft is "refused, not stored". It is not. T-9 and T-14
> replaced that behaviour with flag-and-escalate, and the README was never updated to match.

## What the code actually does

`OutboundService.DraftAsync` (src/Muthur.Server/Services/OutboundService.cs:75) refuses exactly one thing at draft
time: an unknown target (`target_not_allowed`, exit 2). A credential never refuses a draft. The body is stored, hashed,
and `OutboundGate.Flags(body)` is recorded on the message.

`OutboundGate.NeedsFounder` (src/Muthur.Core/OutboundGate.cs:28) is:

```csharp
message.Target is { RequiresFounderApproval: true } || message.Flags.Count > 0
```

So any flag escalates the message to founder approval, whatever the target's own setting. After peer review it
goes to `awaiting_founder` rather than `approved` (OutboundService.cs:122), and `EnsureSendable` refuses the send
until `FounderApprovedAt` is set (OutboundGate.cs:52). T-14 then puts those messages at the top of `/operations`
with the masked flag that caused it.

The founder seeing the text and deciding is the control. Refusing the draft was the weaker design: it taught agents
to reword until the scanner went quiet, and it threw away the evidence.

## Design

One line of README.md (line 128), inside the outbound code block, becomes:

```
muthur out draft --target news --file announce.md      # unknown target → refused. A credential → drafted, flagged, only you can send it
```

Nothing else changes. The surrounding sentence about `--founder-approval` targets is already correct, and stays.

## Verification

```
dotnet build && dotnet test            # unchanged: 3794 pass, 0 warnings
grep -n "refused, not stored" README.md     # no match
```

Against a scratch hub (own MUTHUR_HOME/MUTHUR_URL, installed CLI under artifacts/), with a `file` target defined
WITHOUT `--founder-approval`:

- `out draft --target notes --text 'docker login registry.example.com -u deploy -p Sup3rS3cretValue9'`
  → exit 0, a stored message, `"flags":["possible-credential-near-password-word: ..."]`, `"requiresFounderApproval":true`.
- `out draft --target nowhere --text 'hello'` → exit 2, `target_not_allowed`.
- The flagged message cannot be sent on peer review alone: `out send` refuses while it is awaiting the founder.
