# Direct offline work — 2026-09-21

The founder authorized direct local coding instead of dogfooding. Mother must remain offline. T-67 and its related blocker family are excluded. The founder explicitly instructed: **Leave live-observation items pending.** Local fixtures do not replace those acceptance steps.

This branch is an integration checkpoint, not a release or a claim that the task board is complete. No live task status was rewritten, no validator identity was impersonated, and no live model sessions were launched. Do not mark tasks Done from the existence of this commit.

## Preserved work

Branch `direct/offline-completion-20260921` in `.worktrees/direct-offline` starts at main `547d987`. It incorporates the preserved T-123, T-121, T-94, T-103, T-104, T-105, T-107, T-108 and T-109 branches. Earlier direct work is checkpointed in `825cac4`.

Direct additions include objective tracking, versioned operating guidance, advisory routing, exact integration execution/promotion, and committed visual design approval. Guidance delivery is recorded for processes that actually started, including failed sessions. Design approval binds immutable artifact blobs and a frozen spec hash; validation requires independent visual and interaction evidence. Text-only tasks retain their existing validation path.

Integration executes approved build/test commands against one recorded merge commit in an owned detached checkout. Passing evidence precedes compare-and-swap promotion. A checked-out target is refused rather than silently changing someone's checkout. Candidate conflicts retain uncapped structured file evidence and bounded prose. Default-branch deletion invalidates the candidate. Promotion intent is durable for crash recovery.

## Verification and remaining work

Logs are retained in the root workspace's `artifacts/direct-*.log`, outside this worktree.

- Earlier full regression attempt: CLI 341, Core 3742 and Launch 238 tests passed. Server regression did not pass; the run was stopped after identifying landing-policy fixture failures. This is not a passing full-suite result.
- Focused integration/objective run: 47 passed (`direct-integration-tests.log`).
- Knowledge/objective run: 8 passed (`direct-knowledge-tests.log`).
- Design/knowledge/routing run: 3 passed (`direct-design-tests.log`).
- Real Git integration runner: 3 passed (`direct-runner-tests.log`), covering exact checkout, build failure/skipped tests, conflict evidence and checkout cleanup.
- Downstream fixtures are being migrated to explicit integration evidence. Simulated fixture receipts are labeled as such and are not product verification evidence. Later `direct-landing-tests*.log` files record the evolving results.

Outstanding before release or task completion:

1. Finish conductor and workflow benchmark integration-policy regressions. Preserve capacity, daily budget, restart, stale-subject and conflict attribution checks. Do not weaken the required integration gate to make old fixtures pass.
2. Rerun the complete suite once the targeted failures are resolved; run the required XML policy, Native AOT, scratch installed CLI and feature smoke checks. Never target the live home with development binaries.
3. Complete the acceptance audit and missing CLI/documentation/installed evidence for objectives, operating guidance, advisory routing, and visual design. Human preference approval and live improvement claims are not supplied by local fixtures.
4. T-110 release/performance work and T-111 remote validator work have not been implemented by this direct checkpoint. Their live deployment/two-machine observations must remain pending.
5. Only after verification, integrate the finished changes and update the task board with explicit direct-work evidence. Keep T-67-related and live-observation tasks pending.

T-112, T-114, T-115, T-116, T-118, T-120 and T-122 retain their live-observation acceptance. T-124's historical install/pilot prerequisite is not satisfied by this offline branch. T-67, T-74, T-79, T-81, T-85, T-86 and T-97 remain outside this completion claim. Cancelled tasks remain cancelled.
