# T-95 — Remove measured throughput friction

User authorization: implement the September 20 throughput audit's concrete improvements and make the pacing improvement measurable without reducing quality.

## Goal

Workers receive complete verified assignments without handwritten base metadata. An answered decision resumes an exited owner promptly. Dependency waiting is measured and classified honestly. Resumed sessions see current decisions. Execution reports identify failed outcomes and cannot recycle a prior session's final message.

## Scope

1. Generate an assignment from the actual caller repository, named local base, full base SHA, named default branch, committed spec blob and actual worker worktree/branch. Resolve HEAD to the current local branch; refuse incomplete or invalid dispatch before inference. Obtain the default branch from the task/project registration or an explicit option. Preserve the existing caller-worktree base behavior and verification commands. Confirm the created worktree matches the dispatched commit and the base has not moved.
2. Give launched repository sessions narrowly scoped process-local Git trust for the verified worktree. Preserve worker credential scrubbing and the existing sandbox, command and outbound boundaries. No wildcard/global trust or permission expansion. Record dispatch identity and structured worker outcome in receipts with backward-compatible optional fields.
3. Preserve original-token-bound child exit proof for blocked tasks. Once all questions are answered, the conductor recovers an exited owner's work on its next pass. Never steal a live owner's task, revive unanswered work, or release a replacement session based on stale evidence.
4. Record dependency-driven return to backlog explicitly and replay existing dependency events correctly for historical receipts. Nonempty dependencies only change an in-progress task; clearing dependencies or updating a blocked task does not imply backlog. A legitimate dependency wait is not an unproductive orchestration failure.
5. Add a bounded latest-decision packet to resumed orchestration: request identifiers, timestamps, questions and answers, with explicit instructions to reconcile task dependencies and scope against the latest decision. Do not grant authority to override founder decisions, permission gates, spending or outbound approval.
6. Use unique attempt output locations and preserve process failure reasons even when a final report exists. Include run identity, status/failure classification and dispatched/finished commit provenance where known; unknown stays unknown. Supply repeatable phase timing and artifact capture for this rollout and a read-only throughput measurement script.

## Quality and scope constraints

Independent validation and existing full-suite/installed-product requirements remain. Keep two conductor slots. Do not broaden sensitive-file permissions, change account credentials, or waive T-67/T-88 requirements. No unrequested corporate priorities are invented. A larger executor/verification service, capacity expansion and objective data model require evidence from this corrected baseline and are not prerequisites for landing these bounded fixes.

## Verification

Regression tests cover prompt fields, actual caller/base selection, invalid or moved base, spec provenance, process-local trust and worker identity scrubbing, per-attempt output isolation, process-error reporting, blocked-child exit/answer/resumption, multiple questions, live/re-registered owners, dependency replay and no-verdict classification, old/new receipt shapes, and bounded latest-decision context.

Run build and full test suite with task-specific MUTHUR_HOME/MUTHUR_URL. Install the pinned branch into a scratch destination; exercise worker dispatch with a deterministic stand-in harness (no paid inference), receipt metadata, historical dependency timing and answered-owner resumption on an isolated hub. Verify cleanup and live hub identity. Record before/after measures without claiming a steady-state throughput increase from a short sample.

Land through the existing independent validation gate, then install from the approved pinned revision during a safe restart boundary. Preserve current sessions where possible by draining staffing before the brief restart. Reconcile approved T-87 documentation sequencing only after creating a durable dependency-bound behavioral follow-up and preserving its evidence requirements.
