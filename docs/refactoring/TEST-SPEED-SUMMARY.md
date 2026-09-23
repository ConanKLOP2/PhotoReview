# TS — Test Speed & Gate Reliability (Summary)

**Status:** TS00–TS04 DONE (4 commits merged); gate now runs ~23s without hang. TS05–TS10 TODO.

Full plan: [`../archive/historical/TEST-SPEED-PLAN-2026-09-20.md`](../archive/historical/TEST-SPEED-PLAN-2026-09-20.md)

## Target

Gate (`dotnet test PhotoReview.slnx`) runs ≤60s (local) / ≤3 min (CI), never hangs silently, detects hangs within 120s.

## Key Issues Found (Fixed)

| ID | Issue | Fix |
|---|---|---|
| **F1** | `WarmNavigationReadBoundsTests` hung (no Dispatcher pump) | ✅ TS01: use `StaTestHost` |
| **F2** | Fixture generation: 16–23s per process, 389 MB, no cleanup | ✅ TS02: cheaper fixture, cleanup |
| **F3** | No hang timeout in `verify-all.ps1` / CI | ✅ TS00: add `--blame-hang --blame-hang-timeout 120s` |
| **F5–F8** | Mojibake in tests; 4 tests with only `TODO` comments; 1 test asserts Q-T1 queue behavior (production drops actions) | ✅ TS01, TS04: fix encoding, remove stub tests, clarify behavior |
| **F7** | Vietnamese text double-encoded in 17 Core tests + 5 production files | ✅ TS04: restore UTF-8 encoding |

## Completed (TS00–TS04)

| ID | Task | Status | Evidence |
|---|---|---|---|
| **TS00** | Hang guard: `--blame-hang` in verify-all.ps1 & CI | ✅ DONE | Gate never hangs >120s; fails with test name |
| **TS01** | Fix App hanging test + Dispatcher pump | ✅ DONE | Use `StaTestHost.WaitForAsync`; 41s full run |
| **TS02** | Cheaper fixture: deterministic pattern images | ✅ DONE | Fixture ≲5s; cleanup called; temp dirs removed |
| **TS03** | Restore `StaTestHost.WaitForAsync` polling (revert spin loop) | ✅ DONE | Integration tests no longer timeout |
| **TS04** | Fix UTF-8 encoding (mojibake in tests + source docs) | ✅ DONE | Vietnamese strings display correctly |

## Remaining (TS05–TS10)

| ID | Task | Status | Blocker |
|---|---|---|---|
| **TS05** | Deterministic journal test (remove `Task.Delay` timespan) | 🔄 TODO | Q-S3: decide if barrier or deterministic schedule |
| **TS06** | Honest hot-path tests (every test has real assertion) | 🔄 TODO | TS01 pass rate confirmed |
| **TS07** | Temp hygiene: `TempRoot` cleanup fixture | 🔄 TODO | TS02 infrastructure in place |
| **TS08** | Timing report in verify-all (show per-project wall time) | 🔄 TODO | Informational; enables TS09 |
| **TS09** | Align CI filters with local (same Category logic) | 🔄 TODO | TS08 report enables |
| **TS10** | **Re-audit TC01–TC11 "done" claims before trusting TC status** | 🔄 TODO | **Blocks all TC work** |

## Current Gate Status (after TS00–TS04)

✅ No hang  
✅ ~23s wall time (local)  
✅ All tests PASS  
✅ No stale temp files  

## See Also

- **TC:** Requires TS10 audit before starting
- **DT07:** Test boilerplate consolidation (follow-on)
