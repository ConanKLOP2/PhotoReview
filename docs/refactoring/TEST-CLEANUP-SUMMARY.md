# TC — Test Cleanup (Summary)

**Status:** Baseline (TC00) not started; TC01–TC11 blocked on TS10 re-audit. Decisions Q-T1..Q-T4 finalized.

Full plan: [`TEST-CLEANUP-PLAN-2026-09-20.md`](TEST-CLEANUP-PLAN-2026-09-20.md)

## Objective

Build a **production-path test suite** protecting hot paths: read disk minimally, Next continuously, Move/Delete in sequence with real image sizes/formats. Replace mock-based tests with real-code tests where safe.

## Key Findings (Evidence)

| ID | Issue | Impact |
|---|---|---|
| **G1** | InterleavedFileActionSequenceTests runs in constructor on mock lists, not `FileActionService`/`MainViewModel` | False confidence; can't catch production regressions |
| **G2** | Only 9 single-action VM tests; no test for continuous Next×N or Delete×N sequences | Concurrent rejection behavior (Q-T1) unverified |
| **G3** | Test images are 1×1 PNG; real fixtures (4000×3000+) in Imaging.Tests only, not reusable | No window for decode-during-move race; no RAM/preload pressure |
| **G4** | Only single-decode assertions; no test for "warm Next = 0 source reads" or "full folder each file read once" | OC11 acceptance missing |
| **G5** | Source reads bypass `IFileSystem` in several places (`PreviewImageService`, `SourceBytesCache`, `FileHashService`) | Blind spots in source-read accounting |
| **G6** | Delete tests only use `FakeRecycleBin`; real Recycle Bin acceptance missing (OC06 TODO) | Shell behavior (delay, multiple versions, restore) untested |
| **G7** | ~50 instances of `Task.Delay`/timespan assertions scattered across tests | Flake risk; race tests rely on timing, not barrier |
| **G8** | `RealWorldPhotosManualTest` returns early without `Assert.Skip` when PHOTOREVIEW_FIXTURE_DIR missing | Silent PASS when no fixture |

## Task Groups

### P0 — Hot-Path Safety (in progress after TS10)

| ID | Task | Acceptance | Blocker |
|---|---|---|---|
| **TC00** | Baseline: run full suite, trait all tests, measure flake | PASS baseline; HotPath trait working | TS10 audit |
| **TC01** | Shared real-image fixture builder (TestSupport.Windows) | Builder creates 50 images ≲2s; no WPF in Core.Tests | — |
| **TC02** | Source-read probe (`ReadBudgetProbe`) | All source reads countable; probe detects new reads | Q-T2 seam (optional) |
| **TC03** | Production MainViewModel + FileActionService tests (Next×N, Move×N, Delete×N) | Continuous actions work; 1 source read per warm file | TC01, TC02 |
| **TC04** | OperationJournal concurrency (append Committed under concurrent Move/Delete) | No dropped operations; journal durability intact | TC02 |
| **TC05** | Concurrent action rejection (IsBusy gate, dropped action tracking) | Q-T1 behavior: action dropped, files unharmed, count tracked | TC03 |
| **TC06** | Catalog consistency under action sequence | State ≡ expectations after each step; no stale entries | TC03 |
| **TC07** | Real user photos (PHOTOREVIEW_FIXTURE_DIR) | Next/Move/Delete on real images; UNDO to clean up | Q-T3 provided |
| **TC08** | Checkpoint migration: map old tests to new (G4 assertion checks) | Parity table shows 1→1 coverage; delete old after new PASS | TC03–TC06 |
| **TC09** | Native Recycle Bin (C01 acceptance from OC06) | Restore correct version under path/size/timestamp ambiguity | Q-T4 approved; OC06 done |
| **TC10** | Test boilerplate reduction (gist from DT07) | Consolidate `Fake*` helpers; reuse `TestImages` | DT07 |
| **TC11** | Gate update: add `HotPath` filter to default run | Full test PASS; HotPath ≲ 30s; stress/manual separate | TC00+ |

## Decisions

| Q | Question | Decision |
|---|---|---|
| **Q-T1** | Queue actions or drop during busy? | **Drop** (current). TC05 locks this behavior via test + metric. |
| **Q-T2** | Seam for source-read probe? | **Yes** (small seam). TC02 adds hook; no behavior change. |
| **Q-T3** | Real photo fixture path? | **User-provided** via env var `PHOTOREVIEW_FIXTURE_DIR`. TC07 blocks until provided. |
| **Q-T4** | Live Recycle Bin test? | **Yes** (temp files only, with Undo cleanup). TC09. |

## See Also

- **OC06:** Native Recycle identity acceptance
- **OC11:** Warm-navigation source-read gate (G4 acceptance)
- **DT07:** Test boilerplate consolidation (TC10 follow-up)
- **TS10:** Re-audit prior test claims before trusting TC status
