# Refactoring Status Summary

**Last updated:** 2026-09-24  
**Current phase:** ST mostly complete (ST08/ST09 blocked on OC14); WD01 done, WD02-06 closed; OC/IO open

## ST (Structure) — Status

For full details see: [`STRUCTURE-OPTIMIZE-STATUS.md`](STRUCTURE-OPTIMIZE-STATUS.md)

| ID | Task | Status | Blocker |
|---|---|---|---|
| ST00-ST07, ST10-ST12 | Code cleanup & infrastructure | ✅ DONE | — |
| ST08-ST09 | Extract FileActionController, CleanupController | ⏸ BLOCKED | OC14 (Undo semantics) |

**Key invariants:** INV-1 through INV-12 in [`docs/architecture.md`](../architecture.md)

## OC (Optimize-Clean) — Key Blockers

Plan: [`../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md)

| ID | Status | Notes |
|---|---|---|
| OC14 | 🔄 PARTIAL, re-scoped (Q-AR5) | Re-scoped to "Undo gate location" — **critical path** for ST08/ST09, OC15-18 (no longer blocks WD, see ADR 0005/AR04) |
| OC15-OC18 | 🔄 TODO | Depend on OC14 |

## WD / IO / D — Status

- **WD:** WD01 unblocked via AR04 (#37, ADR 0005); WD02-06 closed 2026-09-23 (Q-AR5) — WD02 low-risk part covered by AR03c, WD03-06 had no known dialog bug to justify keeping open
- **IO:** IO01 kept (Q-AR5); IO02-07 closed 2026-09-23 (Q-AR5) — speculative until IO01's contract exists
- **D:** Closed 2026-09-23 (Q-AR5) — see `PERF-STATUS.md`

Full tracking: [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md)
