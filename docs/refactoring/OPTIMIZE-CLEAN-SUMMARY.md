# OC — Optimize & Clean (Summary)

**Status:** OC01–OC10 mostly complete; OC11–OC18 pending. OC14 is critical blocker for ST08/09/WD/OC15-18.

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
| OC11 | Decode/RAM optimization | 🔄 TODO | SourceBytesCache, decoder adapters | Measure sync-over-Task, cache write contention |
| OC12-OC13 | TBD | 🔄 TODO | — | — |
| **OC14** | **Undo unification (Ctrl+Z)** | **🔄 IN PROGRESS** | **UndoService, file-action sources** | **Move+Recycle both; unifies entry point** |
| OC15-OC18 | UI cleanup, viewport unify | 🔄 BLOCKED | — | Blocked on OC14 |

## Critical Path

**OC14 (Undo unification)** blocks:
- ST08 (FileActionController extraction)
- ST09 (DuplicateCleanupController extraction)
- All WD tasks (WPF dialog work)
- OC15–OC18 (dependent on resolved Ctrl+Z semantics)

## Key Concerns Resolved

- **F01:** DuplicateFinder survivor policy deterministic
- **F02:** TurboJPEG stride overflow guarded (checked long)
- **F03:** PreloadScheduler lifetime ownership clarified
- **F04:** STA/async dialog boundary (IUiScheduler abstraction done)
- **F08:** Cache invalidation atomic (epoch/lock added)
- **F09:** Undo history in-memory, <1MB journal
- **F10:** Benchmark profile propagation complete
- **F11:** PerfAnalyze grouping by worker/condition

See full findings and remediation details in [`../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](../archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md) section 2.
