# Active Tasks — Consolidated Work Remaining (2026-09-20)

**Updated:** 2026-09-20  
**Master SHA:** 8bdd658 (after ST01 merged)  
**Status:** All ST tasks DONE; TC/OC/WD/IO/DT tasks pending

---

## Summary by Group

| Group | Task Count | Status | Notes |
|-------|-----------|--------|-------|
| **TC** (Test Cleanup) | 11 | TC00 ✅, TC01-TC11 🔄 | Ready for implementation; decisions Q-T1..Q-T4 finalized |
| **OC** (Optimize/Clean) | 13 | ~65% done | OC01-OC13; many blocks on UI-thread/native/GUI acceptance |
| **WD** (WPF Dialog) | 6 | All 🔄 | Blocked pending OC14 Undo semantics (ST06 DONE) |
| **IO** (I/O Durability) | 7 | All 🔄 | Blocked pending contract lock-down |
| **DT** (Docs Diet) | 10 | All 🔄 | Planning complete; not yet implemented |
| **T89** (GUI/Layout) | 1 | 🔄 IN PROGRESS | Awaiting GUI/STA acceptance before Fit changes |
| **D** (Perf Diagnosis) | 13 | D00-D07 mixed | D01-D02 BLOCKED; D08-D09 TODO |

---

## TC Tasks — Test Cleanup (Implementation Ready)

**Plan:** [`TEST-CLEANUP-PLAN-2026-09-20.md`](refactoring/TEST-CLEANUP-PLAN-2026-09-20.md)  
**Decisions finalized (2026-09-20):**
- Q-T1: Queue keypresses (UX priority) ✅
- Q-T2: Add read-count seam ✅
- Q-T3: Real photos via env var ✅
- Q-T4: Native Recycle Bin ✅

| ID | Name | Status | Blocker | Notes |
|----|------|--------|---------|-------|
| TC00 | Baseline + traits | ✅ DONE | — | 775/776 PASS; 1 flaky (100ms assertion) |
| TC01 | Fixture builder | 🔄 TODO | — | PhotoFolderBuilder for shared JPEG generation |
| TC02 | ReadBudgetProbe | 🔄 TODO | — | Source-read measurement seam (Q-T2) |
| TC03 | Disk-read invariants | 🔄 TODO | TC01, TC02 | Sequential navigation, cache hits, Move/Delete zero-reads |
| TC04 | Rapid Next (key-repeat) | 🔄 TODO | TC01 | Stress test, barrier-based (no `Task.Delay`) |
| TC05 | Rapid Move/Delete | 🔄 TODO | TC01, TC02 | Production path, replaces G1 tests, queue behavior (Q-T1) |
| TC06 | Native Recycle Bin | 🔄 TODO | Q-T4 | Real Recycle, self-cleanup via Undo; outside gate |
| TC07 | Real photos manual | 🔄 TODO | Q-T3 | Real folder via `PHOTOREVIEW_FIXTURE_DIR` env var |
| TC08 | Replace G1 tests | 🔄 TODO | TC05 | Delete old tests after mapping |
| TC09 | Flake audit | 🔄 TODO | — | Remove `Task.Delay`, add barriers; 30 reps on each file |
| TC10 | Merge OperationJournalTests | 🔄 TODO | — | Consolidate duplicate test files |
| TC11 | CI gate integration | 🔄 TODO | TC05 | `verify-all.ps1` + `-Stress`/`-Native`/`-Manual` flags |

**Known issue:** Flaky test `OperationJournalTests.LargeJournal_ReadCommittedMoves_IsBoundedAndFast` (100ms assert; 112-166ms in full parallel run). Fix in TC09.

---

## OC Tasks — Optimize/Clean (Partial, Interleaved)

**Plan:** [`OPTIMIZE-CLEAN-PLAN-2026-09-20.md`](refactoring/OPTIMIZE-CLEAN-PLAN-2026-09-20.md)

