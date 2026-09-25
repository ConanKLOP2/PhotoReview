# Current Work — PhotoReview

**Updated:** 2026-09-25 | **Base:** master `2d52f6d` (#76 merged) | **Branch:** `docs/decisions-q-r7-r11`

## Now: round-2 decisions implemented, PRs #77 #78 #79 open

- **User:** review + merge #77 (Q-R7), #78 (Q-R10, needs a GUI check: multi-select open from Explorer), #79 (Q-R8); the real Recycle Bin was found without `$R` files on 2026-09-24 after a subagent mutation run (cause unproven); visual checks: Recovery, Settings journal option, VI wording, zoom, Fit first frame (T89), AR04.
- **Plan / evidence:** [plan](docs/refactoring/REVIEW-2026-09-25-PLAN.md) · [round 2 reports](docs/archive/evidence/review-2026-09-25/round2/).

## Review round 3 (2026-09-25, branch `review/2026-09-25-round3`, PR pending)

- Base master `633ea3c` (#77-#80 merged). Fixed: permanent-delete dialog re-checks folder/busy/catalog after the nested loop; alpha-opacity scan moved to the persist worker; forward client (empty reply = NoInstance, >16 paths truncated); log shutdown after forward server; preload survives corrupt files; sibling nav with trailing `\`; malformed language file no longer throws; corrupt `config.json` is not overwritten if its backup fails; `EstimatedBytes` uses real bpp; compare key opens compare when a pair exists; F11 restores the previous window state.
- Gate: build 0 warnings, all non-Manual/Native/Slow tests green.
- Decisions Q-R12..Q-R16 answered 2026-09-25 (all per recommendation, R16 = do all) and implemented on the same branch: `ForwardOutcome.Unknown`; duplicate cleanup up-front refusal on no-Recycle-Bin drives; Settings save/open errors shown; strict `ShortcutKeyName` + IME/Tab/Esc in the capture box; Space/Enter kept for buttons and compare panes; failed zoom decode not retried; journal skips unknown Type/State; persist queue 8; bounded `PreloadScheduler.Dispose` (3 s); ZoomOut from Fit < MinZoom is a no-op; `\photos` destination rejected; per-path source-bytes evict. Single-step undo kept. Left as is: `TryGetFileInfo` catch-all (File.Exists hides access errors). Flaky once, passed twice on rerun: App test 'Log file rotates to a .1 backup...'.

- **Round 4 (same branch):** regressions found in round 3 fixed (Space/Enter no longer exempt on toolbar buttons; SettingsStore never overwrites a corrupt config after a failed backup; forward client empty reply = Unknown; Dispose cleanup deferred after drain timeout; persist queue 16); `FileLog.Flush` race (flaky 10 MB AppLog rotation test removed, FileLogTests covers rotation); vacuous/weak tests fixed (Join assert, IntegrationSmokeTests, ThumbnailCache locked file); tooling: benchmark CLI no longer fills the real Recycle Bin (opt in with `PHOTOREVIEW_BENCH_REAL_RECYCLE_BIN=1`), `summary.json` NaN crash, burst-scenario match, generator XML-doc line terminators.
- **Round 4 deferred:** benchmark fidelity (profile windows/full-folder not applied in `BenchmarkImageExecutor`, FirstFrame warm after 1st sample, parallel-read vs cold-read), file-action workload race handling, `verify-all -TestReport` project timing regex, source-text tests (SourcePresenceTests placement/monitor/association, MainWindow gate-logic text check), `DrainAsync` fixed-window negative asserts in MainWindowBehaviorTests, SourceBytesCache test does not prove per-path (needs an in-flight read hook), GUI benchmark window still uses the real Recycle Bin.

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
