# Active Tasks

**Updated:** 2026-09-27 | **Base:** `master` `1de561c` (#134, tag `v2.0.137`) | **Open PRs:** none

Finished groups (AR, ST, OC, TS, TC, DF/T89, CQ, WD, IO, D, DT, L, reviews, perf night, UI feedback): one line each in
[`refactoring/HISTORY.md`](refactoring/HISTORY.md). Decisions: [`refactoring/OPEN-DECISIONS.md`](refactoring/OPEN-DECISIONS.md).
This file is the only place for open work (`task_on_progress.md` links here, do not copy it there).

## Open

| Item | State | Next step |
|---|---|---|
| **GUI checks (user)** | Pending since #128-#134: context-menu Zoom (Fit + 200/300/400 %, Zoom levels submenu with arrow), "Taken:"/"Modified:" labels (info line + title bar), info-overlay auto-hide (Q-R34), Zoom card in Settings (level, click-to-zoom, arrow step %, read-only shortcuts), sort modes (Q-R33), preload window setting (Q-R31). | User confirms or reports. |
| **Q-R29** (NAS follow-ups) | Open: (1) `ThumbnailCache.BuildKey` stats the file on the UI thread per cold navigation; (2) whole-folder preload has no I/O priority/bandwidth cap, so on a slow NAS link 8 workers can delay the next viewer decode. The 40 s first-image delay itself was fixed by AR16 (test `FolderLoadCoordinatorTests.SlowStorage`). | Awaiting user decision (see OPEN-DECISIONS). |
| **Flaky test** | `InfoOverlayFaultTests.SiblingSearchFault_*` under heavy CPU (race on the pending placeholder). | Fix if it recurs in CI. |
| **Caution** | `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it in isolation. | - |

## Closed on purpose (do not reopen without a new measurement)

- D-series perf diagnosis (legacy graph); re-derive from the AR02e/perf-night baselines in `PERF-STATUS.md` if a bottleneck shows.
- TS08/TS09, WD02-WD06, IO06-IO07, DT04-DT07 (Q-AR5, diminishing returns); OC12/OC13 (generic cleanup, no concrete item).
- AR10 live RAM budget (Q-AR6 a); Q-R9 Recycle Bin orphan sweep (declined: never touch the user's real bin).

## Guard rails

- Do not reduce `ApplyFitViewAsync` to a single pass without GUI/STA evidence (T89).
- Do not compare perf numbers across the AR02c boundary (legacy `--perf-session` graph vs production graph).
