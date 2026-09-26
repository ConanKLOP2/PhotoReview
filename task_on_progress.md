# Current Work — PhotoReview

**Updated:** 2026-09-26 | **Base:** `a261fc6` | **Open PRs:** #126, #127

## Now

- **Preload window (Q-R31, #125):** Settings ▸ Performance, ahead/behind (32/8); RAM floor follows; after restart.
- **NAS report:** 40 s to first image = pre-AR16 per-file open, fixed ≥v2.0.111 (test #122). Follow-ups: Q-R29.
- **Done 2026-09-26 (#94–#112 merged):** architecture review AR10–AR19 ([summary](docs/refactoring/ARCH-REVIEW-2026-09-26-SUMMARY.md); #106, AR16/Q-AR7 (c) background readability probe #107), benchmark speed-up #109 (gate −34 %, quick −24 %, in-app Quick check), stale Slow MainWindow tests #110, journal test-host crash #112, source audit F0–F4 closed. Details and numbers: [progress log](docs/archive/progress-log-2026-09.md#2026-09-26-late), `PERF-STATUS.md`.
- **GUI checks — user reported "GUI OK" (2026-09-26):** AR13 (pan + glide, wheel zoom at cursor, Ctrl+wheel navigation, click-to-zoom, Fit after resize/DPI change, skipped-files list), AR16 (locked file: first image at once, then the warning), Settings > Updates and Defaults, close during a folder scan, Recycle Bin undo, decoder fallbacks with damaged EXIF/ICC, T89 Fit.
- **Measured 2026-09-26 (PERF-STATUS):** natural sort / snapshot validator confirmed on an idle PC (validator ×2.36, sort ×1.59, no allocations); Q-R17 on F4 fills the whole folder (~10 GB) but first-visual latency is ~2.5–3× higher in steady state → **Q-R26 = B** (user): keep Q-R17, find the cause. Round 2 (idle PC): cause = whole-folder preload pass running every image's cache lookup on the UI thread per navigation; fixed in `perf/q-r26-preload-kick-off-ui` (first-visual back to window-mode level, whole folder still cached).
- **Q-R27 = D (#116):** each running operation holds a named kernel event (`WindowsLiveOperationRegistry`) from Prepared to its outcome; startup reconcile skips live operations, so a second process no longer reports them Failed.
- **Q-R28 = A (i18n export, branch `feat/i18n-export-all`):** Settings > Export strings to translate now writes every key (missing first + `_missing` list, then current translations, notes for all); `plural: none` languages no longer get unused `.one` keys. Settings > Reload translations now shows a message: "no problems" or the list of problems (skipped files, entries falling back to English; first 15, rest counted). Tests + 9 mutations pass; GUI look of the message still to be checked by the user.
- **Tests no longer overwrite the user's real config.json (user, urgent, 2026-09-26, `fix/isolate-test-config`):** `DataRootFixture` now also sets `PHOTOREVIEW_ISOLATE_CONFIG=1`, so `AppPaths.FromEnvironment()` puts `config.json` under the temp data root (before, e.g. `SettingsJournalDurabilityTests` reset `%LOCALAPPDATA%\PhotoReview\config.json` to defaults on every test run). Full suite verified: real config hash unchanged.
- **Releases are manual (user, 2026-09-26):** CI only tags `v2.0.N`; no automatic draft. Actions > Release > Run workflow (tag input) publishes directly (`draft` box optional).
- **UI feedback Q-R30 — merged (#119, #123, release build 2.0.127):** arrow-key pan, dark title bar + scrollbars, toolbar auto-hide, context menu (Open folder/Settings/Click zoom level), title-bar fields, optional Modified date EXIF field, info font size, click-zoom key `2`, glide smoothing `Predict`. **GUI check pending.**
- **Sort modes (Q-R33):** `Default`, `NameAscending`, `NameDescending` ignore Explorer order; `Name` still follows it.
- **Flaky:** `InfoOverlayFaultTests.SiblingSearchFault_*` under heavy CPU (race on the pending placeholder).
- **Caution:** `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it in isolation.
- **Decision log:** handoff and decisions live on `master` only (no `develop` since #104). Keep this file short: move finished detail to the progress log.

## Status by Group

Group status: see [`docs/ACTIVE-TASKS.md`](docs/ACTIVE-TASKS.md) (single source; do not copy it here).

## Critical Process Rules

- No direct `master` commits: branch → PR → review
- No `ApplyFitViewAsync` single-pass without T89 evidence
- No broad `Dispatcher.Invoke` / `GetRequiredService` (ADR 0005 rule, AR04 DONE)
- No silent `IgnoreInaccessible`; durability changes only per ADR 0007 (journal mode setting, session no-fsync)
- No OS SendInput/SetForegroundWindow in test harnesses
- Never run code that deletes/sweeps the user's real Recycle Bin (tests use fakes)
- Don't compare perf numbers across the AR02c boundary (legacy vs production graph)

## Key Links

[ACTIVE-TASKS](docs/ACTIVE-TASKS.md) · [Open decisions](docs/refactoring/OPEN-DECISIONS.md) · [INDEX](docs/INDEX.md) · [History](docs/archive/progress-log-2026-09.md)

## Quick Checks

```powershell
tools/docs-budget.ps1 -Check
dotnet build PhotoReview.slnx -c Release
dotnet test PhotoReview.slnx -c Release --filter "Category!=Manual&Category!=Native&Category!=Slow"
tools/verify-all.ps1
```
