# Documentation Index
**Review 2026-09-25 plan (T1):** [`refactoring/REVIEW-2026-09-25-PLAN.md`](refactoring/REVIEW-2026-09-25-PLAN.md) — verified findings, waves 1–5, Q-R decisions.

Quick reference for finding documentation by purpose. Read-tier guides which files to load.

| File | Purpose | Tier | When Read |
|------|---------|------|-----------|
| **AGENTS.md** | Rules for work mode, process, agent behavior | T0 | Startup, always |
| **task_on_progress.md** | Current status, blockers, critical rules | T0 | Startup, always |
| **docs/ACTIVE-TASKS.md** | Consolidated open tasks across all groups | T0 | Startup |
| **docs/architecture.md** | System design, code map, component ownership | T1 | Any area |
| **docs/APP-MECHANISMS-VI.md** | Core flow descriptions (Vietnamese) | T1 | App startup/flow |
| **docs/adr/*.md** | Architecture decisions 0001-0007 | T1 | When working in relevant area |
| **docs/refactoring/I18N-PLAN.md** | Multi-language (EN+VI, community JSON catalogs): design, L00–L12 tasks, Q-L decisions | T1 | UI-text changes |
| **docs/TRANSLATING.md** | Adding or fixing a language (JSON catalogs) | T1 | Before editing translations |
| **docs/refactoring/ARCH-REVIEW-SUMMARY.md** | Architecture review 2026-09-23: findings F1-F9, AR task table, order | T1 | AR tasks, boundaries |
| **docs/refactoring/arch-review/AR0x-*.md** | Step-by-step plan per AR task (files, lines, tests, acceptance) | T1 | The AR task at hand |
| **docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md** | ST task status & INV (invariants) | T1 | When touching ST scope |
| **docs/refactoring/REFACTOR-STATUS.md** | OC/ST consolidated status (quick lookup) | T1 | When working on refactoring |
| **docs/refactoring/PERF-STATUS.md** | Perf baselines (AR02e, perf night) | T1 | When checking perf diagnosis |
| **docs/refactoring/OPEN-DECISIONS.md** | All Q-* decisions consolidated (incl. Q-AR1..5) | T1 | For decision lookup |
| **docs/refactoring/TC00-BASELINE-REPORT.md** | TC baseline test metrics | T1 | For test cleanup context |
| **docs/refactoring/OPTIMIZE-CLEAN-SUMMARY.md** | OC task status & blockers | T1 | When working on OC tasks |
| **docs/refactoring/TEST-CLEANUP-SUMMARY.md** | TC objectives, issues, tasks | T1 | When working on test cleanup |
| **docs/refactoring/TEST-SPEED-SUMMARY.md** | TS completed work, remaining tasks | T1 | When working on test speed |
| **docs/refactoring/T89-FIT-SUMMARY.md** | Fit layout issue, causes, acceptance | T1 | When working on Fit or GUI |
| **README.md** | Project overview, setup, build commands | T1 | First time setup |
| **docs/archive/** (incl. `historical/`, `future/`, `evidence/`, `progress-log-2026-09.md`) | Full plans, ADR evidence, completed-task detail, commit logs | T2 | **Do not read** unless referenced or verifying decisions |

**Tiers:** T0 ≤ 12 KB total (always read) · T1 ≤ 15 KB/file (per work) · T2 unlimited (archive only, not required for current work).
