# Documentation Index
**Review 2026-09-25 plan (T1):** [`refactoring/REVIEW-2026-09-25-PLAN.md`](refactoring/REVIEW-2026-09-25-PLAN.md) — verified findings, waves 1–5, Q-R decisions.

Quick reference for finding documentation by purpose. Read-tier guides which files to load.

| File | Purpose | Tier |
|------|---------|------|
| **AGENTS.md** | Rules for work mode, process, agent behavior | T0, always |
| **task_on_progress.md** | Current status, blockers, critical rules | T0, always |
| **docs/ACTIVE-TASKS.md** | Consolidated open tasks across all groups | T0, always |
| **docs/architecture.md** | System design, code map, component ownership | T1, any area |
| **docs/APP-MECHANISMS-VI.md** | Core flow descriptions (Vietnamese) | T1, app flow |
| **docs/adr/*.md** | Architecture decisions 0001-0008 | T1, relevant area |
| **docs/refactoring/I18N-PLAN.md** | Multi-language design, L00–L12 tasks, Q-L decisions | T1, UI text |
| **docs/TRANSLATING.md** | Adding/fixing a language (JSON catalogs) | T1, translations |
| **docs/refactoring/ARCH-REVIEW-SUMMARY.md** | AR review 2026-09-23: findings F1-F9, task order | T1, AR tasks |
| **docs/refactoring/arch-review/AR0x-*.md** | Step-by-step plan per AR task | T1, the AR task at hand |
| **docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md** | ST task status & invariants | T1, ST scope |
| **docs/refactoring/REFACTOR-STATUS.md** | OC/ST consolidated status | T1, refactoring |
| **docs/refactoring/PERF-STATUS.md** | Perf baselines (AR02e, perf night) | T1, perf diagnosis |
| **docs/refactoring/OPEN-DECISIONS.md** | Q-* decision registry (mostly resolved) | T1, decision lookup |
| **docs/refactoring/WORK-2026-09-25-ROUND7-FEATURES.md** | 9 agent branches (features + review round 7 fixes), integration + Settings redesign plan | T1, current batch |
| **docs/refactoring/OPTIMIZE-CLEAN-SUMMARY.md** | OC task status & blockers | T1, OC tasks |
| **docs/refactoring/TEST-CLEANUP-SUMMARY.md** | TC status (DONE); pointer to archive | T1, test cleanup |
| **docs/refactoring/TEST-SPEED-SUMMARY.md** | TS completed work, remaining tasks | T1, test speed |
| **docs/refactoring/T89-FIT-SUMMARY.md** | Fit layout status; pointer to archive | T1, Fit/GUI |
| **README.md** | Project overview, setup, build commands | T1, first-time setup |
| **docs/archive/** (`historical/`, `future/`, `evidence/`, `progress-log-2026-09.md`) | Full plans, ADR evidence, completed-task detail | T2, **do not read** unless referenced |

**Tiers:** T0 ≤ 16 KB total (always read) · T1 ≤ 24 KB/file (per work; `README.md`, `docs/*.md`, `docs/adr/`, `docs/refactoring/*.md`, `arch-review/`) · T2 unlimited (archive only). Checked by `tools/docs-budget.ps1 -Check`.
