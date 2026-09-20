# T-93 — Route ordinary decisions through overseer triage

Frozen contract: new CLI/API questions default to `triage`. Explicit `technical` and
`human` remain supported. All existing human or missing-category records stay human;
the previous default did not record enough evidence to distinguish intent. No migration
or automatic downgrade of those records is permitted.

The current enabled overseer may classify an open triage request as technical or human
using a new triage command/API, with a required reason and evidence. It may also move a
mislabeled technical request to human. Human classification is terminal for the overseer;
it cannot reverse human/legacy routes. Classification never answers or unblocks a task.
Only the existing guarded decide operation answers a technical request. Changes are
transactional, attributed to the actual agent, and visible in request DTO/UI route and
reason fields. Existing founder answer authority is unchanged.

Standing delegation: retaining established compatibility, enforcing shared rules,
resolving duplicate implementation scope, and making internal identifiers consistent
are technical judgment. The words policy/contract/compatibility alone are not reasons
to defer. New product commitments/preferences, spending/concurrency changes, account
access, permission expansion, outbound approval and secrets remain human. Mixed or
uncertain human questions stay open; recording technical advice never answers their
human portion. Classification is evidence-based agent judgment, not keyword detection.

Update default CLI/API route, launched orchestrator guidance, overseer brief/prompt and
documentation. The overseer must consult current route metadata rather than treating
older memory labels as authority. Supply route reasons in bounded context packets.
Classification events must not themselves create another paid wake/poll cycle. The
existing checkpoint, condition, cooldown, account and two-slot rules remain unchanged.

Verify the three incidents (37/38/39 equivalents) via ask → triage → decide → unblock;
human categories, explicit/legacy human preservation, unauthorized/stale callers,
closed requests, reason/evidence enforcement, request route display, Native AOT CLI
round-trip and existing request/overseer regressions. Independently request
`/needs-you` from the installed scratch hub after creating one request of each kind;
assert `Overseer triage`, `Overseer decision`, `Founder only` and their route reasons
in the returned server-rendered HTML. These fields are plain server-rendered card
content; T-93 does not change the existing live request-event subscription or add
browser-only behavior. A browser connector is therefore not a prerequisite for this
task's required verification. The owner's Playwright check additionally exercised
live arrival/removal without reload and remains supporting evidence, not a substitute
for independent installed CLI/API/HTML checks. Independent validation is mandatory;
the owner does not vote.

Verification amendment after the first blocked verdict: the original wording implied
a browser was a required gate, although this change is independently provable through
the installed API and prerendered HTML. The validator confirmed all 4726 tests and
installed routing/authority checks but had no browser connector. This amendment makes
the required rendering check explicit without dropping any route/reason assertion.
Do not treat an unavailable browser connector as a missing prerequisite here.
