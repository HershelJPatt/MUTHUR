# Decisions — 2026-09-22: the deferred task families

The ledger that held T-15 through T-124 lived on a second checkout's hub and no longer exists. This machine's live hub
holds T-1 to T-14 (the rebuilt ledger of 2026-09-17) and, from today, T-15. The tasks below therefore survive only as
specs and docs, and the decisions here are recorded in this file rather than as ledger state. Dogfooding is paused
since 2026-09-22; MUTHUR is worked on directly.

| Family | Decision | Why |
|---|---|---|
| **T-67** (Claude worker rails + real probe) | **Done.** The rails were already in main; the missing probe ran today. Evidence: `docs/T-67-probe-2026-09-22.md`. | Claude tokens are available again; the probe was the only outstanding item. |
| **T-97** (one real Claude Agent-tool unit, measured) | **Done today**, immediately after T-67. Evidence in the same file. | Its only prerequisite was T-67. |
| T-74, T-79, T-81, T-85, T-86 (T-67 follow-ups) | **Closed.** Re-read against the probe result; none needs new work. The permission questions they asked are answered by the measurement. | They were diagnosis tasks for a probe that could not start. |
| **T-110** (proposal 11, release/performance) | **Closed into T-15** on the live hub: conductor wakes on task transitions instead of waiting a full pass interval. | The measurable part is done by `muthur sim run`; the interval is the one finding worth code. |
| **T-111** (proposal 12, remote validator, two machines) | **Folded into M9.** | It is M9's "done when" line verbatim. |
| **M9** (deployable) | **Deferred until the first external project is chosen** (PLAN §11, decision 1). | The trust boundary decides the hosting shape; building it first means guessing. |
| Live-observation acceptances (T-112, T-114, T-115, T-116, T-118, T-120, T-122, T-124) | **Closed as not observable.** One umbrella observation task will be created when a real project runs on the hub. | Dogfooding is off and the ledger they measured against is gone. |

Everything else that `docs/OFFLINE-COMPLETION-2026-09-21.md` listed as outstanding (full suite, XML policy, installed
smoke, integration gate) was completed on 2026-09-22; see the four commits ending at 51a5b8d.
