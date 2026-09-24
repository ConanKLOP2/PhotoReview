# Structure Optimize — Final Status (ST01–ST12)

**Date:** 2026-09-20 (confirmed current 2026-09-22 — only ST08/ST09 remain open)
**Baseline:** `master` `5dc5cda`
**Full history (test metrics, file-change list, rollback procedure):** [`archive/STRUCTURE-OPTIMIZE-STATUS-detail.md`](archive/STRUCTURE-OPTIMIZE-STATUS-detail.md) · plan: [`archive/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md`](archive/STRUCTURE-OPTIMIZE-PLAN-2026-09-20.md) · tasks: [`archive/STRUCTURE-OPTIMIZE-TASKS.md`](archive/STRUCTURE-OPTIMIZE-TASKS.md) · decisions: [`archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md`](archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md)

---

## 1. Task Status Summary

| ID | Task | Status | Decision Block | PR / Commit | Notes |
|---|---|---|---|---|---|
| ST00 | Baseline & new arch rules | ✓ DONE | — | `5dc5cda` | 762 tests baseline |
| ST01 | Dead code & redundant abstractions | ✓ DONE | — | `a6bbc55` | Touches `App.xaml.cs`, `MainWindow*` |
| ST02 | SourceSizeTracker | ✓ DONE | — | `775f3b2` | O(n) reduction in GetFileStat |
| ST03 | Extract AppComposition from App.xaml.cs | ✓ DONE | — | `479a1ab` | < 200 lines App.xaml.cs |
| ST04 | Move infra down (FileHashService, PhysicalMemory, PerfCsvListener) | ✓ DONE | Q-ST2 | Multiple | See detail doc for move list |
| ST05 | Extract PerfAnalyze* from WPF project | ✓ DONE | Q-ST1 | Separate project | New project `PhotoReview.PerfAnalysis` (`net10.0`, no WPF) |
| ST06 | Remove CLI reflection into MainWindow | ✓ DONE — superseded by AR02d | ST03, ST04, Q-ST3 | Multiple | AR02d replaced public mutable fields with read-only properties (`Files`, `CurrentIndex`, …) |
| ST07 | Mandatory deps + test builder for MainViewModel | ✓ DONE | ST03 | `b620999` | Enables ST08–ST09 |
| ST08 | Extract FileActionController | ⏸ BLOCKED | ST07, OC14, Q-ST4 | — | Depends on OC14 (Ctrl+Z semantics) |
| ST09 | Extract DuplicateCleanupController & SiblingFolderNavigator | ⏸ BLOCKED | ST08 | — | Depends on ST08 |
| ST10 | Test & architecture rule cleanup | ✓ DONE | ST04–ST09 (partial) | `986b20f`, `ef0b601` | New arch rules for new boundaries |
| ST11 | Sync documentation | ✓ DONE | ST01–ST10 | This document | |
| ST12 | Investigate Presentation project (no implementation) | ✓ APPROVED | ST06, ST09 | ADR [`0004`](../adr/0004-presentation-project-separation.md) | Do not split; 0 projects drop App ref |

## 2. Decisions (Q-ST1–Q-ST4)

All finalized 2026-09-20; rationale in [`archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md`](archive/STRUCTURE-DECISIONS-Q-ST1-ST4.md). Summary: **Q-ST1** separate `PerfAnalysis` project — yes (ST05). **Q-ST2** move `PerfCsvListener`+`DiagOptions` to Core — yes (ST04). **Q-ST3** keep Cli→App dependency, make `MainWindow` members public — yes (ST06); superseded 2026-09-23 by AR02d (read-only properties, no more public fields). **Q-ST4** Ctrl+Z semantics — Move+Recycle, both undoable (ST08 awaits OC14).

## 3. Next Phases (OC / WD / IO / TC)

- **Optimize-Clean (OC):** OC14–OC18 pending; see [`OPTIMIZE-CLEAN-SUMMARY.md`](OPTIMIZE-CLEAN-SUMMARY.md).
- **Test Cleanup (TC):** TC00–TC11 done; see [`TEST-CLEANUP-SUMMARY.md`](TEST-CLEANUP-SUMMARY.md).
- **Dialog Refactor (WD):** WD01 done via AR04 (#37, ADR 0005); WD02–WD06 closed 2026-09-23 (Q-AR5, no known dialog bug to justify keeping open).
- **I/O Durability (IO):** IO01 (contract) done via ADR 0007; IO02 baseline done; IO03–IO07 tracked in `task_on_progress.md`.

## 4. Architecture Rules (New)

| Rule | Constraint | Task |
|---|---|---|
| R6 (updated) | Core does not reference WPF / App types | ST04, ST05 |
| R7 | Benchmarking does not reference App (reflection banned) | ST10b |
| R8 | PerfAnalysis project does not reference WPF / App / Imaging | ST05, ST10b |
| R9 | CLI does not use reflection into App private members | ST06, ST10b |

Full test metrics (ST00 baseline: 762/762 PASS), the moved/extracted file list, and the rollback procedure are archived in [`archive/STRUCTURE-OPTIMIZE-STATUS-detail.md`](archive/STRUCTURE-OPTIMIZE-STATUS-detail.md).

---

**Status:** ST01-ST07, ST10-ST12 DONE and merged. Only ST08/ST09 remain, blocked on OC14 (Ctrl+Z semantics unification).
