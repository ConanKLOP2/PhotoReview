# Open Decisions

Consolidation of all pending decisions (Q-*) across active task groups. See linked plan files for full rationale.

| ID | Group | Question | Status | Plan |
|---|---|---|---|---|
| Q-D1 | DT (Docs) | Add read-tier + size rules to AGENTS.md? | ✅ YES | docs/AGENTS.md |
| Q-D2 | DT | Archive via `git mv` or delete? | ✅ YES (`git mv`) | docs/INDEX.md |
| Q-D3 | DT | Add CLAUDE.md? | ✅ YES | docs/CLAUDE.md (created) |
| Q-D4 | DT | Add `.ignore` for archive? | ✅ YES (verify tools respect) | DT08 task |
| Q-ST1 | ST | Separate PerfAnalysis project? | ✅ YES | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-ST2 | ST | Move PerfCsvListener to Core? | ✅ YES | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-ST3 | ST | Keep Cli→App dependency? | ✅ YES | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-ST4 | ST | Ctrl+Z: Move-only or Move+Recycle? | ✅ Move+Recycle | STRUCTURE-OPTIMIZE-STATUS.md |
| Q-T1 | TC | Queue keypresses in fixture? | ✅ YES | TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-T2 | TC | Read-count seam approach? | ✅ Real-file-in-temp | TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-T3 | TC | Real photos via env var? | ✅ YES | TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-T4 | TC | Use real Recycle Bin? | ✅ YES (via fixture) | TEST-CLEANUP-PLAN-2026-09-20.md |
| Q-OC14 | OC | Undo unification approach? | 🔄 IN PROGRESS | OPTIMIZE-CLEAN-PLAN-2026-09-20.md |
| Q-OC15 | OC | UI pattern cleanup scope? | 🔄 WAITING OC14 | OPTIMIZE-CLEAN-PLAN-2026-09-20.md |
| Q-S3 | TS | Journal test determinism? | 🔄 BLOCKED | TEST-SPEED-PLAN-2026-09-20.md |

**Legend:** ✅ Decided · 🔄 Pending · ⏸ Blocked

**Most critical blocker:** OC14 (Undo unification) — blocks ST08/ST09, WD, OC15-18, DT05 acceptance.

See [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md) for current task status.
