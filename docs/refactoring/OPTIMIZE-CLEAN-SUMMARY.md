# OC — Optimize & Clean (Summary)

**Status:** per-group status is in [`docs/ACTIVE-TASKS.md`](../ACTIVE-TASKS.md) (single source). This file keeps the OC task detail only. OC14 merged (#64); OC15-18 merged (#73, OC18 Fit GUI check under T89).

Full historical details archived: [`docs/archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`](../archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md) · [`../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md)

## Task Status

| ID | Task | Status | Files | Acceptance |
|---|---|---|---|---|
| OC01 | Baseline & regression fixtures | ✅ DONE | Tests; contracts locked | All tests PASS baseline (761/761+) |
| OC02 | Duplicate finder & UI thread | ✅ DONE | DuplicateFinder, MainViewModel, WpfDialogService | >=1 survivor per group; no UI deadlock |
| OC03 | Decoder safety | ✅ DONE | TurboJpegDecoder, WicDirectDecoder | No unchecked overflow; graceful fallback |
| OC04 | Preload lifetime | ✅ DONE | PreloadScheduler | No late cache write or unobserved error |
| OC05 | Cache invalidation | ✅ DONE | SourceBytesCache, DiskCacheStore | Invalidated work doesn't repopulate; no orphans |
| OC06 | Journal/Undo/Recovery | ✅ MOSTLY DONE | UndoService, OperationJournal, SessionWriter | History <1MB lookup; native Recycle identity TODO |
| OC07 | Display state & T89 start | ✅ PARTIAL | MainViewModel, CompareViewModel | T89 GUI acceptance still TODO (STA/layout tests) |
| OC08 | Folder/Catalog I/O | ✅ DONE | FolderLoadCoordinator, ReviewCatalog | No O(n²) remapping; metadata consistency |
| OC09 | Benchmark semantics | ✅ PARTIAL | BenchmarkModels, CLI/GUI parity | Action races decode; failed profiles recorded; image-based testing TODO |
| OC10 | Perf analysis grouping | ✅ DONE | PerfAnalyze | Group by worker/condition; R-CONT sane |
| OC11 | Decode/RAM optimization | ✅ DONE | SourceBytesCache, decoder adapters | Done via the perf series #39–#48 |
| OC12-OC13 | TBD | 🔄 TODO | — | — |
| **OC14** | **Undo unification (Ctrl+Z)** | ✅ DONE (#64) | UndoService, `FileActionGate` in MainViewModel | Re-scoped to "Undo gate location" (Q-AR5); mutual exclusion done, semantics tests refactored |
| OC15-OC18 | UI cleanup, viewport unify | ✅ DONE (#73) | MainWindow*, pan threshold | OC16 n/a, OC17 already done; OC18 Fit check under T89 (user) |

## Key Concerns Resolved

F01–F11 (survivor policy, stride overflow, preload lifetime, STA boundary, cache invalidation, undo history size, benchmark propagation, PerfAnalyze grouping) are all resolved — full list with remediation notes archived in [`archive/OPTIMIZE-CLEAN-SUMMARY-detail.md`](archive/OPTIMIZE-CLEAN-SUMMARY-detail.md). Full findings: [`../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md) section 2.
