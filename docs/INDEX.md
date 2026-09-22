# Documentation Index

Quick reference for finding documentation by purpose. Read-tier guides which files to load.

| File | Purpose | Tier | Size | When Read |
|------|---------|------|------|-----------|
| **AGENTS.md** | Rules for work mode, process, agent behavior | T0 | 1.6 KB | Startup, always |
| **task_on_progress.md** | Current status, blockers, critical rules | T0 | 1 KB | Startup, always |
| **docs/ACTIVE-TASKS.md** | Consolidated open tasks across all groups | T0 | 10 KB | Startup, for task context |
| **docs/architecture.md** | System design, code map, component ownership | T1 | 7 KB | When working in any area |
| **docs/APP-MECHANISMS-VI.md** | Core flow descriptions (Vietnamese) | T1 | 7 KB | When understanding App startup/flow |
| **docs/adr/*.md** | Architecture decisions (0001-0004) | T1 | 21 KB | When working in relevant area |
| **docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md** | ST task status & INV (invariants) | T1 | 8 KB | When touching ST scope |
| **docs/refactoring/T89-FIT-SUMMARY.md** | Fit layout issue, root causes, acceptance | T1 | 2 KB | When working on Fit or GUI |
| **docs/refactoring/T89-FIT-LAYOUT-PLAN.md** | Full T89 plan (archived for reference) | T2 | 17 KB | When needing full context |
| **docs/refactoring/OPTIMIZE-CLEAN-SUMMARY.md** | OC task status & blockers (quick reference) | T1 | 5 KB | When working on OC tasks |
| **docs/refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md** | Full OC plan (archived for reference) | T2 | 44 KB | When needing detailed findings |
| **docs/refactoring/TEST-CLEANUP-SUMMARY.md** | TC objective, issues, task groups | T1 | 5 KB | When working on test cleanup |
| **docs/refactoring/TEST-CLEANUP-PLAN-2026-09-20.md** | Full TC plan (archived for reference) | T2 | 23 KB | When needing detailed findings |
| **docs/refactoring/TEST-SPEED-SUMMARY.md** | TS status, issues fixed, remaining work | T1 | 3 KB | When working on test speed |
| **docs/refactoring/TEST-SPEED-PLAN-2026-09-20.md** | Full TS plan (archived for reference) | T2 | 17 KB | When needing detailed measurements |
| **README.md** | Project overview, setup, build commands | T1 | 7 KB | First time setup |
| **docs/archive/** | Historical plans, completed tasks, old decisions | T2 | 122 KB | **Do not read** unless referenced or verifying decisions |
| **docs/archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md** | Full DT plan with all task details | T2 | 16 KB | Archive; DT tasks reference as needed |
| **docs/archive/progress-log-2026-09.md** | Historical task validation, commit logs | T2 | 3 KB | Archive; reference for prior-round decisions |

**Tier definitions:**
- **T0 (always read):** ≤ 12 KB total — entry points, status, rules
- **T1 (per work):** ≤ 15 KB per file — scope-specific mechanisms and plans
- **T2 (archive only):** No limit — historical data, not required for current work

**Cold-start budget:** T0 + one typical T1 file ≈ 7-10 KB (~2-3k tokens) when optimized.
