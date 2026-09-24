# Current Work — PhotoReview

**Updated:** 2026-09-24 | **Base:** master (`b8fe687`, #48) | **Branch:** none open

## Latest: `LoadingMode.Original` fix — MERGED (#48 `b8fe687`, 2026-09-24)

- `PreviewStateContext.IsOriginalLoadingMode` was never assigned since T46d (`9a5f7b8`), so Settings → "Original" still produced downscaled preview keys/decodes. Wired in `App.ConfigureServices`; composition test `ConfigureServices_PreviewStateContext_IsOriginalLoadingMode_FollowsSettings`.
- RAM: memory keys and disk hash include `IsOriginal`, so a mode switch never serves stale images; the 16 GB LRU evicts by `EstimatedBytes` (~170 originals at ~96 MB). `MainViewModel.ShowSettings` clears caches only on *backend* change — a mode change cancels preload + re-presents, old-mode entries age out via LRU (intended).
- Gate on master `b8fe687`: build Release 0 warnings; tests 0 failed (933 passed, 6 skipped).

## Architecture review AR00–AR07 DONE (2026-09-24)

- Merged: #24 AR03 · #25 AR07 · #26 AR06 · #27 AR02a · #28 AR01 · #34 (AR02b/c + #31 decode-width fix) · #35 AR02d · #36 TS05–07/TC04/TC09 · #37 AR04 (this branch). Decisions: `docs/refactoring/OPEN-DECISIONS.md`.
- Perf: AR02e baseline + AR04 gate (passed, interleaved runs) in `docs/refactoring/PERF-STATUS.md`. #31 fixed full-size Preview decode (since T46d): S3 burst shown 27–47 → 145–175 /200, peak WS 16.9 → 7.0 GB. TurboJpeg slower than WicDirect on the real set — keep WicDirect.
- Left for user (visual): Fit first-frame (T89), Settings TurboJPEG item greyed without dll, AR04 GUI acceptance (hold →, delete while navigating).
- **Next:** OC14 (Undo gate location → MainViewModel, unblocked by AR02d/AR04) → ST08/09, OC15–18. IO01 needs a user decision (durability contract). DT10 final measurement. Open: S3 decoder fallbacks 14 → 85 (PNG counted), RAM-hit P50 +1.5 ms.

## Previous: nav hot-path perf pass — DONE (#20 `94c5aeb..20300b4`, fix #21 `9161c35`)

Details: `docs/archive/progress-log-2026-09.md`. Deferred: App `ConfigureAwait(false)` → AR04; RAM budget / disk-cache value / lazy EXIF → tune from AR02e numbers; `GetOriginalDimensionsAsync` key reuse stays reverted.

## Status by Group

| Group | Status | Notes |
|-------|--------|-------|
| **AR** | ✅ AR00–AR07 all DONE | GUI acceptance (T89, AR04) by user. |
| ST | ✅ (ST08/09 wait OC14) | ST06 public fields replaced by AR02d (#35). |
| TS | ✅ TS00-07, TS10 | TS08/09 closed (Q-AR5). |
| DF, CQ | ✅ Done | |
| **T89** | 🔄 GUI acceptance only (kept, Q-AR5) | Code merged (#15). DF02 Fit tests skipped in `9f880d1`. AR02a touches Fit viewport — verify together. |
| **TC** | ✅ TC01-TC11 | TC04, TC09 done (#36); TC06/07 kept in Integration.Tests (documented, Q-AR5). |
| **OC** | 🔄 ~65% | OC14 kept, re-scoped to "Undo gate location" (Q-AR5) — blocks ST08/09, OC15-18; no longer blocks WD (Q-AR2=yes). |
| **WD** | ✅ WD01 done (AR04, #37) | WD02-06 closed 2026-09-23 (Q-AR5, no known dialog bug). |
| **IO** | 🔄 IO01 only (kept) | IO02-07 closed 2026-09-23 (Q-AR5, speculative). |
| **D** | ❌ Closed (Q-AR5) | Legacy `--perf-session` numbers; re-open from AR02e baseline if needed. |
| **DT** | 🔄 | DT00-03, 08, 09 done; DT04-07 closed (Q-AR5); DT10 kept. |

## Critical Process Rules

- ❌ No direct `master` commits: branch → PR → review
- ❌ No `ApplyFitViewAsync` single-pass without T89 evidence
- ❌ No broad `Dispatcher.Invoke` / `GetRequiredService` before WD01 (→ replaced by ADR 0005 rule once AR04 is DONE)
- ❌ No journal durability reduction / `IgnoreInaccessible` before IO01/02
- ❌ No OS SendInput/SetForegroundWindow in test harnesses
- ❌ Do not compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [AR summary](docs/refactoring/ARCH-REVIEW-SUMMARY.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [Structure status](docs/refactoring/STRUCTURE-OPTIMIZE-STATUS.md) · [T89 summary](docs/refactoring/T89-FIT-SUMMARY.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual"
tools/verify-all.ps1
```
