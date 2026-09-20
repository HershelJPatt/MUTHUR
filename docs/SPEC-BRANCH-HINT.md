# Preserve the optional spec branch hint

Founder request 38, answered on 2026-09-20 in ledger event 4312, chose option A:
preserve [T-50's branch-hint contract](../specs/T-50-spec-on-branch.md).
[T-75](../specs/T-75.md) records this decision and adds regression coverage;
the existing implementation already satisfies it.

The branch is optional. Spec attachment resolves content from the first source
that has the file, in this order:

1. The supplied branch hint, when present.
2. The stored task branch, when set and distinct from the supplied hint.
3. The project repository's working tree.

A missing file or nonexistent hinted branch falls through to the next source.
Once a source provides content, that content is authoritative for validation:
invalid content fails rather than searching for a valid replacement. In
particular, a wrong task heading on either branch returns `spec_id_mismatch`
even when the working tree contains a valid copy.

When the CLI's branch option is omitted, it uses the inferred checkout ref where
available; detached HEAD or unavailable provenance yields no hint. An omitted
API `Branch` is null. Specs on the default branch remain supported through the
working-tree fallback or an explicit default-branch hint. There is no requirement
for an authoritative branch or committed provenance at attachment time: even an
uncommitted spec in the project repository's working tree can attach.

Orchestrators must still commit frozen specs before delegation. T-68's incidents
with implementers starting from the wrong base require verifying the assigned
base and frozen spec before implementation; they do not justify breaking the
attachment contract. Attachment and implementer base verification serve different
purposes.

Existing `spec_missing` diagnostics and all existing guards remain unchanged,
including repository containment, lifecycle, heading, size, and attended-needs
behavior. Strict provenance is outside this decision. Any future proposal for it
requires a concrete demonstrated problem and a migration proposal.
