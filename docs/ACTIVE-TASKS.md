# Active Tasks — Consolidated Work Remaining

**Updated:** 2026-09-23
**Status:** ST, TS00-04, DF, CQ all DONE and merged to `master`. New group **AR** (architecture review, plan only). TC/OC/WD/IO/DT/T89(GUI)/D still open.

---

## Summary by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** (Architecture Review 2026-09-23) | 🔄 AR00 DONE on branch `docs/arch-review-plan`; AR01-AR07 TODO | 5 decisions Q-AR1..Q-AR5 pending. Summary: `refactoring/ARCH-REVIEW-SUMMARY.md`. |
| **ST** (Structure Optimize) | ✅ DONE (ST08/ST09 blocked) | ST01-ST07, ST10-ST12 merged. ST08/ST09 wait on OC14. See `refactoring/STRUCTURE-OPTIMIZE-STATUS.md`. |
| **TS** (Test Speed / gate reliability) | ✅ TS00-TS04 DONE, TS05-TS10 TODO | Gate no longer hangs, runs ~23s. Plan: `archive/historical/TEST-SPEED-PLAN-2026-09-20.md`. |
| **DF** (Double-click → Fit) | ✅ DONE | PR #14 merged. Plan archived: `refactoring/archive/DBLCLICK-FIT-PLAN-2026-09-21.md`. |
| **CQ** (Code Quality / Warnings) | ✅ DONE | 634→0 warnings (PR #18 merged). Plan: `refactoring/CQ-WARNINGS-PLAN.md`. |
| **TC** (Test Cleanup) | 🔄 TODO (unverified prior claims) | TS10 must re-audit before trusting any "done" status. Plan: `archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md`. |
| **T89** (Fit Layout — GUI acceptance) | 🔄 GUI acceptance only | T89.1-T89.2 (`10ff31f`) **merged to `master` via PR #15**; branch deleted. DF02 Fit tests skipped in `9f880d1`. STA/GUI acceptance still TODO. Plan: `archive/historical/T89-FIT-LAYOUT-PLAN.md`. |
| **OC** (Optimize/Clean) | 🔄 ~65% done | OC14 (Undo unification) is the key blocker for ST08/09, WD, and OC15-18. Plan: `archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`. |
| **WD** (WPF Dialog) | 🔄 All TODO | WD01 → implemented by AR04 (if Q-AR2 = yes, no longer waits for OC14); WD02 low-risk part → AR03c; WD03-06 blocked on OC14. |
| **IO** (I/O Durability) | 🔄 All TODO | Blocked on IO01 contract lock-down. |
| **DT** (Docs Token Diet) | 🔄 Partial | DT00-03, DT08, DT09 DONE; Q-D1..Q-D4 decided. DT04-07, DT10 TODO. Plan: `archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`. |
| **D** (Perf Diagnosis) | 🔄 Mixed, partially blocked | D01/D02 wait on D06 data; D08/D09/D12 wait on D07. **Note:** `--perf-session` ran a non-production graph (no preload) until AR02c — re-baseline in AR02e before trusting D numbers. |

---

## AR Tasks — Architecture Review 2026-09-23

**Summary:** [`refactoring/ARCH-REVIEW-SUMMARY.md`](refactoring/ARCH-REVIEW-SUMMARY.md) · **Per-task plans:** `refactoring/arch-review/`

| ID | Name | Decision | Depends on | Real machine/GUI | Status |
|----|------|----------|------------|------------------|--------|
| AR00 | Review + plans + ADR 0005 draft + stale-status fixes | — | — | No | ✅ DONE on `docs/arch-review-plan` (PR pending) |
| AR01 | Ship TurboJpeg: explicit registration, native probe, Settings reflects availability, release gate | Q-AR1 | — | 1 check | TODO |
| AR02a | `AppHost`, caches from `IAppPaths`, viewport provider, presentation/preload/move seams | Q-AR3 | — | Fit check (with T89) | TODO |
| AR02b | Integration tests build MainWindow via `AppHost` (12 sites) | Q-AR3 | AR02a | No | TODO |
| AR02c | `Benchmark.Cli` perf-session/ui-probe on production graph; report effective config | Q-AR3 | AR02a | Run once | TODO |
| AR02e | Perf re-baseline on production graph | — | AR02c | **Yes** (user machine) | TODO |
| AR02d | Delete test ctors/`CreateTestViewModel`; public fields → read-only properties | Q-AR3 | AR02b, AR02c | No | TODO |
| AR03 | SourceBytes policy (a), `Platform.Windows` without WPF (b), startup dialog via `IDialogService` (c) | — | — | No | TODO |
| AR04 | UI-thread affinity: remove 47 `ConfigureAwait(false)` in App, arch test, catalog Debug guard | Q-AR2 | AR02c, AR02e | **Yes** (GUI + perf) | TODO |
| AR06 | One release location, delete broken `outputs/release`, prune worktrees/leftovers | Q-AR4 | — | No | TODO |
| AR07 | Fix 17 broken links, restore ADR evidence, link gate, docs-budget fix, group triage | Q-AR5 | AR00 | No | TODO |

---

## TS Tasks — Test Speed and Gate Reliability

**Plan:** [`archive/historical/TEST-SPEED-PLAN-2026-09-20.md`](archive/historical/TEST-SPEED-PLAN-2026-09-20.md)

| ID | Name | Priority | Status |
|----|------|----------|--------|
| TS00 | Hang guard in gate and CI | P0 | ✅ DONE |
| TS01 | Fix hanging App test and its vacuous/failing siblings | P0 | ✅ DONE |
| TS02 | Cheap, clean fixture | P0 | ✅ DONE |
| TS03 | Restore `StaTestHost.WaitForAsync` | P0 | ✅ DONE |
| TS04 | Restore Vietnamese text + mojibake guard rule | P0 | ✅ DONE |
| TS05 | Deterministic, faster journal test (needs Q-S3) | P1 | TODO |
| TS06 | Honest hot-path tests (no PASS without assertion) | P1 | TODO |
| TS07 | Temp hygiene (`TempRoot`) | P1 | TODO |
| TS08 | Timing report in `verify-all.ps1` | P2 | TODO |
| TS09 | CI/local filter alignment | P2 | TODO |
| TS10 | Re-audit commits claiming TC01-TC11 | P1 | TODO — do before trusting any TC status |

---

## TC Tasks — Test Cleanup

**Plan:** [`archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md`](archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md)
**Decisions finalized:** Q-T1 queue keypresses · Q-T2 read-count seam · Q-T3 real photos via env var · Q-T4 native Recycle Bin

> **TS10 Audit Results** (2026-09-22):
> - d5fc9c8 (TC01-TC03): ✓ Genuine scaffolding. PhotoFolderBuilder, ReadBudgetProbe, WarmNavigationReadBoundsTests.TC03 all exist with real assertions.
> - fe00f36 (TC08-TC10): ✓ TC08/TC10 real. InterleavedFileActionSequenceTests.TC08a/b/c exist with production-code tests. [Trait] categorization works.
> - 1c1f728 (TC11): ✓ CI gate real. verify-all.ps1 filter logic implemented, tested with -Stress/-Native/-Integration/-Slow/-All.
> - 34886ec (TC07): ✗ Real tests exist with assertions, but **added to Integration.Tests (wrong project**). RealPhotosManualTests.cs has 2 real methods.
> - 7cab725 (TC06): ✗ Real tests exist with assertions, but **added to Integration.Tests (wrong project)**. NativeRecycleBinTests.cs has 2 real methods.
> **Action:** TC06/TC07 tests are legit but wrongly located. Move to App.Tests/HotPath/ or decide if Integration.Tests is correct. TC01-TC05 scaffolding is solid.

| ID | Name | Status | Blocker | Notes |
|----|------|--------|---------|-------|
| TC01 | Fixture builder | ✅ EXISTS | — | PhotoFolderBuilder.cs: 103 lines, creates synthetic JPEG+PNG+corrupted+non-image files |
| TC02 | ReadBudgetProbe | ✅ EXISTS | — | ReadBudgetProbe.cs: 76 lines, wraps ReviewMetrics for I/O counting |
| TC03 | Disk-read invariants | ✅ EXISTS | TC01, TC02 | WarmNavigationReadBoundsTests.TC03: real assertions with ReadBudgetProbe |
| TC04 | Rapid Next (key-repeat) | TODO | TC01 | Marked Skip("not yet implemented") in WarmNavigationReadBoundsTests |
| TC05 | Rapid Move/Delete | ✅ EXISTS | TC01, TC02 | WarmNavigationReadBoundsTests.TC05: tests rapid Next without await, real assertions |
| TC06 | Native Recycle Bin | ✅ EXISTS* | Q-T4 done | **Integration.Tests/HotPath/NativeRecycleBinTests.cs — LOCATION ERROR.** 2 real methods: DeleteMultiple/DeleteRapidly with 52 assertions |
| TC07 | Real photos manual | ✅ EXISTS* | Q-T3 done | **Integration.Tests/HotPath/RealPhotosManualTests.cs — LOCATION ERROR.** 2 real methods: WarmNext/FileActions with ReadBudgetProbe assertions |
| TC08 | Replace G1 tests | ✅ EXISTS | TC05 | InterleavedFileActionSequenceTests.TC08a/b/c: production-code replacement tests (183 lines added) |
| TC09 | Flake audit | 🔄 PARTIAL | — | TS02 `FixturePerfTest` fixed (branch `test/tc09-ts02-flake`): gate asserts count/composition/size only; cost budget → `Category=Slow` test on thread CPU time (≤10 s, ~1.5 s cold). Full suite 3× 0 failures. Remaining: OperationJournalTests.LargeJournal |
| TC10 | Test categorization | ✅ EXISTS | — | [Trait("Category", ...)] added to 46+ test classes, filter works in verify-all.ps1 |
| TC11 | CI gate integration | ✅ EXISTS | TC05 | verify-all.ps1 gate filter logic working; CI workflow updated |

**Known issue:** Flaky test `OperationJournalTests.LargeJournal_ReadCommittedMoves_IsBoundedAndFast` (100ms assert; 112-166ms in parallel). Fix in TC09.

---

## T89 — Fit Layout (code on master, GUI acceptance open)

**Plan:** [`archive/historical/T89-FIT-LAYOUT-PLAN.md`](archive/historical/T89-FIT-LAYOUT-PLAN.md)
**Branch:** `feature/Fit-Layout-Status` (commit `10ff31f`, T89.1-T89.2) — **merged to `master` via PR #15** (verified 2026-09-23: `git branch --contains 10ff31f` → master; branch no longer on origin).

| Aspect | Status |
|--------|--------|
| Wheel scroll | ✅ DONE (merged, PR #9) |
| Pan threshold | ✅ DONE (merged, PR #9) |
| Viewport convergence helpers | ✅ on master (PR #15) |
| Fit initial | TODO — STA layout assertion needed |
| Fit zoom | TODO — viewport + scrollbar edge cases |
| GUI acceptance | TODO — manual test on real images; do it together with AR02a step 3 (production viewport currently `(0,0)` in `ApplyInitialViewMode`, finding F3) |

**Blocker:** do not reduce `ApplyFitViewAsync` to a single pass without GUI/STA evidence.

---

## OC Tasks — Optimize/Clean

**Plan:** [`archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md)

| ID | Name | Status | Blocker |
|----|------|--------|---------|
| OC01 | Baseline regression fixtures | TODO | UI-thread dialog boundary |
| OC06 | Journal/Undo/Recovery | PARTIAL | Undo history + recovery retry done; native restore identity TODO |
| OC07 | Display state + T89 | PARTIAL | Display subset done; T89 GUI/STA acceptance TODO |
| OC09 | Benchmark semantics | PARTIAL | Profile propagation done; workload action/report TODO |
| OC11 | Decode/RAM optimization | TODO | Pending P95 measurements |
| OC12 | Clean code limits | TODO | Post-implementation cleanup |
| OC13 | Integration/validation | TODO | Final testing before publish |
| OC14 | Undo unification + gate | PARTIAL | Mutual exclusion done; semantics tests refactored — **key blocker for ST08/09, WD, OC15-18** |
| OC15-OC18 | Clean-code wave | TODO | OC14 |

---

## WD Tasks — WPF Dialog Service

| ID | Name | Status |
|----|------|--------|
| WD01 | UI-thread audit | TODO → implemented by **AR04** / ADR 0005 (needs Q-AR2) |
| WD02 | DRY refactor (low-risk) | TODO — startup `MessageBox` covered by **AR03c** |
| WD03 | DI refactor | TODO (needs WD01) |
| WD04-WD06 | Window lifecycle | TODO (needs WD01-WD03) |

---

## IO Tasks — I/O Durability (Blocked on Contract)

| ID | Name | Status |
|----|------|--------|
| IO01 | Durability contract | TODO |
| IO02 | Benchmark baseline | TODO |
| IO03-IO04 | Async FileSystem | TODO (needs IO01/IO02) |
| IO05 | Permission reproduction | TODO (needs IO01/IO02) |
| IO06 | Buffer optimization | TODO (needs IO01/IO02) |
| IO07 | Temp-name optimization | TODO (needs IO01/IO02) |

---

## DT Tasks — Docs Token Diet (Partial)

**Plan:** [`archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`](archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md)

DT00-DT03, DT08, DT09 DONE; Q-D1..Q-D4 decided (see OPEN-DECISIONS). DT04-DT07, DT10 TODO. Known gap: `docs-budget.ps1` does not count `docs/INDEX.md` as T0 → fixed in AR07 §4.

---

## D Tasks — Perf Diagnosis (Mixed, Partially Blocked)

**Plan:** [`archive/historical/PERF-DIAGNOSIS-TASKS.md`](archive/historical/PERF-DIAGNOSIS-TASKS.md)

| ID | Name | Status | Blocker |
|----|------|--------|---------|
| D01 | AppLog script | BLOCKED | D06 data |
| D02 | File access count | BLOCKED | D06 data |
| D06 | Perf session run | TODO | — |
| D07 | Procmon analysis | TODO | D06 |
| D08 | ETW deep-dive | TODO | D07 |
| D09 | GC + memory | TODO | D07 |
| D12 | Approve optimizations | TODO | D08-D09, user sign-off |

---

## CQ Summary — Code Quality / Warnings (COMPLETE)

✅ **DONE & MERGED (PR #18).** 634 → 0 warnings. All analyzer waves (Wave 1: CQ01-03, Wave 2: CQ04/05/08, Wave 3: CQ06/07) complete. See `refactoring/CQ-WARNINGS-PLAN.md` for details and real bugs fixed along the way.

---

## Dependencies & Critical Path

```
TC01 → TC02 → (TC03 ∥ TC04) → TC05 → TC08 → (TC06, TC07) → TC09-TC11
                                                (TS10 must re-audit first)

OC14 (Undo) → ST08 → ST09
           → WD03 → WD04-WD06
           → OC15-OC18

AR00 → (AR01 ∥ AR03 ∥ AR06) → AR07
AR00 → AR02a → AR02b ∥ AR02c → AR02e (user machine) → AR02d
                               AR02e → AR04 (= WD01) → WD02 rest

IO01-IO02 → (IO03-IO07)

T89 GUI/STA acceptance (code already on master) ↔ AR02a step 3 (viewport)

DT02 → DT03 → (DT04 ∥ DT05) → DT06-DT10
```

---

## Notes

- **Completed work is archived**, not deleted: `docs/refactoring/archive/` holds resolved ST plans/tasks/decisions and the completed DF plan.
- **TC tasks** are high-priority but must wait on TS10 (re-audit) before any status is trusted.
- **T89** must have GUI/STA acceptance before merging `feature/Fit-Layout-Status` or touching `ApplyFitViewAsync` further.
- **OC14** is the single biggest unblock: it gates ST08/09, all of WD, and OC15-18.
