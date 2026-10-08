# Static review pass 1 (2026-10-08): commits bd381f11..347f79cd

Overnight multi-area static review (App, Core, Imaging, Platform.Windows + tools, tests), read-only: no build, no tests, no running app. Each finding was challenged by an independent
verifier that tried to refute it by reading the code. 13 raw findings, 6 confirmed (all LOW), 7 refuted. Dynamic issues (performance, real UI, multi-monitor behaviour) are not covered.

## Confirmed (all LOW)

| # | Where | Finding | Status |
|---|---|---|---|
| 1 | `tools/coverage.ps1:82` | `Remove-Item -Recurse -Force` on `<OutDir>\raw` without an ownership marker: a user-supplied `-OutDir` that already has a `raw` folder loses it on every run | fix in PR `fix/tools-safety-review` |
| 2 | `tools/diag/fullscreen-capture.ps1:222` | the final cleanup deletes every `*.png` in the user-given `-Out` folder, not only its own frames | same PR |
| 3 | `tools/diag/tune-rank.ps1:260-275` | the A/A split mask uses an Int32 `1 -shl i`: wraps with more than 32 ok runs in a group (only the noise estimate is affected) | same PR |
| 4 | `tools/diag/make-subset-fixture.ps1:196` | the alias registration regex replaces one line only: a multi-line alias entry in `fixtures.local.json` becomes invalid JSON | same PR |
| 5 | `src/PhotoReview.App/Coordinators/FullscreenWindowPlacer.cs:108` | `Exit()` re-applies the saved rect to a Normal-state window even if the monitor or work area changed while in fullscreen (dock unplugged) | open, product code, not changed |
| 6 | `tests/PhotoReview.Integration.Tests/InstanceScopeGapTests.cs:105` | weak negative oracle: only `NotEqual(Delivered)` after a 500 ms wait | open, test only |

## Refuted (kept for the record)

`FullscreenWindowPlacer.Enter` style-before-WPF-property order (intended, bits already match); `fullscreen-capture.ps1` config restore ordering (app already exited on both failure paths);
unbounded `ready.Wait()` in `PerfDispatcherHooksTests` (a thread exception fails the host fast, same pattern elsewhere); machine-dependent soak thresholds (cache pinned, thresholds scale with measured capacity);
`TuneScriptTests` process spawns (small, only the dry-run test spawns); `RecycleBinPlatformGapTests` platform state (fakes for deletes and shell; real parts only read HKCU/%TEMP%);
`*ForTests` members in `PreloadScheduler` (style only; counter bounded to 8 resumes per navigation).

Next pass: run again when `master` gains `src/`, `tools/` or `tests/` commits.
