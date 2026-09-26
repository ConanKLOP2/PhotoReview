# Current Work — PhotoReview

**Updated:** 2026-09-26 (late night) | **Base:** master `880370b` (#94–#112 merged) | **Open PRs:** none

## Now

- **Benchmark speed-up — merged (#109):** `run-matrix.ps1` one warm-up per (fixture, mode, condition) per batch (`-WarmupEveryCell` = old behaviour), `-Profile quick` uses new `s2-next-slow-quick` (40 keys); `BenchmarkWindow` "Quick check" button (fast-sequential, 10 iterations), image limit (default 64, 0 = all), non-blocking cache teardown. F4: gate 393 → 260 s (−34 %), quick 209 → 158 s (−24 %), in-app quick check 2.6 s vs default 8.2 s. **User GUI check:** Quick check button, image-limit box, close the window mid-run (no crash, no leftover `%TEMP%\PhotoReview-Benchmark-Cache\*`).
- **Architecture review 2026-09-26 — merged (#106):** [summary](docs/refactoring/ARCH-REVIEW-2026-09-26-SUMMARY.md). Four agent branches merged without conflicts: AR11 (static `AppSettings` persistence removed, Settings round-trip test, Core `File.*` boundary test), AR12 (+AR17a, AR18a: `.gitignore` native rule, Release warnings-as-errors, GC note, TurboJpeg "experimental" label), AR15 (dead overloads, one atomic cache writer, `SourceBytesCache` reads on the calling thread), AR13 (+AR19a, AR14a: `PointerInputController`, `FitViewController` — Fit loop moved verbatim, still ≤ 3 passes; `MainWindow.xaml.cs` 715 → 404 lines; `ShowSkippedFiles` via `IDialogService`). Decisions Q-AR6/8/9/10 = recommended options (user said "run everything"); Q-AR7 = (c) (user, "chọn c"), see below.
- **Measured 2026-09-26 (PERF-STATUS):** AR15c not worse with the source-bytes cache on (new CLI flag `--source-bytes-cache`); AR16 probe ≈ 114 ms per folder open on F4 (≈ 68 % of the old 166 ms first visual) → **Q-AR7 decided (c)** by the user; merged in #107 (below).
- **AR16 / Q-AR7 (c) — merged (#107):** folder open lists without the per-file open probe and presents the first image; the probe runs in the background, is applied after the Explorer order settles, removes unreadable files (current kept, or advanced like Delete) and still reports them (ADR 0007). GUI check for the user: folder with a locked file → first image at once, then the "skipped" warning.
- **Still to do:** **user GUI check for AR13**: pan + glide, wheel zoom at cursor, Ctrl+wheel navigation, click-to-zoom, Fit after resize and DPI change, skipped-files list opens.
- **User (GUI / real machine), still open from the overnight review:** Settings > General > Updates, Defaults button, close during a folder scan, Recycle Bin undo on a non-English Windows, decoder fallbacks with damaged EXIF/ICC; perf runs for natural sort / snapshot validator and Q-R17 on F4.
- **Test-host stability — merged (#112):** the one-off `JournalConcurrencyTests` crash was an `IOException` (append retry budget exhausted on a loaded machine) escaping a raw `Thread`, which kills the whole test host; thread failures are now captured and rethrown on the test thread, and the test only asserts acknowledged commits. Product code already handles this (action reports a journal error; startup reconcile catches). FA-01 itself is covered deterministically by `JournalReconcileRaceTests`.
- **Slow MainWindow tests fixed (#110):** the 5 failures on master were stale tests, not app bugs — actions were installed by mutating `_settings` (the `ShortcutRouter` only rebuilds on `SettingsStore.Changed`; now via `SettingsStore.Save`), the fake Explorer provider was registered as an instance (DI never disposes those; now a factory), and INV-5 still expected no Undo for a late Move (APP-03/Q-R25 B registers it). New test: a late *failed* Move is not restored into the new folder. 14/14 pass ×3.
- **Source audit 2026-09-26:** all five findings closed — F2 by design (#101), F3 (#101, `b547ce8`), F0/F1/F4 (#103); verdicts in [the audit](docs/refactoring/REVIEW-2026-09-26-FULL-SOURCE-AUDIT.md#follow-up-claude-2026-09-26).
- **Caution:** `PerformanceHarnessWarmupTests.DefaultReportIsNotWrittenIntoThePhotoFolder` writes then deletes `%TEMP%\PhotoReview-Benchmark\photoreview-performance-report.json`; do not rerun it in isolation.
- **Decision log:** handoff and decisions live on `master` only (no `develop` since #104).

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
