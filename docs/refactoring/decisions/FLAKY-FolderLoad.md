---
id: FLAKY-FolderLoad
order: 18
summary: |-
  CI flake in the folder-switch race test was a fixture bug (session sweep listing took the scan-block hook), not a production race; fake fixed (#209).
---

# FLAKY-FolderLoad — root cause of the `FolderLoadCoordinatorTests` race flake (2026-09-27)

## Symptom

`FolderLoadCoordinatorTests.LoadAsync_SecondFolderWhileFirstScanBlocked_FirstLoadIsDroppedSilently` failed on
GitHub Actions ("Run App tests (HotPath)") on #193, #197, #198 and #201 with
`Assert.Equal() Failure: Expected: 1 Actual: 2` on `_sink.CatalogReadyCount` (9-30 ms runs). It never failed in
isolation. #193 (commit 5b88d1b0) reproduced it locally (2-core affinity + repeated full `App.Tests` runs) and traced
the slow load's generation guard passing *before* the fast load's prologue had run.

## Root cause: a test-fixture bug, not a production race

- R14 (#195, commit 436656e2) made the `SessionStore` constructor start a background stale-temp sweep:
  `StartupSweepTask = Task.Run(() => SweepStaleTempFiles(...))`, which lists the sessions folder with
  `IFileSystem.EnumerateFiles`.
- The test fixture builds its `SessionStore` over the test's `FakeFileSystem` in the class constructor. That fake fired
  its `OnEnumerateFiles` hook from **both** `EnumerateFiles` and `EnumerateFilesWithStat` (the folder scan).
- The race tests install a hook that blocks the **first** listing (`calls == 1`) and signal `scanEntered`. When the
  thread pool was slow to start the sweep (few cores, the rest of the suite running in parallel), the sweep's listing
  came after the hook was installed and became call #1: it signalled `scanEntered` and blocked itself. The real slow
  folder scan was call #2 and ran unblocked, so the slow load completed (and was applied) before the test even
  started the second load; the second load was then applied too -> `CatalogReadyCount == 2`.
- This matches #193's trace exactly: the slow load's guard saw its own generation as current because the fast load
  had genuinely not started yet. `FolderLoadCoordinator.LoadAsync`, `GenerationClock` and the cancellation handling
  are correct; the flakes started the day R14 merged.

Same latent flaw in the sibling tests using the hook: `LoadAsync_FolderGenerationBumpedDuringScan_AppliesNothing`,
`LoadAsync_RapidSwitches_OnlyTheLastIsApplied`, `Dispose_DuringBlockedScan_CancelsLoad_AndNoSinkUpdateFollows`.

## Fix

`FakeFileSystem.EnumerateFiles` no longer fires `OnEnumerateFiles`; the hook is documented as the folder scan's
listing only (`EnumerateFilesWithStat`, the only listing `FolderLoadCoordinator` uses). No production change.
The blocking `ManualResetEventSlim.Wait` in the hooks was not the cause and is kept (it blocks one pool thread for
the duration of the test only; the scan runs inside `Task.Run`, so it must block synchronously).

A deterministic fixture guard test (`FakeFileSystem_SessionSweepListing_DoesNotFireTheScanHook`) performs the
sweep's listing after installing the hook and asserts the hook is not fired (and that the scan listing still fires it).

## Second flake found while stress-testing: `Probe_DoneButNotYetApplied_ThenDisposed_IsDropped`

Under 2-core affinity this test failed 3 of ~90 class runs (10 s timeout on "the probe completed and queued its apply
step", and the assert on its raw thread crashed the test host). Cause, again in the test: `LoadAsync` starts the
readability probe and only in its `finally` calls `ApplyReadabilityProbeAsync`, which `await`s the probe. When the
"UI" thread is preempted between the two long enough for the pool to finish the (3-file) probe, that `await`
completes synchronously inside `LoadAsync`, nothing is ever posted to the test's queue context and the wait times out.
Production is fine either way (the inline apply runs on the UI thread and still checks the load token). Fix: the test
holds the probe (`HoldProbeOf`) until `LoadAsync` has returned, so the apply step is always posted; the body's
exception is captured and asserted on the test thread instead of crashing the host.

## Evidence

All local runs on this dev box with `start /affinity 3` (2 cores) while other sessions kept the CPU near 100 %:

- Before: full `App.Tests` project, 1 failure of this test in 9 runs (plus 5 CI failures on 2026-09-27 and #193's
  30-50 % local hit rate).
- After the fake fix: 6 full `App.Tests` runs, 0 failures of the race tests (one unrelated wall-clock failure of
  `PerfTraceTests.ListenerDoesNotBlockUnderBurstAndAccountsForEveryEvent`, 7.3 s, in a run that took 9 min).
- After both fixes: 200 runs of the whole `FolderLoadCoordinatorTests` class (two loops of 100 in parallel), 200/200.
- Mutation checks: firing the hook from `EnumerateFiles` again fails the guard test; removing the coordinator's
  post-scan cancellation/generation guards fails the three race tests; removing the load-token gate in
  `ApplyReadabilityProbeAsync` fails the Probe test — all deterministically.

## Other known flakes (not fixed here)

- `InfoOverlayFaultTests.SiblingSearchFault_*`: the only CI failure (run 36238159761, 2026-09-26, `Assert.Contains`
  on the placeholder, test line 32) predates commit a4bc1ba6, which gated the fake finder with a
  `ManualResetEventSlim` so the fault continuation cannot overwrite the placeholder before it is asserted. No CI
  failure since; no remaining race found. Considered fixed by a4bc1ba6.
- `CompositionRootTests.MainWindowClosed_DisposesProductionPreloadScheduler`: no CI failure in the last 40 failed
  runs; it failed once locally in a solution-level gate run while writing this fix (the detailed message was not
  captured; 0 failures in 12 further App-only / solution runs). Most likely cause: its check-then-create of the
  process-wide WPF `Application` (`if (Current is null) new Application()`) is not atomic against
  `DarkScrollBarRenderingTests`, a parallel collection doing the same thing — the TOCTOU that test already documents
  having seen in CI ("Cannot create more than one Application instance"). Applied the same tolerant creation (catch
  `InvalidOperationException` when another `Application` now exists) to the three STA tests in `CompositionRootTests`.
  Unconfirmed by a captured message: if it recurs, capture the `threadException` text.
- `ImagePresenterTests.PresentAsync_WhenSourceChangesDuringDecode_ShowsLocalizedImageError` (seen once locally under
  load): uses `FileToucher`, a real background thread rewriting the file's timestamp; it only works if that thread
  runs during the decode window. On a starved 2-core box the toucher can be descheduled for the whole window, the
  decode sees no change and no error is shown. A deterministic version needs a stat seam (fake `IFileSystem` whose
  stat changes on every call) instead of a real toucher thread — left as a follow-up.
