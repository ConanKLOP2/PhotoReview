# Active Tasks — Consolidated Work Remaining

**Updated:** 2026-09-24
**Status:** AR00-AR07, ST, TS, TC, DF, CQ, L (i18n L00-L11) all DONE and merged to `master` (v2.0.64). IO01 decided (ADR 0007); IO03-IO05, OC14, DT10 open. T89 (GUI acceptance) waits on the user.

---

## Summary by Group

| Group | Status | Notes |
|-------|--------|-------|
| **L** (I18N: EN + VI, community JSON catalogs) | ✅ L00–L11 DONE (#55, #61, 2026-09-24); L12 (Vietnamese copy polish) open | Q-L1..Q-L8 decided. Plan: `refactoring/I18N-PLAN.md`, ADR 0006, `TRANSLATING.md`. |
| **AR** (Architecture Review 2026-09-23) | ✅ AR00–AR07 all DONE (AR02d #35, AR04 #37); only T89/AR04 GUI acceptance left (user) | Q-AR1..Q-AR5 all decided 2026-09-23. Summary: `refactoring/ARCH-REVIEW-SUMMARY.md`. |
| **ST** (Structure Optimize) | ✅ DONE (ST08/ST09 blocked) | ST01-ST07, ST10-ST12 merged. ST08/ST09 wait on OC14. See `refactoring/STRUCTURE-OPTIMIZE-STATUS.md`. |
| **TS** (Test Speed / gate reliability) | ✅ TS00-TS07, TS10 DONE; TS08/TS09 closed (Q-AR5) | Gate no longer hangs, runs ~23s. Plan: `archive/historical/TEST-SPEED-PLAN-2026-09-20.md`. |
| **DF** (Double-click → Fit) | ✅ DONE | PR #14 merged. Plan archived: `refactoring/archive/DBLCLICK-FIT-PLAN-2026-09-21.md`. |
| **CQ** (Code Quality / Warnings) | ✅ DONE | 634→0 warnings (PR #18 merged). Plan: `refactoring/CQ-WARNINGS-PLAN.md`. |
| **TC** (Test Cleanup) | ✅ TC04, TC09 DONE (#36); TC06/TC07 live in `App.Tests/HotPath` (real Recycle Bin / real photos) | TS10 audit complete 2026-09-22. Plan: `archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md`. |
| **T89** (Fit Layout — GUI acceptance) | 🔄 GUI acceptance only (kept, Q-AR5) | T89.1-T89.2 (`10ff31f`) **merged to `master` via PR #15**; branch deleted. DF02 Fit tests skipped in `9f880d1`. STA/GUI acceptance still TODO. Plan: `archive/historical/T89-FIT-LAYOUT-PLAN.md`. |
| **OC** (Optimize/Clean) | 🔄 ~65% done | OC14 kept, re-scoped to "Undo gate location" (Q-AR5) — blocks ST08/09, OC15-18 (no longer WD, see ADR 0005). Plan: `archive/historical/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`. |
| **WD** (WPF Dialog) | ✅ WD01 unblocked (AR04/ADR 0005); WD02-06 closed 2026-09-23 (Q-AR5) | WD02 low-risk part covered by AR03c; WD03-06 had no known dialog bug to justify keeping open. |
| **IO** (I/O Durability) | 🔄 IO01 DECIDED (ADR 0007, 2026-09-24); IO03–IO05 TODO | Journal durability = user setting (Fast default / power-loss safe), session no-fsync, unreadable files skipped with a visible warning. IO02, IO06, IO07 closed. |
| **DT** (Docs Token Diet) | 🔄 Partial | DT00-03, DT08, DT09 DONE; Q-D1..Q-D4 decided. DT04-07 closed 2026-09-23 (Q-AR5, diminishing returns); DT10 kept. Plan: `archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`. |
| **D** (Perf Diagnosis) | ❌ Closed 2026-09-23 (Q-AR5) | Numbers were from the legacy `--perf-session` graph (no preload). Re-open any item from the AR02e production-graph baseline if a bottleneck shows. |

---

## AR Tasks — Architecture Review 2026-09-23

**Summary:** [`refactoring/ARCH-REVIEW-SUMMARY.md`](refactoring/ARCH-REVIEW-SUMMARY.md) · **Per-task plans:** `refactoring/arch-review/`

| ID | Name | Decision | Depends on | Real machine/GUI | Status |
|----|------|----------|------------|------------------|--------|
| AR00 | Review + plans + ADR 0005 draft + stale-status fixes | — | — | No | ✅ DONE (PR #23 merged, `bff22d9`) |
| AR01 | Ship TurboJpeg: explicit registration, native probe, Settings reflects availability, release gate | Q-AR1 = **A** (ship TurboJpeg) | — | 1 check | ✅ DONE (#28); headless check OK, Settings visual check by user |
| AR02a | `AppHost`, caches from `IAppPaths`, viewport provider, presentation/preload/move seams | Q-AR3 = **YES** | — | Fit check (with T89) | ✅ DONE (#27); Fit visual check by user (T89) |
| AR02b | Integration tests build MainWindow via `AppHost` (12 sites) | Q-AR3 = **YES** | AR02a | No | ✅ DONE (#30, landed via `fix/land-ar02bc-and-31`) |
| AR02c | `Benchmark.Cli` perf-session/ui-probe on production graph; report effective config | Q-AR3 = **YES** | AR02a | Run once | ✅ DONE (#29 + `--decoder`, landed via `fix/land-ar02bc-and-31`) |
| AR02e | Perf re-baseline on production graph | — | AR02c | **Yes** (user machine) | ✅ DONE 2026-09-23 — `refactoring/PERF-STATUS.md` |
| AR02d | Delete test ctors/`CreateTestViewModel`; public fields → read-only properties | Q-AR3 = **YES** | AR02b, AR02c | No | ✅ DONE (#35) |
| AR03 | SourceBytes policy (a), `Platform.Windows` without WPF (b), startup dialog via `IDialogService` (c) | — | — | No | ✅ DONE (PR #24 merged, `b453fc6`) |
| AR04 | UI-thread affinity: remove 47 `ConfigureAwait(false)` in App, arch test, catalog Debug guard | Q-AR2 = **YES** (ADR 0005 Accepted) | AR02c, AR02e | **Yes** (GUI + perf) | ✅ DONE (#37); perf gate passed (paired runs, `refactoring/PERF-STATUS.md`); GUI acceptance by user |
| AR06 | One release location, delete broken `outputs/release`, prune worktrees/leftovers | Q-AR4 = **YES** (CI path only) | — | No | ✅ DONE (#26) |
| AR07 | Fix 17 broken links, restore ADR evidence, link gate, docs-budget fix, group triage | Q-AR5 = **per §5 proposal table** | AR00 | No | ✅ DONE (#25) |

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
| TS05 | Deterministic, faster journal test (needs Q-S3) | P1 | ✅ DONE (#36, Q-S3 = recommendation) |
| TS06 | Honest hot-path tests (no PASS without assertion) | P1 | ✅ DONE (#36) |
| TS07 | Temp hygiene (`TempRoot`) | P1 | ✅ DONE (#36) |
| TS08 | Timing report in `verify-all.ps1` | P2 | closed 2026-09-23 (Q-AR5), plan: `archive/historical/TEST-SPEED-PLAN-2026-09-20.md` |
| TS09 | CI/local filter alignment | P2 | closed 2026-09-23 (Q-AR5) — CI filter already aligned, see `ci.yml` note; plan: `archive/historical/TEST-SPEED-PLAN-2026-09-20.md` |
| TS10 | Re-audit commits claiming TC01-TC11 | P1 | ✅ DONE — audit completed 2026-09-22 (see TS10 Audit Results below); kept open note removed (Q-AR5) |

---

## TC Tasks — Test Cleanup

**Plan:** [`archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md`](archive/historical/TEST-CLEANUP-PLAN-2026-09-20.md)
**Decisions finalized:** Q-T1 queue keypresses · Q-T2 read-count seam · Q-T3 real photos via env var · Q-T4 native Recycle Bin

> **TS10 Audit Results** (2026-09-22):
> - d5fc9c8 (TC01-TC03): ✓ Genuine scaffolding. PhotoFolderBuilder, ReadBudgetProbe, WarmNavigationReadBoundsTests.TC03 all exist with real assertions.
> - fe00f36 (TC08-TC10): ✓ TC08/TC10 real. InterleavedFileActionSequenceTests.TC08a/b/c exist with production-code tests. [Trait] categorization works.
> - 1c1f728 (TC11): ✓ CI gate real. verify-all.ps1 filter logic implemented, tested with -Stress/-Native/-Integration/-Slow/-All.
> - 34886ec (TC07): ✗ Real tests exist with assertions, but added to Integration.Tests at the time; later moved to App.Tests/HotPath (`440527f`). RealPhotosManualTests.cs has 2 real methods.
> - 7cab725 (TC06): ✗ Real tests exist with assertions, but added to Integration.Tests at the time; later moved to App.Tests/HotPath (`440527f`). NativeRecycleBinTests.cs has 2 real methods.
> **Resolved:** TC06/TC07 tests live in `tests/PhotoReview.App.Tests/HotPath/` (real Recycle Bin / real photos). TC01-TC05 scaffolding is solid.

| ID | Name | Status | Blocker | Notes |
|----|------|--------|---------|-------|
| TC01 | Fixture builder | ✅ EXISTS | — | PhotoFolderBuilder.cs: 103 lines, creates synthetic JPEG+PNG+corrupted+non-image files |
| TC02 | ReadBudgetProbe | ✅ EXISTS | — | ReadBudgetProbe.cs: 76 lines, wraps ReviewMetrics for I/O counting |
| TC03 | Disk-read invariants | ✅ EXISTS | TC01, TC02 | WarmNavigationReadBoundsTests.TC03: real assertions with ReadBudgetProbe |
| TC04 | Rapid Next (key-repeat) | ✅ DONE (#36) | TC01 | Real rapid-Next test in WarmNavigationReadBoundsTests |
| TC05 | Rapid Move/Delete | ✅ EXISTS | TC01, TC02 | WarmNavigationReadBoundsTests.TC05: tests rapid Next without await, real assertions |
| TC06 | Native Recycle Bin | ✅ EXISTS* | Q-T4 done | **App.Tests/HotPath/NativeRecycleBinTests.cs.** — needs real Recycle Bin. 2 real methods: DeleteMultiple/DeleteRapidly with 52 assertions |
| TC07 | Real photos manual | ✅ EXISTS* | Q-T3 done | **App.Tests/HotPath/RealPhotosManualTests.cs.** — needs real photos. 2 real methods: WarmNext/FileActions with ReadBudgetProbe assertions |
| TC08 | Replace G1 tests | ✅ EXISTS | TC05 | InterleavedFileActionSequenceTests.TC08a/b/c: production-code replacement tests (183 lines added) |
| TC09 | Flake audit | ✅ DONE | — | TS02 `FixturePerfTest` fixed (PR #32 merged): gate asserts count/composition/size only; cost budget → `Category=Slow` test on thread CPU time (≤10 s, ~1.5 s cold). Full suite 3× 0 failures. LargeJournal → bounded-work assertion (TS05, #36). |
| TC10 | Test categorization | ✅ EXISTS | — | [Trait("Category", ...)] added to 46+ test classes, filter works in verify-all.ps1 |
| TC11 | CI gate integration | ✅ EXISTS | TC05 | verify-all.ps1 gate filter logic working; CI workflow updated |


---

## T89 — Fit Layout (code on master, GUI acceptance open)

**Plan:** [`archive/historical/T89-FIT-LAYOUT-PLAN.md`](archive/historical/T89-FIT-LAYOUT-PLAN.md)
**Code:** commit `10ff31f` (T89.1-T89.2) — **merged to `master` via PR #15** (verified 2026-09-23: `git branch --contains 10ff31f` → master; branch no longer on origin).

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
| OC11 | Decode/RAM optimization | 🔄 PRs #40–#48 (perf night 2026-09-24) | See PERF-STATUS "Perf night"; #43 waits on zoom decision |
| OC12 | Clean code limits | TODO | Post-implementation cleanup |
| OC13 | Integration/validation | TODO | Final testing before publish |
| OC14 | Undo unification + gate | PARTIAL — kept, re-scoped 2026-09-23 (Q-AR5) to "Undo gate location" | Mutual exclusion done; semantics tests refactored — blocks ST08/09, OC15-18 (no longer blocks WD01, see ADR 0005/AR04) |
| OC15-OC18 | Clean-code wave | TODO | OC14 |

---

## WD Tasks — WPF Dialog Service

| ID | Name | Status |
|----|------|--------|
| WD01 | UI-thread audit | ✅ DONE via AR04 (#37) |
| WD02 | DRY refactor (low-risk) | closed 2026-09-23 (Q-AR5) — the only real case is covered by **AR03c** |
| WD03 | DI refactor | closed 2026-09-23 (Q-AR5) — no known dialog bug to justify keeping open |
| WD04-WD06 | Window lifecycle | closed 2026-09-23 (Q-AR5) — no known dialog bug to justify keeping open |

---

## IO Tasks — I/O Durability (Blocked on Contract)

| ID | Name | Status |
|----|------|--------|
| IO01 | Durability contract | ✅ DECIDED — ADR 0007 (2026-09-24): journal mode setting (Fast default / power-loss safe), session no-fsync, skip+warn unreadable files |
| IO02 | Benchmark baseline | ✅ done inline (2026-09-24, in ADR 0007): journal record P50 1.79 ms (durable) vs 0.36 ms; session write P50 3.75 ms |
| IO03 | Journal durability setting | TODO — Settings option (Fast default / Power-loss safe, off-UI-thread writes); tests per ADR 0007 |
| IO04 | Session write policy | TODO — session no fsync (atomic kept); corrupt/empty session = no session |
| IO05 | Unreadable files: skip + warn | TODO — enumeration skips with count + visible warning (strings via i18n catalogs) |
| IO06-IO07 | Async boundary; low-risk cleanup | closed 2026-09-23 (Q-AR5) |

---

## DT Tasks — Docs Token Diet (Partial)

**Plan:** [`archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`](archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md)

DT00-DT03, DT08, DT09 DONE; Q-D1..Q-D4 decided (see OPEN-DECISIONS). DT04-DT07 closed 2026-09-23 (Q-AR5) — diminishing returns; plan: `archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`. DT10 (final measurement) kept (Q-AR5). Known gap: `docs-budget.ps1` did not count `docs/INDEX.md` as T0 → fixed in AR07 §4.

---

## D Tasks — Perf Diagnosis (Mixed, Partially Blocked)

**Plan:** [`archive/historical/PERF-DIAGNOSIS-TASKS.md`](archive/historical/PERF-DIAGNOSIS-TASKS.md)

| ID | Name | Status | Blocker |
|----|------|--------|---------|
| D01 | AppLog script | closed 2026-09-23 (Q-AR5) | numbers were from the legacy `--perf-session` graph; plan: `archive/historical/PERF-DIAGNOSIS-TASKS.md` |
| D02 | File access count | closed 2026-09-23 (Q-AR5) | numbers were from the legacy `--perf-session` graph; plan: `archive/historical/PERF-DIAGNOSIS-TASKS.md` |
| D06 | Perf session run | closed 2026-09-23 (Q-AR5) | numbers were from the legacy `--perf-session` graph; plan: `archive/historical/PERF-DIAGNOSIS-PLAN.md` |
| D07 | Procmon analysis | closed 2026-09-23 (Q-AR5) | numbers were from the legacy `--perf-session` graph; plan: `archive/historical/PERF-DIAGNOSIS-PLAN.md` |
| D08 | ETW deep-dive | closed 2026-09-23 (Q-AR5) | numbers were from the legacy `--perf-session` graph; plan: `archive/historical/PERF-DIAGNOSIS-PLAN.md` |
| D09 | GC + memory | closed 2026-09-23 (Q-AR5) | numbers were from the legacy `--perf-session` graph; plan: `archive/historical/PERF-DIAGNOSIS-PLAN.md` |
| D12 | Approve optimizations | closed 2026-09-23 (Q-AR5) | numbers were from the legacy `--perf-session` graph; plan: `archive/historical/PERF-DIAGNOSIS-TASKS.md` |

**Re-open trigger:** any D item may be re-opened from the AR02e production-graph baseline if a real bottleneck shows up.

---

## CQ Summary — Code Quality / Warnings (COMPLETE)

✅ **DONE & MERGED (PR #18).** 634 → 0 warnings. All analyzer waves (Wave 1: CQ01-03, Wave 2: CQ04/05/08, Wave 3: CQ06/07) complete. See `refactoring/CQ-WARNINGS-PLAN.md` for details and real bugs fixed along the way.

---

## Dependencies & Critical Path

```
TC01 → TC02 → (TC03 ∥ TC04) → TC05 → TC08 → (TC06, TC07) → TC09-TC11
                                                (TS10 must re-audit first)

OC14 (Undo) → ST08 → ST09
           (WD closed)
           → OC15-OC18

AR00 → (AR01 ∥ AR03 ∥ AR06) → AR07
AR00 → AR02a → AR02b ∥ AR02c → AR02e (user machine) → AR02d
                               AR02e → AR04 (= WD01) → WD02 rest

IO01 (ADR 0007) → IO03 ∥ IO04 ∥ IO05

T89 GUI/STA acceptance (code already on master) ↔ AR02a step 3 (viewport)

DT02 → DT03 → (DT04 ∥ DT05) → DT06-DT10
```

---

## Notes

- **Completed work is archived**, not deleted: `docs/refactoring/archive/` holds resolved ST plans/tasks/decisions and the completed DF plan.
- **T89**: GUI/STA acceptance (user) before touching `ApplyFitViewAsync` further.
- **OC14** (Undo gate location) is the biggest remaining unblock: it gates ST08/09 and OC15-18.
