# Review 2026-09-25 rounds 3 and 4

Branch `review/2026-09-25-round3` (base master `633ea3c`). Decisions: [OPEN-DECISIONS](OPEN-DECISIONS.md) Q-R12..Q-R16.

## Round 3 / 4 notes (2026-09-25, branch `review/2026-09-25-round3`, PR pending)

- Base master `633ea3c` (#77-#80 merged). Fixed: permanent-delete dialog re-checks folder/busy/catalog after the nested loop; alpha-opacity scan moved to the persist worker; forward client (empty reply = NoInstance, >16 paths truncated); log shutdown after forward server; preload survives corrupt files; sibling nav with trailing `\`; malformed language file no longer throws; corrupt `config.json` is not overwritten if its backup fails; `EstimatedBytes` uses real bpp; compare key opens compare when a pair exists; F11 restores the previous window state.
- Gate: build 0 warnings, all non-Manual/Native/Slow tests green.
- Decisions Q-R12..Q-R16 answered 2026-09-25 (all per recommendation, R16 = do all) and implemented on the same branch: `ForwardOutcome.Unknown`; duplicate cleanup up-front refusal on no-Recycle-Bin drives; Settings save/open errors shown; strict `ShortcutKeyName` + IME/Tab/Esc in the capture box; Space/Enter kept for buttons and compare panes; failed zoom decode not retried; journal skips unknown Type/State; persist queue 8; bounded `PreloadScheduler.Dispose` (3 s); ZoomOut from Fit < MinZoom is a no-op; `\photos` destination rejected; per-path source-bytes evict. Single-step undo kept. Left as is: `TryGetFileInfo` catch-all (File.Exists hides access errors). Flaky once, passed twice on rerun: App test 'Log file rotates to a .1 backup...'.

- **Round 4 (same branch):** regressions found in round 3 fixed (Space/Enter no longer exempt on toolbar buttons; SettingsStore never overwrites a corrupt config after a failed backup; forward client empty reply = Unknown; Dispose cleanup deferred after drain timeout; persist queue 16); `FileLog.Flush` race (flaky 10 MB AppLog rotation test removed, FileLogTests covers rotation); vacuous/weak tests fixed (Join assert, IntegrationSmokeTests, ThumbnailCache locked file); tooling: benchmark CLI no longer fills the real Recycle Bin (opt in with `PHOTOREVIEW_BENCH_REAL_RECYCLE_BIN=1`), `summary.json` NaN crash, burst-scenario match, generator XML-doc line terminators.
- **Round 4 deferred:** benchmark fidelity (profile windows/full-folder not applied in `BenchmarkImageExecutor`, FirstFrame warm after 1st sample, parallel-read vs cold-read), file-action workload race handling, `verify-all -TestReport` project timing regex, source-text tests (SourcePresenceTests placement/monitor/association, MainWindow gate-logic text check), `DrainAsync` fixed-window negative asserts in MainWindowBehaviorTests, SourceBytesCache test does not prove per-path (needs an in-flight read hook), GUI benchmark window still uses the real Recycle Bin.


## Round 5 (review of the round-4 diff)
- Buttons exempt from Space/Enter again, but a click hands focus back to the window (``ReturnFocusAfterButtonClick``) so shortcuts keep working after a toolbar click. Not GUI-verified.
- Forward client: silent hang-up = ``NoInstance`` (owner read nothing); ``Unknown`` only for a reply that misses the client deadline. Test drives a real silent server.
- ``SettingsStore``: guard set only for the default config path, reset on each ``Load``, also on the locked-file branch; suppressed saves log a warning.
- ``summary.json``: NaN written as ``null`` (``NaNAsNullDoubleConverter``).
- Left: ``BenchmarkWindow`` (GUI) still uses the real Recycle Bin; CLI vs GUI delete timings are not comparable. Flaky under full-suite load, passes alone: Core test 'Dispose returns within its bound while a write is in flight and skips the last write'.

## Round 6 (perf + robustness audits)
Fixed: turbojpeg.dll loads only from the app folder; Undo of a Recycle refuses to restore over a newer file at the original path; journal append after a torn last line starts a new line; duplicate finder throws instead of hashing everything to "" when no hash service.
Not fixed, need measurement or a decision (nothing here was measured; estimates are from reading code):
- **Whole-folder preload estimate** (`RamBudgetPolicy.JpegExpansionFactor = 10`, used by `PreloadScheduler`) is ~8x pessimistic for viewport-sized previews, so folders above ~1.6 GB source fall back to the 32-ahead/8-behind window and most of the 16 GB target stays unused. Q-R17 = D (2026-09-25, branch `perf/q-r17-preload-estimate`): box bound, then measured mean x 1.25 after 8 previews at the current box. Same branch fixes a scheduler crash: two preload calls completing synchronously (cached or superseded) returned the runtime's shared `Task<bool>` and the duplicate `running` key ended the preload loop. Perf run on the user machine still due (AR02e boundary).
- Whole-folder warm scan runs synchronously on the UI thread per navigation (5-50 ms for 10k-50k files); `EntriesSnapshot()` + `SourceSizeTracker` re-sum per navigation; compare-pair index rebuilt on the UI thread after each file action (15-40 ms at 10k); 4 stats per RAM-hit navigation; thumbnail lookup duplicating the preview decode; 3+ redundant stats per preloaded image; source-bytes cache blocks the viewer thread on a pool read (option off by default).
- Recycle on a fixed drive whose bin is disabled or too small is a silent permanent delete (needs IFileOperation or registry `BitBucket\Volume` check).
- No pixel-count / file-size / scan-count cap in TurboJpeg and WicDirect decoders (huge or progressive-bomb JPEGs).
- A Move whose post-move size check fails is reported as failed although the file moved (catalog restore points at a missing path).
- Recycle-restore poll is only ~500 ms; a slow restore reports failure.
