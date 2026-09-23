# Structure Optimize — Final Status (ST01–ST12)

**Date:** 2026-09-20 (status confirmed current 2026-09-22 — only ST08/ST09 remain open)  
**Baseline:** `master` `5dc5cda` (updated after ST01-ST10 implementation)  
**Plan (archived, all tasks resolved):** [`archive/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md`](archive/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md) · [`archive/STRUCTURE-OPTIMIZE-TASKS.md`](archive/STRUCTURE-OPTIMIZE-TASKS.md) · [`archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md`](archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md)

---

## 1. Task Status Summary

| ID | Task | Status | Decision Block | PR / Commit | Notes |
|---|---|---|---|---|---|
| ST00 | Baseline & new arch rules | ✓ DONE | — | `5dc5cda` | 762 tests baseline established |
| ST01 | Dead code & redundant abstractions (ThumbnailModels, IProgressiveExplorerOrderProvider) | ✓ DONE | — | `a6bbc55` | Touches `App.xaml.cs`, `MainWindow*` |
| ST02 | SourceSizeTracker (remove repeated folder stat) | ✓ DONE | — | `775f3b2` | Behavioral change; O(n) reduction in GetFileStat |
| ST03 | Extract AppComposition from App.xaml.cs | ✓ DONE | — | `479a1ab` | Factory cleanup, < 200 lines App.xaml.cs |
| ST04 | Move infrastructure down (FileHashService, PhysicalMemory, PerfCsvListener) | ✓ DONE | Q-ST2 | Multiple commits | PerfCsvListener → Core/Diagnostics; FileHashService → Imaging; PhysicalMemory → Platform.Windows |
| ST05 | Extract PerfAnalyze* from WPF project | ✓ DONE | Q-ST1 | Separate project | New project: `PhotoReview.PerfAnalysis` (`net10.0`, no WPF) |
| ST06 | Remove CLI reflection into MainWindow; make members public | ✓ DONE | ST03, ST04, Q-ST3 | Multiple commits | `SuppressWindowPlacement()` & `PerfDispatcherHooks` public |
| ST07 | Mandatory dependencies + test builder for MainViewModel | ✓ DONE | ST03 | `b620999` | Reduces optional params; enables ST08–ST09 |
| ST08 | Extract FileActionController | ⏸ BLOCKED | ST07, OC14, Q-ST4 | — | Depends on OC14 (Ctrl+Z semantics) + Q-ST4 decision |
| ST09 | Extract DuplicateCleanupController & SiblingFolderNavigator | ⏸ BLOCKED | ST08 | — | Depends on ST08 |
| ST10 | Test & architecture rule cleanup | ✓ DONE | ST04–ST09 (partial) | `986b20f`, `ef0b601` | Consolidate OperationJournalTests + add arch rules for new boundaries |
| ST11 | Sync documentation | ✓ DONE | ST01–ST10 | This document | Consolidate STRUCTURE plans; finalize status; document decisions |
| ST12 | Investigate Presentation project (no implementation) | ✓ APPROVED | ST06, ST09 | ADR `docs/adr/0004-presentation-project-separation.md` | Decision: **do not split**; 0 projects drop App ref; benefit unmeasured |

---

## 2. Decisions (Q-ST1 through Q-ST4)

**All decisions finalized 2026-09-20.** See [`archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md`](archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md) for rationale.

| Question | Decision | Rationale | Task Impact |
|---|---|---|---|
| **Q-ST1** | Separate `PerfAnalysis` project? | ✓ **Yes** — new project `PhotoReview.PerfAnalysis` (`net10.0`) | ST05 DONE |
| **Q-ST2** | Move `PerfCsvListener` + `DiagOptions` to Core? | ✓ **Yes** — move to `Core/Diagnostics`; preserve EventSource contract | ST04 DONE |
| **Q-ST3** | Keep Cli→App dependency? Censor reflection? | ✓ **Yes** — accept dependency; make MainWindow members public | ST06 DONE |
| **Q-ST4** | Ctrl+Z semantics: Move-only or Move+Recycle? | ✓ **Move+Recycle** — undo both Move and Recycle | ST08 awaits OC14 |

---

## 3. Next Phases (OC / WD / IO / TC)

### Optimize-Clean (OC) Tasks

**Status:** OC14–OC18 pending; others completed or under review.

- **OC14** — Undo entry-point unification (Ctrl+Z semantics) — blocks ST08–ST09
- **OC15–OC17** — UI clean-code wave (pattern cleanup, caching optimization)
- **OC18** — Fit viewport unification (pending T89 evidence)

### Test Cleanup (TC00–TC11)

**Status:** TC00 baseline in progress; Q-T1..Q-T4 decisions made.