| ID | Name | Status | Blocker | Notes |
|----|------|--------|---------|-------|
| OC01 | Baseline regression fixtures | 🔄 TODO | — | UI-thread dialog boundary still TODO |
| OC06 | Journal/Undo/Recovery | ⚠️ PARTIAL | OC14 (Q-ST4) | Undo history ✅, recovery retry ✅; native restore identity TODO |
| OC07 | Display state + T89 | ⚠️ PARTIAL | T89 evidence | Display subset ✅; T89 GUI/STA acceptance TODO |
| OC09 | Benchmark semantics | ⚠️ PARTIAL | — | Profile propagation ✅; workload action/report TODO |
| OC11 | Decode/RAM optimization | 🔄 TODO | — | Blocked pending P95 measurements |
| OC12 | Clean code limits | 🔄 TODO | — | Post-implementation cleanup |
| OC13 | Integration/validation | 🔄 TODO | — | Final testing before publish |
| OC14 | Undo unification + gate | ⚠️ PARTIAL | ST08 DONE | Mutual exclusion ✅; semantics tests refactored (commit 8ce0baf) |
| OC15-OC18 | Clean-code wave | 🔄 TODO | OC14 | Post-OC14 implementation |

---

## WD Tasks — WPF Dialog Service (Blocked on OC14)

**Plan:** `OPTIMIZE-CLEAN-PLAN-2026-09-20.md` (WD01–WD06)  
**Blocker:** OC14 Ctrl+Z semantics finalization

| ID | Name | Status | Notes |
|----|------|--------|-------|
| WD01 | UI-thread audit | 🔄 TODO | Call graph + dispatcher contract |
| WD02 | DRY refactor (low-risk) | 🔄 TODO | Pattern consolidation |
| WD03 | DI refactor | 🔄 TODO | Requires WD01 proof |
| WD04-WD06 | Window lifecycle | 🔄 TODO | Requires WD01-WD03 proof |

---

## IO Tasks — I/O Durability (Blocked on Contract)

**Plan:** `OPTIMIZE-CLEAN-PLAN-2026-09-20.md` (IO01–IO07)  
**Blocker:** IO01/IO02 contract + benchmark baseline

| ID | Name | Status | Notes |
|----|------|--------|-------|
| IO01 | Durability contract | 🔄 TODO | Lock down completeness semantics |
| IO02 | Benchmark baseline | 🔄 TODO | Journal + FileAction + Recovery timing |
| IO03-IO04 | Async FileSystem | 🔄 TODO | Requires IO01/IO02 |
| IO05 | Permission reproduction | 🔄 TODO | Requires IO01/IO02 |
| IO06 | Buffer optimization | 🔄 TODO | Requires IO01/IO02 baseline |
| IO07 | Temp-name optimization | 🔄 TODO | Requires IO01/IO02 baseline |

---

## DT Tasks — Docs Token Diet (Planning Complete, Not Yet Implemented)

**Plan:** [`DOCS-TOKEN-DIET-PLAN-2026-09-20.md`](refactoring/DOCS-TOKEN-DIET-PLAN-2026-09-20.md)  
**Scope:** Reduce token load on cold-start context (docs ~564 KB → ~200 KB target)

| ID | Name | Status | Blocker | Notes |
|----|------|--------|---------|-------|
| DT00 | Baseline + budget | 🔄 TODO | — | Tokenizer measurement |
| DT01 | Entry points (INDEX, CLAUDE.md) | 🔄 TODO | Q-D1, Q-D3 | 4 KB digest on main paths |
| DT02 | Archive completed history | 🔄 TODO | Q-D2 | Move 335 KB (REFACTOR-TASKS, test-parity) |
| DT03 | Compress active plans | 🔄 TODO | — | DT02 must complete first |
| DT04 | Live docs + code map | 🔄 TODO | — | `architecture.md` + imports diagram |
| DT05 | Compress T89 plan | 🔄 TODO | T89 acceptance | Only after GUI evidence |
| DT06 | Code comment vetting | 🔄 TODO | — | Keep only WHY comments |
| DT07 | Boilerplate reduction | 🔄 TODO | — | Test setup common patterns |
| DT08 | Noise reduction | 🔄 TODO | Q-D4 | Obsolete branches/tags in git |
| DT09 | Prevent re-growth | 🔄 TODO | — | Automation + guidelines |
| DT10 | Re-measure + close | 🔄 TODO | All prior | Final report |

