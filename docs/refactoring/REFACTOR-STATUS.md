# Refactoring Status Summary

**Last updated:** 2026-09-22  
**Current phase:** ST mostly complete (ST08/ST09 blocked on OC14); OC/WD/IO open

## ST (Structure) — Status

For full details see: [`STRUCTURE-OPTIMIZE-STATUS.md`](STRUCTURE-OPTIMIZE-STATUS.md)

| ID | Task | Status | Blocker |
|---|---|---|---|
| ST00-ST07, ST10-ST12 | Code cleanup & infrastructure | ✅ DONE | — |
| ST08-ST09 | Extract FileActionController, CleanupController | ⏸ BLOCKED | OC14 (Undo semantics) |

**Key invariants:** INV-1 through INV-12 in [`docs/architecture.md`](../architecture.md)

## OC (Optimize-Clean) — Key Blockers

Plan: [`OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](OPTIMIZE-CLEAN-PLAN-2026-09-20.md)

| ID | Status | Notes |
|---|---|---|
| OC14 | 🔄 IN PROGRESS | Undo entry-point unification (Ctrl+Z semantics) — **critical path** for ST08/ST09, WD, OC15-18 |
| OC15-OC18 | 🔄 TODO | Depend on OC14 |

## WD / IO / D — Status

- **WD:** All blocked on OC14 (Undo unification)
- **IO:** All blocked on IO01/IO02 contract lock-down
- **D:** D01/D02 blocked on D06 Procmon data; D08/D09 pending profiler analysis

Full tracking: [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md)
