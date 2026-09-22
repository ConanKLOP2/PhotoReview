# Docs Cleanup Plan (2026-09-20)

**Goal:** Remove outdated/completed plans from active docs folder → keep only current, active files.

---

## Phase 1: Identify & Archive (Execute Now)

### Files to MOVE → `docs/archive/`

**Historical Plans (superseded):**
- `refactoring/REFACTOR-PLAN.md` (30 KB) — 2026-09-16, replaced by STRUCTURE-OPTIMIZE-PLAN
- `refactoring/REFACTOR-TASKS.md` (169 KB!) — 88 tasks, mostly DONE, very large
- `refactoring/PERF-DIAGNOSIS-PLAN.md` (19 KB) — 2026-09-16
- `refactoring/PERF-DIAGNOSIS-TASKS.md` (32 KB) — D00-D13, mostly blocked
- `refactoring/test-parity.md` (55 KB) — T10 task matrix, completed

**Diagnosis & Results (historical data):**
- `refactoring/diagnosis/*` (6 files) — 2026-09-16 perf traces
- `refactoring/results/*` (2 files) — 2026-09-16 benchmark results

**Planning (not yet implemented):**
- `refactoring/DOCS-TOKEN-DIET-PLAN-2026-09-20.md` (17 KB) — DT00-DT10, future work

**Review needed:**
- `APP-MECHANISMS-VI.md` — Tiếng Việt; is it core docs or historical? **Keep if core, Archive if outdated**

### Action
```bash
mkdir -p docs/archive/historical
git mv docs/refactoring/REFACTOR-PLAN.md docs/archive/historical/
git mv docs/refactoring/REFACTOR-TASKS.md docs/archive/historical/
git mv docs/refactoring/PERF-DIAGNOSIS-PLAN.md docs/archive/historical/
git mv docs/refactoring/PERF-DIAGNOSIS-TASKS.md docs/archive/historical/
git mv docs/refactoring/test-parity.md docs/archive/historical/
git mv docs/refactoring/DOCS-TOKEN-DIET-PLAN-2026-09-20.md docs/archive/future/
git mv docs/refactoring/diagnosis docs/archive/diagnosis-2026-09-16
git mv docs/refactoring/results docs/archive/results-2026-09-16
```

---

## Phase 2: Consolidate Active Files (Minimal Docs Root)

### Keep in `docs/`
- `architecture.md` — Core architecture reference (required)
- `README.md` — Project overview (required)
- `ACTIVE-TASKS-2026-09-20.md` — Master task tracking (NEW)
- `CLEANUP-PLAN.md` — This file

### Keep in `docs/adr/`
- `0001-image-decoder.md`
- `0002-ui-framework.md`
- `0003-journal-startup.md`
- `0004-presentation-project-separation.md`

### Keep in `docs/refactoring/`
**Active/Current Plans:**
- `STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md` — ST00-ST12 (DONE)
- `STRUCTURE-OPTIMIZE-STATUS.md` — Status summary
- `STRUCTURE-OPTIMIZE-TASKS.md` — Task reference
- `STRUCTURE-DECISIONS-Q-ST1-ST4.md` — Decision rationale

**Current Implementation Plans:**
- `OPTIMIZE-CLEAN-PLAN-2026-09-20.md` — OC01-OC18 (partial)
- `TEST-CLEANUP-PLAN-2026-09-20.md` — TC00-TC11 (ready)
- `T89-FIT-LAYOUT-PLAN.md` — GUI/layout (in progress)
- `TC00-BASELINE-REPORT.md` — Baseline data

---

## Phase 3: Review & Decide (User Input Needed)

### Q1: APP-MECHANISMS-VI.md
**Status:** Tiếng Việt, ~5 KB  
**Question:** Is this core documentation or historical?
- **If CORE:** Keep in `docs/` (and consider English translation)
- **If HISTORICAL:** Move to `docs/archive/`

### Q2: Translations
**Current state:** Some Tiếng Việt files mixed with English
**Option A:** Keep as-is (bilingual support)  
**Option B:** Keep only English in active docs; archive .md files if translated for historical reference

---

## Phase 4: Future (After DT Tasks)

### When DT02 completes (archive phase):
- Delete archived files from git history (or `git filter-branch`)
- Update INDEX / entry-point documentation

### When DT10 completes (final):
- Verify no broken links
- Measure final token load
- Archive this CLEANUP-PLAN.md

---

## Benefits

| Aspect | Before | After |
|--------|--------|-------|
| Docs files (main) | 35+ | ~10 |
| `refactoring/` files | 20+ | 8 |
| Size (docs/) | ~564 KB | ~220 KB (target) |
| Cold-start context waste | High | Reduced |
| Maintenance burden | Medium | Low |

---

## Rollback

All archived files remain in git history:
```bash
git log --follow docs/archive/historical/REFACTOR-TASKS.md
git checkout <SHA> -- docs/refactoring/REFACTOR-TASKS.md
```

No data loss.