**Decision status:**
- Q-D1: Edit `AGENTS.md`? (pending)
- Q-D2: Archive location (`docs/archive/`)? (pending)
- Q-D3: `CLAUDE.md` required? (pending)
- Q-D4: Git cleanup allowed? (pending)

---

## T89 — GUI/Layout (IN PROGRESS, Awaiting Evidence)

**Plan:** [`T89-FIT-LAYOUT-PLAN.md`](refactoring/T89-FIT-LAYOUT-PLAN.md)  
**Status:** Math/wheel/pan merged (PR #9); STA/GUI acceptance pending

| Aspect | Status | Notes |
|--------|--------|-------|
| Wheel scroll | ✅ DONE | Math tested |
| Pan threshold | ✅ DONE | Short-circuit added |
| Fit initial | 🔄 TODO | STA layout assertion needed |
| Fit zoom | 🔄 TODO | Viewport + scrollbar edge cases |
| GUI acceptance | 🔄 TODO | Manual test on real images |

**Blocker:** Do not reduce `ApplyFitViewAsync` to single pass without GUI/STA evidence.

---

## D Tasks — Perf Diagnosis (Mixed, Partially Blocked)

**Plan:** [`PERF-DIAGNOSIS-TASKS.md`](refactoring/PERF-DIAGNOSIS-TASKS.md)

| ID | Name | Status | Blocker | Notes |
|----|------|--------|---------|-------|
| D01 | AppLog script | 🚫 BLOCKED | D06 data | Script ready; re-run after D06 |
| D02 | File access count | 🚫 BLOCKED | D06 data | Script ready; re-run after D06 |
| D06 | Perf session run | 🔄 TODO | — | Use `--perf-session` with Procmon |
| D07 | Procmon analysis | 🔄 TODO | D06 | Win32 API trace |
| D08 | ETW deep-dive | 🔄 TODO | D07 | Event Tracing for Windows |
| D09 | GC + memory | 🔄 TODO | D07 | .NET profiler |
| D12 | Approve optimizations | 🔄 TODO | D08-D09 | User sign-off before T82-T87 |

---

## Cleanup Recommendations

### 1. Immediate (Do Now)

- [ ] **Archive old plans:** Move REFACTOR-PLAN.md, REFACTOR-TASKS.md, PERF-DIAGNOSIS-*, test-parity.md to `docs/archive/`
- [ ] **Review APP-MECHANISMS-VI.md:** Is it core docs or historical? Keep or archive?
- [ ] **Clean up diagnosis/** and **results/** folders:** Move to archive if historical only

### 2. Next Phase (After TC01-TC05)

- [ ] **Implement TC01-TC11** (test hotpath baseline)
- [ ] **TC06/TC07** unlock after environment setup (real photos, Recycle Bin)
- [ ] **OC14-OC18** can run in parallel after OC14 Undo semantics proof

### 3. Before Publishing (DT Tasks)

- [ ] **Finalize DT decisions (Q-D1..Q-D4)**
- [ ] **Archive completed tasks** (REFACTOR-TASKS, PERF-DIAGNOSIS, etc.)
- [ ] **Create digest files** (REFACTOR-STATUS.md, PERF-STATUS.md ≤ 4 KB each)
- [ ] **Update INDEX** with live entry points only

---

## Dependencies & Critical Path

```
TC00 (done) ──→ TC01 ──→ TC02 ──→ (TC03 ∥ TC04) ──→ TC05 ──→ TC08
                                                       ↓
                                                    (TC06, TC07)
                                                       ↓
                                                    TC09-TC11

OC14 (Undo) ──→ WD01 ──→ (WD02 ∥ WD03) ──→ WD04-WD06
             ↓
             OC15-OC18

IO01-IO02 ──→ (IO03-IO07)

T89 (pending GUI evidence)

DT02 ──→ DT03 ──→ (DT04 ∥ DT05) ──→ DT06-DT10
```

---

## Notes

- **TC tasks** are high-priority: baseline and decision-ready for immediate implementation.
- **OC tasks** are partially done; several waiting on GUI/native evidence.
- **WD/IO** tasks blocked on prerequisite contracts (OC14, IO01).
- **DT tasks** are planning-complete; awaiting user decisions (Q-D1..Q-D4).
- **T89** must have GUI acceptance before any `ApplyFitViewAsync` changes.