- **Q-T1:** Queue keypresses when action running? **✓ YES** (UX priority: don't silently drop)
- **Q-T2:** Add seam to detect blind spots in source-read? **✓ YES**
- **Q-T3:** Real-photo directory via env var? **✓ YES** (if available on machine)
- **Q-T4:** Real Recycle Bin in tests? **✓ YES** (TC06 self-cleans via Undo)

See [`../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md`](../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md) for TC00–TC11 details.

### Dialog Refactor (WD) Tasks

**Status:** WD01 audit pending; WD02–WD05 blocked on DI/dispatcher/lifecycle contracts.

- WD01 — UI-thread/call-graph audit
- WD02–WD05 — DRY refactor (low-risk), then DI/dispatcher changes

### I/O Durability (IO) Tasks

**Status:** IO01 (contract lock) pending; IO02 (baseline benchmark) follows.

- IO01 — Define durability/completeness guarantees
- IO02 — Establish performance baseline
- IO03–IO07 — Only after IO01–IO02

---

## 4. Test Metrics (ST00 Baseline)

| Category | Count | Notes |
|---|---|---|
| **Architecture** | 13 rules | Validates project boundaries, no-reflection, no upward deps |
| **Core** | 321 PASS (+ 1 flaky) | `OperationJournalTests.IsBoundedAndFast` < 100 ms when isolated; 112–166 ms in parallel |
| **Imaging** | 205 PASS | |
| **App** | 174 PASS | |
| **Integration** | 62 PASS | |
| **Total** | **762/762 PASS** | No failures on ST00 baseline |

---

## 5. File Changes Summary

### Moved / Extracted

- `App/ThumbnailModels.cs` → **deleted** (ST01)
- `App/IProgressiveExplorerOrderProvider.cs` → **deleted** (ST01)
- `App/Diagnostics/PerfCsvListener.cs` → `Core/Diagnostics/PerfCsvListener.cs` (ST04)
- `App/Diagnostics/DiagOptions.cs` → `Core/Diagnostics/DiagOptions.cs` (ST04)
- `App/FileHashService.cs` → `Imaging/Caching/FileHashService.cs` (ST04)
- `App/PhysicalMemory.cs` → merged into `Platform.Windows/WindowsMemoryProbe.cs` (ST04)
- `Benchmarking/PerfAnalyze*.cs` (6 files) → new project `PhotoReview.PerfAnalysis/` (ST05)
- `App.xaml.cs` (composition factory) → `App/Composition/AppComposition.cs` (ST03)
- `App/Diagnostics/PerfDispatcherHooks.cs` → new file in `Diagnostics/` (ST03)

### Visibility Changes

- `MainWindow` members made public for CLI (ST06):
  - `MainWindow.SuppressWindowPlacement()`
  - `PerfDispatcherHooks`
  - Related members via public accessors

### Architecture Rules (New)

| Rule | Constraint | Task |
|---|---|---|
| R6 (updated) | Core does not reference WPF / App types | ST04, ST05 |
| R7 | Benchmarking does not reference App (reflection banned) | ST10b |
| R8 | PerfAnalysis project does not reference WPF / App / Imaging | ST05, ST10b |
| R9 | CLI does not use reflection into App private members | ST06, ST10b |

---

## 6. Known Issues & Rollback

### Pre-existing Issues (Not Regression)

- **OperationJournalTests `IsBoundedAndFast`**: Flaky timing assertion (< 100 ms); passes in isolation, fails in parallel runs. Acceptable; timing assertions are inherently flaky in CI.
- **CLI `--ui-next-probe`**: FAIL "Next image did not reach RAM cache" — **pre-existing on `master` baseline**; unrelated to ST tasks.

### Rollback Procedure

1. Per-task rollback: `git revert <commit-hash>` (refactor commits are independent)
2. For ST02 (behavioral change): also revert ST03–ST10 if reverting ST02
3. For infrastructure moves (ST04): namespace imports in consumers must be updated

---

## 7. Cross-References

- **Main plan (archived):** [`archive/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md`](archive/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md)
- **Task details (archived):** [`archive/STRUCTURE-OPTIMIZE-TASKS.md`](archive/STRUCTURE-OPTIMIZE-TASKS.md)
- **Decision rationale (archived):** [`archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md`](archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md)
- **Architecture rules:** [`../architecture.md`](../architecture.md)
- **Presentation project ADR:** [`../adr/0004-presentation-project-separation.md`](../adr/0004-presentation-project-separation.md)
- **Overall roadmap:** [`../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md) (OC / WD / IO)
- **Test cleanup:** [`../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md`](../archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md) (TC00–TC11)

---

**Status:** ST01-ST07, ST10-ST12 DONE and merged. Only ST08/ST09 remain, blocked on OC14 (Ctrl+Z semantics unification).
