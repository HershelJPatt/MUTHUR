# T-109 pilot design — revision 1, awaiting founder approval

This is a proposed product/design contract, not an approved or frozen implementation spec.
Base: main df3f929. Source inspected: TaskDetail.razor and RequestsPanel.razor.
T-99 is done (landed cb52ed1); T-94 is backlog and is optional, not a prerequisite
for preparing this design. No later T-109 decisions were present at claim time.

## Decision requested

Approve the following exact pilot preference choices for the actual MUTHUR task-detail
page. This approval would cover the layout, copy, states, and interaction below;
it would not approve an implementation, waive independent validation, or authorize
outbound sharing. Engineering contracts will be frozen separately before delegation.

## Before preview (current main, schematic)

    [ Task ID | title | lifecycle | validation badges ]
    [ Project / validation provenance / round / hashes / owner / spec / branch ]
    [ Description                                                ]
    [ History                                                    ]

No visual design panel or revision selector exists in the inspected source.
This schematic is source-derived, not a screenshot or a claim of browser evidence.

## After preview (proposed, schematic)

    [ Task ID | title | lifecycle | validation badges ]
    [ Project / validation provenance / round / hashes / owner / spec / branch ]
    [ Visual design                        Approved / Needs approval / Stale ]
    [ Revision: [r2 v]    design digest    bound spec digest                  ]
    [ Decision: Approved by <reviewer>, <time>, scope: <verbatim scope>       ]
    [ Before preview ]    [ After preview ]                                  
    [ State coverage: loading / empty / error / success / narrow              ]
    [ Design constraints | Accessibility | Written acceptance criteria       ]
    [ Artifact availability: verified / missing / changed / unchecked         ]
    [ Earlier decisions: revision, reviewer, time, decision, scope             ]
    [ Description                                                            ]
    [ History                                                                ]

The panel sits between metadata and Description. It reuses existing panel, form,
text, and status styling; no new brand palette, navigation page, or design tool.
Artifacts are local, versioned repository files with content digests. Preview text
always names the revision; historical content never presents itself as current.
Do not execute supplied HTML/scripts or silently fetch external URLs to render previews.
Approval occurs through the existing attributable founder decision route; the panel
is for inspecting revisions and decisions, not a second bypass approval mechanism.

## Interaction and state contract

- Initial load: show 'Loading visual design…'; announce completion without moving focus.
- No design: keep text-only tasks working and show 'No visual design attached' in a
  compact panel. Do not require approval or artifact upload for such tasks.
- Pending: show 'Needs approval', all artifacts/criteria, and no invented reviewer.
- Approved: show 'Approved', exact revision and spec digests, reviewer, timestamp,
  decision and approved scope. Approval is separate from implementation validation.
- Changed revision or bound spec: show 'Stale — approval applies to an earlier revision'.
  Preserve the old decision and let users inspect its exact historical revision.
- Missing/changed artifact: display the individual path and 'Missing' or 'Changed';
  never silently display substitute bytes as approved. Unchecked is not verified.
- Load error: show readable error and a 'Retry' button; retry preserves revision
  selection where that revision still exists. A failed refresh cannot imply current approval.
- Revision selector: labeled native select, newest first. Changing selection updates
  the previews, criteria and decision details together. Label historical selection
  'Historical revision'; keep current-approval status visibly distinct.
- Success: selecting an available revision displays that revision and announces it.
- Narrow (360 CSS px): before/after previews stack, metadata wraps, controls remain
  keyboard reachable, no page-wide horizontal scrolling; long hashes/paths wrap.
- Accessibility: semantic heading and labels, meaningful before/after alternative
  text, visible focus, keyboard selection and retry, status expressed in text as
  well as color, preserve existing theme contrast and respect reduced motion.

## Objective acceptance to freeze after preference approval

1. Installed CLI and dashboard identify the same immutable design/spec revision.
2. Changing a design or bound spec makes prior approval stale while retaining history.
3. An independent validator detects an intentional wrong revision/layout artifact and
   separately exercises revision selection or retry through a genuine interaction.
4. Missing or changed bytes report honestly; text-only lifecycle remains compatible.
5. Capture actual before/after screenshots and loading, empty, error, approved, stale,
   and narrow evidence in isolated scratch environments using pinned installed builds.
   The schematics here do not satisfy that future execution evidence requirement.
6. Full build/test and installed-product checks, independent review and existing
   T-67/T-88/T-97 permissions, capacity, budgets and outbound gates remain required.
7. Record approval waiting separately from execution, revisions, one-task cohort,
   observation window and missing data; do not claim a measured speedup.

## Recovery and scope

The pilot is this task's own task-detail change. Keep all prior text-only workflows.
Rollback uses the ordinary reviewed repository revert path, preserving ledger evidence.
No external sharing, proprietary service, account, spending or permission expansion.
No implementation has been delegated and this document is not an implementation pass.
