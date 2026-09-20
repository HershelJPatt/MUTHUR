# T-51 — explicit receipts window notices

Frozen amendment, 2026-09-20. This replaces the previous spec text at 80b2709.
Founder request #27 selected numeric vocabulary; request #35 explicitly resolves the
old exemptions: supplied zero, negative, empty, whitespace and unparsable values
must show a notice. Absent hours stays quiet. Preserve existing defaults and clamps.
The founder rejected comparing three label mappings with the complete notice as a
reason to invoke the vocabulary escape hatch. No further product decision is open.

## Scope and design (Unit A: the whole implementation)

Modify only Receipts.razor, ReceiptsPanel.razor, wwwroot/app.css and
DashboardReceiptsTests.cs, in their existing Server / Server.Tests directories.
Do not change API, CLI, contracts, ReceiptsService, numeric parsing, or window bounds.

Keep the page's existing long.TryParse invariant integer parsing and saturation to int.
A missing query parameter is null and quiet. Every supplied string for which parsing
fails or the parsed number is <= 0 is invalid. Empty and whitespace are supplied,
not absent. Pass the invalid original string as a nullable panel parameter, with null
meaning no notice. Use a null-pattern check, not a nonempty check, in the panel so
empty strings render a notice. If framework binding loses the absent/empty distinction,
use NavigationManager's current query to detect presence; pin that distinction with
real page GET tests. Do not introduce a general duration parser.

The existing report and Effective property determine the window shown. Preserve:
- absent: 24 hours, no notice;
- empty, whitespace, 7d, 24h, 30d, abc, 1e9, 24,168, null (literal), long overflow:
  24 hours with notice;
- 0, -9 and long.MinValue: 1 hour with notice;
- positive integers including 24, 168, 720, 100000, 9999999999999 and long.MaxValue:
  existing clamping to 1..720, no notice.

Place a div.notice immediately after and outside the btn-row, before stats.
For invalid values render this sentence (Razor encoded):
"hours=<typed> is not a positive integer number of hours, so this is the last <Effective> hours.
The windows are hours=24 (24h), hours=168 (7d), hours=720 (30d)."
Use singular "hour" for 1. Derive the actual numeric window from Effective, not the
requested value or a hard-coded 24. Build the vocabulary from Windows using invariant
formatting, declared after Windows if a static field. Echo at most 40 characters plus
an ellipsis when truncated; do not render raw markup. Blank may render as hours=.
Keep all three numeric anchors and effective active-control behavior unchanged.
Use one .notice CSS rule in the receipts section using existing mono/amber/amber-bg
tokens, 1px amber border, 6px radius, 8px 10px padding and 6px 4px 0 margin. No inline CSS.
Do not add long commentary or unrelated refactors.

## Tests

Extend DashboardReceiptsTests using real GETs and TypedAsync. Preserve existing tests
except extend the rendered-class union to include an invalid page and require notice.
Add assertions for:
1. Each invalid case above returns 200, notice present, effective hours correct.
2. Absent versus explicit empty and whitespace, and all listed valid positive cases.
3. 7d echoes its value and teaches all three mappings. Controls remain exact numeric
   anchors, 24 active, no button, notice outside btn-row. For 0/-9 no control active.
4. 200 x characters clips to 40 plus ellipsis; HTML payload is encoded (no injected tag).
5. Following each printed window URL yields no notice and the right active control.
6. CSS class coverage includes notice.

## Verification

From the implementation branch, worker and orchestrator each run:
    dotnet build
    dotnet test -v n
Run through deterministic process waits/timeouts; logs may be redirected. Summarize
large logs with muthur utility summarize before inspecting full logs (orchestrator).
Worker cannot use muthur or start the hub. No push, default-branch merge or hub access.

Orchestrator-only installed smoke check:
Set MUTHUR_HOME to a fresh task-specific artifacts scratch directory and MUTHUR_URL
to http://127.0.0.1:7451 BEFORE installation or execution; preserve/restore prior env.
    pwsh ./scripts/install.ps1 -Destination ./artifacts/t51
    ./artifacts/t51/muthur.exe up
Fetch /receipts for absent, empty, whitespace, 7d, 24h, 30d, abc, 0, -9, 24, 168,
720, 100000 and 9999999999999. Assert 200 and the notice/actual window rules above,
numeric anchors and active controls. Assert long and HTML payload behavior too.
Fetch each printed link and assert valid pages have no notice.
API /api/v1/receipts?hours=7d remains 400. Installed CLI receipts --hours 7d must
fail parsing. Check scratch logs for unexpected errors. No browser or GUI required.
Always call installed CLI down in finally and restore env; leave no scratch processes.
Record build/test/smoke evidence with branch and head before implemented submission.
