# Active Tasks — Consolidated Work Remaining

**Updated:** 2026-09-22
**Status:** ST, TS00-04, DF all DONE and merged to `master`. TC/OC/WD/IO/DT/T89(GUI)/D still open.

---

## Summary by Group

| Group | Status | Notes |
|-------|--------|-------|
| **ST** (Structure Optimize) | ✅ DONE (ST08/ST09 blocked) | ST01-ST07, ST10-ST12 merged. ST08/ST09 wait on OC14. See `refactoring/STRUCTURE-OPTIMIZE-STATUS.md`. |
| **TS** (Test Speed / gate reliability) | ✅ TS00-TS04 DONE, TS05-TS10 TODO | Gate no longer hangs, runs ~23s. Plan: `refactoring/TEST-SPEED-PLAN-2026-09-20.md`. |
| **DF** (Double-click → Fit) | ✅ DONE | PR #14 merged. Plan archived: `refactoring/archive/DBLCLICK-FIT-PLAN-2026-09-21.md`. |
| **TC** (Test Cleanup) | 🔄 TODO (unverified prior claims) | TS10 must re-audit before trusting any "done" status. Plan: `refactoring/TEST-CLEANUP-PLAN-2026-09-20.md`. |
| **T89** (Fit Layout — GUI acceptance) | 🔄 IN PROGRESS, not on `master` | T89.1-T89.2 committed on `feature/Fit-Layout-Status` only. STA/GUI acceptance still TODO. Plan: `refactoring/T89-FIT-LAYOUT-PLAN.md`. |
| **OC** (Optimize/Clean) | 🔄 ~65% done | OC14 (Undo unification) is the key blocker for ST08/09, WD, and OC15-18. Plan: `refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`. |
| **WD** (WPF Dialog) | 🔄 All TODO | Blocked on OC14. |
| **IO** (I/O Durability) | 🔄 All TODO | Blocked on IO01 contract lock-down. |
| **DT** (Docs Token Diet) | 🔄 All TODO | Planning complete; 4 decisions (Q-D1..Q-D4) pending. Plan: `archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`. |
| **D** (Perf Diagnosis) | 🔄 Mixed, partially blocked | D01/D02 wait on D06 data; D08/D09/D12 wait on D07. |

---

## TS Tasks — Test Speed and Gate Reliability

**Plan:** [`refactoring/TEST-SPEED-PLAN-2026-09-20.md`](refactoring/TEST-SPEED-PLAN-2026-09-20.md)

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

**Plan:** [`refactoring/TEST-CLEANUP-PLAN-2026-09-20.md`](refactoring/TEST-CLEANUP-PLAN-2026-09-20.md)
**Decisions finalized:** Q-T1 queue keypresses · Q-T2 read-count seam · Q-T3 real photos via env var · Q-T4 native Recycle Bin

> Prior commits (`d5fc9c8`, `fe00f36`, `1c1f728`, `34886ec`, `7cab725`) claimed TC01-TC11 done, but tests hang/fail/pass-without-asserting. Treat all rows below as TODO until TS10 re-audits.

| ID | Name | Status | Blocker |
|----|------|--------|---------|
| TC01 | Fixture builder | TODO | — |
| TC02 | ReadBudgetProbe | TODO | — |
| TC03 | Disk-read invariants | TODO | TC01, TC02 |
| TC04 | Rapid Next (key-repeat) | TODO | TC01 |
| TC05 | Rapid Move/Delete | TODO | TC01, TC02 |
| TC06 | Native Recycle Bin | TODO | Q-T4 done, task not started |
| TC07 | Real photos manual | TODO | Q-T3 done, task not started |
| TC08 | Replace G1 tests | TODO | TC05 |
| TC09 | Flake audit | TODO | — |
| TC10 | Merge OperationJournalTests | TODO | — |
| TC11 | CI gate integration | TODO | TC05 |

**Known issue:** Flaky test `OperationJournalTests.LargeJournal_ReadCommittedMoves_IsBoundedAndFast` (100ms assert; 112-166ms in parallel). Fix in TC09.

---

## T89 — Fit Layout (GUI acceptance, not yet on master)

**Plan:** [`refactoring/T89-FIT-LAYOUT-PLAN.md`](refactoring/T89-FIT-LAYOUT-PLAN.md)
**Branch:** `feature/Fit-Layout-Status` (commit `10ff31f`, T89.1-T89.2) — **not merged to `master`**.

| Aspect | Status |
|--------|--------|
| Wheel scroll | ✅ DONE (merged, PR #9) |
| Pan threshold | ✅ DONE (merged, PR #9) |
| Viewport convergence helpers | ✅ on feature branch only |
| Fit initial | TODO — STA layout assertion needed |
| Fit zoom | TODO — viewport + scrollbar edge cases |
| GUI acceptance | TODO — manual test on real images, then merge to `master` |

**Blocker:** do not reduce `ApplyFitViewAsync` to a single pass without GUI/STA evidence.

---

## OC Tasks — Optimize/Clean

**Plan:** [`refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md)

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

## WD Tasks — WPF Dialog Service (Blocked on OC14)

| ID | Name | Status |
|----|------|--------|
| WD01 | UI-thread audit | TODO |
| WD02 | DRY refactor (low-risk) | TODO |
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

## DT Tasks — Docs Token Diet (Planning Complete, Not Implemented)

**Plan:** [`archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md`](archive/future/DOCS-TOKEN-DIET-PLAN-2026-09-20.md)

10 tasks (DT00-DT10) all TODO. Blocked on 4 open decisions: Q-D1 (edit `AGENTS.md`?), Q-D2 (archive location — now `docs/archive/`, `docs/refactoring/archive/`), Q-D3 (`CLAUDE.md` required?), Q-D4 (git cleanup allowed?).

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

## Dependencies & Critical Path

```
TC01 → TC02 → (TC03 ∥ TC04) → TC05 → TC08 → (TC06, TC07) → TC09-TC11
                                                (TS10 must re-audit first)

OC14 (Undo) → ST08 → ST09
           → WD01 → (WD02 ∥ WD03) → WD04-WD06
           → OC15-OC18

IO01-IO02 → (IO03-IO07)

T89 (GUI/STA evidence, on feature/Fit-Layout-Status) → merge to master

DT02 → DT03 → (DT04 ∥ DT05) → DT06-DT10
```

---

## Notes

- **Completed work is archived**, not deleted: `docs/refactoring/archive/` holds resolved ST plans/tasks/decisions and the completed DF plan.
- **TC tasks** are high-priority but must wait on TS10 (re-audit) before any status is trusted.
- **T89** must have GUI/STA acceptance before merging `feature/Fit-Layout-Status` or touching `ApplyFitViewAsync` further.
- **OC14** is the single biggest unblock: it gates ST08/09, all of WD, and OC15-18.
