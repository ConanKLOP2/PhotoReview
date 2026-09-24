# TS — Test Speed & Gate Reliability (Summary)

**Status:** TS00–TS07, TS10 DONE (TS05-TS07 via #36, TS10 audit via #32); gate now runs ~23s without hang. TS08/TS09 closed 2026-09-23 (Q-AR5).

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

## TS05–TS10 (done / closed)

| ID | Task | Status | Evidence |
|---|---|---|---|
| **TS05** | Deterministic journal test (remove `Task.Delay` timespan) | ✅ DONE | #36 |
| **TS06** | Honest hot-path tests (every test has real assertion) | ✅ DONE | #36 |
| **TS07** | Temp hygiene: `TempRoot` cleanup fixture | ✅ DONE | #36 |
| **TS08** | Timing report in verify-all (show per-project wall time) | ❌ Closed 2026-09-23 (Q-AR5) | — |
| **TS09** | Align CI filters with local (same Category logic) | ❌ Closed 2026-09-23 (Q-AR5) | CI filter already aligned |
| **TS10** | **Re-audit TC01–TC11 "done" claims before trusting TC status** | ✅ DONE | Audit completed 2026-09-22; #32 |

## Current Gate Status (after TS00–TS04)

✅ No hang  
✅ ~23s wall time (local)  
✅ All tests PASS  
✅ No stale temp files  

## See Also

- **TC:** Requires TS10 audit before starting
- **DT07:** Test boilerplate consolidation (follow-on)
