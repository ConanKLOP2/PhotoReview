# Review 2026-09-25 — Findings summary and wave plan

**Created:** 2026-09-24 on master `cbd24b8` (v2.0.71). **Status:** plan only, no fix started.
**Full verified table (60 active + 5 dropped):** [`../archive/evidence/review-2026-09-25-findings.md`](../evidence/review-2026-09-25-findings.md) · **Raw reviewer reports:** [`../archive/evidence/review-2026-09-25/`](../evidence/review-2026-09-25/)

How to use this file: pick the next wave that is **Ready** in §3, read its section in §5 only, follow the numbered steps, then run §4. When a wave's PR is merged, change its row in §3 to `✅ #PR` and add one line to `task_on_progress.md`.

## 1. Executive summary

A code + docs review of `src/`, `tests/`, `tools/`, CI and `docs/` ran on 2026-09-24/25 (four reports). Every High and Critical finding and 18 of the 24 findings rated Medium were checked against master by opening the cited line.

| Severity | Reported | After verification |
|---|---|---|
| Critical | 1 | 0 |
| High | 6 | **4** — IMG-01, CORE-01, CORE-06, DOC-01 |
| Medium | 24 | **19** |
| Low | 30 | **37** |
| Dropped / merged / handled elsewhere | — | 5 (CORE-04 withdrawn, DOC-06→CORE-01, DOC-07→#72, TEST-08 dup, HYG-09 not reproduced) |

Changes from verification: CORE-06 **raised** to High (Retry does file I/O and journal fsync on the UI thread). Lowered: CORE-02, TEST-01, DOC-02/03, IMG-02/04, APP-03, TEST-02. TEST-02's premise was wrong: `Integration.Tests` does run in CI, and only 6 tests are excluded. Four new findings: TEST-10, TOOL-04, IMG-11 and DOC-14.

**Top 10 risks, in plain words**

1. **IMG-01:** a transparent PNG looks right the first time. After it is saved to the disk cache, it comes back with a black background every time, until the user clears the cache.
2. **CORE-06:** the "Retry" button in the Recovery window copies or moves the file on the UI thread. A big file on another drive freezes the app. In "Power-loss safe" mode, the journal flush also runs on the UI thread.
3. **CORE-01:** Undo (Ctrl+Z) in Core resumes on the UI thread, which breaks ADR 0005. No test or analyzer catches this in Core, Imaging or Platform, so it can come back unnoticed.
4. **CORE-03:** an action's destination folder can be `..\..\anywhere`. A typo or a damaged `config.json` can move photos far outside the folder being reviewed.
5. **DOC-01/02/10:** status docs still say that merged PRs are "open" and that finished tasks are "blocked". The same status is kept in five places, so it keeps going stale and misleads the next agent.
6. **TEST-10:** every local test run puts real files into the user's Recycle Bin (benchmark "delete" test). Together with TEST-03 (killed runs leave orphans), this is why the bin holds about 2,650 test items.
7. **CORE-02:** closing the app or switching folders can block the UI while a session write finishes. There is no time limit on that wait.
8. **CORE-05:** Undo-from-Recycle-Bin leaves COM objects for every other item in the bin. With a big bin this adds pressure on Explorer and memory.
9. **IMG-03:** background preload can start up to 8 more large decodes after memory has already passed the 80 % safety line.
10. **APP-01/02:** screen readers read the Action Profiles, Batch Review and Diagnostics windows, and most of Settings, only as "button" or "edit".

## 2. Overlap with work already open (do not duplicate)

| Open work | Covers | Effect on this plan |
|---|---|---|
| PR #72 `docs/dt10` | DOC-07 (T0 trim); rewrites `AGENTS.md`, `task_on_progress.md` status table, `docs/INDEX.md` table, `OPEN-DECISIONS.md`, `STRUCTURE-OPTIMIZE-STATUS.md`, `TEST-CLEANUP-SUMMARY.md`, `OPTIMIZE-CLEAN-SUMMARY.md`, `T89-FIT-SUMMARY.md`, DT row of `ACTIVE-TASKS.md` | Wave 3 starts **after #72 merges**. Any AGENTS.md edit (TOOL-03, TEST-07) goes to Wave 3. |
| PR #73 `refactor/st08-oc18` | ST08/ST09 done (+1 test), OC15 done, OC16 n/a, OC17 already done, OC18 code done (GUI check under T89); touches `MainWindow.xaml.cs`, `MainWindowHelpers.cs` | DOC-02/09 status text is written from #73's final state in Wave 3. APP-04 (`MainWindow.xaml.cs`) waits for #73. |
| Branch `i18n/l12-vi-copy` (L12) | Vietnamese copy polish: `Core/Localization/Languages/vi.json`, `docs/TRANSLATING.md` | Any wave that adds catalog keys (W1c, W4 if needed) waits for L12, or appends keys at the end of `en.json`/`vi.json` and rebases. |

This PR only adds the plan, the evidence files, one `docs/INDEX.md` line and a minimal fix to the "Now" section of `task_on_progress.md` (DOC-01, partial).

## 3. Wave overview

Each row is one PR based on `master`, at most about one day of work.

| Wave | Branch | Findings | Effort | Must wait for | Parallel with | State |
|---|---|---|---|---|---|---|
| **1a** Core/Platform safety | `fix/review-w1a-core-safety` | CORE-06, CORE-01, CORE-02, CORE-05 | ~1 d | Q-R5 (default is fine) | 1b, 2b, 3 | In progress on `review/2026-09-25-integration` |
| **1b** Imaging alpha + preload | `fix/review-w1b-imaging-alpha` | IMG-01, IMG-05, IMG-03 | ~½ d | Q-R1 | 1a, 2a, 2b, 3 | In progress on `review/2026-09-25-integration` (Q-R1 = a) |
| **1c** Action destination policy | `fix/review-w1c-action-destination` | CORE-03 | ~½ d | Q-R2, L12 merged | 1a, 1b, 2b | Ready (Q-R2 = a; L12 merged #74) |
| **2a** CI + architecture rules | `test/review-w2a-ci-rules` | CA2007 rule (CORE-01/DOC-06), TEST-09, TEST-01, TEST-10, TEST-02, TOOL-01, TOOL-04, HYG-08 | ~1 d | **1a merged** (CA2007 needs UndoService fixed), Q-R3 | 1b, 1c, 3 | After 1a |
| **2b** Deterministic tests + coverage | `test/review-w2b-determinism` | TEST-04, TEST-05, TEST-06, TEST-03 | ~1 d | — | 1a, 1b, 1c, 3 | In progress on `review/2026-09-25-integration` |
| **3** Docs single source of truth | `docs/review-w3-status-adrs` | DOC-01 (rest), DOC-02, DOC-03, DOC-09, DOC-10, DOC-13, DOC-04, DOC-05 (ADR 0008), DOC-14, DOC-08, DOC-12, TOOL-03, TEST-07 (AGENTS part) | ~1 d | **#72 and #73 merged**; 2a merged for TOOL-03/TEST-07 text | all code waves | Done on `review/w3` (AGENTS.md filter line left for 2a) |
| **4** Accessibility + theme tokens | `feat/review-w4-a11y-theme` | APP-01, APP-02, APP-03 | ~1 d | 1c (shares `ActionProfilesWindow`), L12 if new keys, Q-R6 | 1a, 1b, 2a, 2b, 3 | After 1c |
| **5a** Tooling/repo hygiene | `chore/review-w5a-hygiene` | HYG-01/02/03/05/06/07/10, HYG-04, TOOL-02, APP-04 | ~½ d | Q-R4, #73 (APP-04) | 5b, 5c | Later |
| **5b** Imaging nits/perf | `perf/review-w5b-imaging` | IMG-02, IMG-04, IMG-07, IMG-08, IMG-09, IMG-10, IMG-11 | ~1 d | 1b merged (same files) | 5a, 5c | Later |
| **5c** Core nits | `refactor/review-w5c-core` | CORE-07, CORE-08, CORE-09, CORE-10, CORE-11, CORE-12 | ~1 d | 1a and 1c merged | 5a, 5b | Later |
| Backlog | — | IMG-06 (split big Imaging classes, L), DOC-11 (RELEASING.md) | — | 5b / none | — | Not scheduled |

## 4. Standard verification (run in every wave)

```powershell
dotnet build PhotoReview.slnx -c Release            # must end with "0 Warning(s)  0 Error(s)"
dotnet test PhotoReview.slnx -c Release --no-build --filter "Category!=Manual&Category!=Native&Category!=Slow&Category!=Stress"
powershell -NoProfile -ExecutionPolicy Bypass -File tools/i18n-check.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-doc-links.ps1          # 0 broken
git commit ... ; powershell -NoProfile -ExecutionPolicy Bypass -File tools/docs-budget.ps1 -Check   # reads HEAD, so commit first
grep -rc $'\xc3\xa1\xc2\|\xc3\xb0\xc2' <every file you touched that contains Vietnamese>  # must print 0 (mojibake guard)
```

Rules that apply to every wave:
- Edit files that contain Vietnamese (catalogs, ADR 0007, `architecture.md`, `APP-MECHANISMS-VI.md`) only with the Edit tool, never with sed or perl.
- Each new test must **fail under the listed mutation**. Apply the mutation locally, watch the test fail, revert, and write "mutation checked" in the PR body.
- Waves that touch the UI need a real-machine check by the agent (see memory "run real-machine checks yourself"). Use fixture F4 `C:\Xiuren\[[WALLPAPER]`.
- Rollback for any wave is `git revert <merge-sha>`. None of the waves changes a persisted format, so reverting is always safe unless the wave says otherwise.

## 5. Waves in detail

### Wave 1a — Core/Platform data safety
**PR title:** `fix(core): off-UI Recovery retry, Undo ConfigureAwait, bounded session shutdown, Recycle Bin COM release`
**Files:** `src/PhotoReview.Core/FileActions/RecoveryRetryService.cs`, `UndoService.cs`; `src/PhotoReview.Core/Session/SessionWriter.cs`; `src/PhotoReview.Platform.Windows/WindowsRecycleBin.cs`; `src/PhotoReview.App/RecoveryWindow.xaml.cs`, `Services/WpfDialogService.cs:75-79`; tests `tests/PhotoReview.Core.Tests/FileActions/{RecoveryRetryServiceTests,UndoServiceTests}.cs`, `Session/SessionWriterTests.cs`, `tests/PhotoReview.Integration.Tests/RecoveryWindowTests.cs`.

Steps:
1. **CORE-06:** add `public Task<RecoveryRetryResult> RetryMoveOrCopyAsync(JournalEntry failed, CancellationToken ct = default)`.
   - Keep the cheap validation (FileExists, stat) synchronous at the top.
   - Run everything from `_journal.Append(prepared)` to the end (mutation + verify + Committed/Failed append) inside `await Task.Run(() => …, ct).ConfigureAwait(false)`.
   - Remove the synchronous `RetryMoveOrCopy`, or make it `[Obsolete]` and internal to tests. Do not leave two public paths.
2. Change the retry delegate type from `Func<JournalEntry, RecoveryRetryResult>` to `Func<JournalEntry, Task<RecoveryRetryResult>>` in:
   - the `RecoveryWindow` constructor (`RecoveryWindow.xaml.cs:47,53`);
   - `WpfDialogService.cs:79`;
   - the test at `RecoveryWindowTests.cs:55`.
3. Make `Retry_Click` `async void`:
   - Set `RetryButton.IsEnabled = false` and the window cursor to Wait.
   - `try { result = await _retry(entry); } catch …` (no `ConfigureAwait`, per ADR 0005 App rule).
   - Restore the button in `finally` when the window is still open. The existing message boxes stay unchanged.
4. **CORE-01:** in `UndoService.cs`, add `.ConfigureAwait(false)` at `:185`, `:215` and `:227`.
5. **CORE-02** (`SessionWriter`):
   - Add `private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2)` (Q-R5).
   - Add a `bool bounded` parameter to `WriteBatch`: when true, use `if (!_writeLock.Wait(ShutdownWait)) { _log?.Error("Session write skipped at shutdown: writer busy", null); return; }`.
   - `Dispose()` sets `_disposed`, calls the bounded flush, then `_writeLock.Dispose()`, wrapped so a double `Dispose` is a no-op.
   - Folder-change `Flush()` keeps the unbounded wait: correctness there needs the write to be visible to `Load`.
6. **CORE-05** (`WindowsRecycleBin.TryRestore`):
   - In the enumeration loop, track `var kept = false;`, set it when the item is added to `candidates`, and call `Release(item)` in a `finally` when `!kept`.
   - In the verb loop, hold `var verbs = item.Verbs();`, release every non-matching `verb`, and release `verbs` in `finally`.

Tests to add (name — what it asserts — mutation that must make it fail):
- `RecoveryRetryServiceTests.RetryMoveOrCopyAsync_RunsMutationOffCallerThread`: a fake `IFileSystem.Copy` records `Thread.CurrentThread.ManagedThreadId`, and the call is made from a thread with a single-threaded `SynchronizationContext`. Asserts the recorded id is not the caller's id. **Mutation:** call `Copy` directly (no `Task.Run`), and the test fails.
- `RecoveryRetryServiceTests.RetryMoveOrCopyAsync_KeepsExistingVerdicts`: port the existing sync cases (source changed, destination exists, verify failed) to the async method, with the same asserts.
- `UndoServiceTests.UndoLastAsync_DoesNotResumeOnCapturedContext`: install a `CountingSynchronizationContext` (counts `Post`, forwards to the thread pool), run an Undo of a Move and a Recycle, and assert `Posts == 0`. **Mutation:** remove any of the three `ConfigureAwait(false)` calls, and `Posts` becomes 1 or more.
- `SessionWriterTests.Dispose_ReturnsWithinBound_WhenWriteInFlight`: a fake file system blocks `WriteAllTextAtomic` on a `ManualResetEventSlim`. Start a debounced write, wait until it is blocked, then call `Dispose()` on another task. Assert it completes within 5 s (event-based, not a delay), then release the event. **Mutation:** use an unbounded `Wait()`, and the test fails at 5 s. Use the injected `delay` seam. No wall-clock sleeps.
- `RecoveryWindowTests.Retry_DoesNotBlockDispatcher` (Integration, STA): the retry delegate returns a `TaskCompletionSource` task. After the click, assert that the dispatcher still processes a posted `DispatcherOperation` before the task completes. **Mutation:** use `.Result` in `Retry_Click`, and the test fails (its timeout is the `StaTestHost` guard).
- CORE-05 has no unit test, because a COM RCW count cannot be observed. It stays covered by `NativeRecycleBinTests` (`Category=Native`), which must still pass locally. Run it once: `--filter "FullyQualifiedName~NativeRecycleBinTests"`.

**Acceptance:**
- §4 is green.
- The Native Recycle Bin tests pass.
- Manual check: retrying a 1 GB cross-drive copy from the Recovery window leaves the window responsive (it can be moved), and the result dialog shows as before.

**Rollback:** revert. There is no data-format change.

**Parallel:** there is no file overlap with 1b, 2b or 3. 2a must wait, because its CA2007 rule would fail the build until `UndoService` is fixed.

### Wave 1b — Imaging: alpha-safe disk cache, tighter preload headroom
**PR title:** `fix(imaging): never persist alpha previews as JPEG; re-check preload memory headroom per candidate`
**Files:** `src/PhotoReview.Imaging/Caching/PreviewImageService.cs:465-467`, `Caching/PreviewCacheFile.cs`, `Preload/PreloadScheduler.cs:296-316`; tests `tests/PhotoReview.Imaging.Tests/PreviewImageServiceTests.cs`, `Caching/PreviewCacheFileTests.cs`, `PreloadSafetyTests.cs`; a transparent PNG fixture from `TestImages` (add `TransparentPng` if none exists: 64×64 with a half-transparent square).

Steps (Q-R1 option **a**, recommended):
1. Add `internal static bool HasAlpha(BitmapSource bmp)` in `PreviewCacheFile`:
   - Return true when `bmp.Format` is one of `Bgra32, Pbgra32, Rgba64, Prgba64, Rgba128Float, Prgba128Float`.
   - Also return true for an indexed format (`Indexed1/2/4/8`) whose `bmp.Palette?.Colors` contains a color with `A < 255`.
2. In `PreviewImageService.DecodeAndCache`, extend the persist condition with `&& !PreviewCacheFile.HasAlpha(bmp)`. Add a one-line comment linking IMG-01/Q-R1.
3. Guard it in `PreviewCacheFile.WriteAtomicallyAsync` too: `if (HasAlpha(bitmap)) throw new ArgumentException(...)`. That way a future caller cannot reintroduce the bug. Leave the header byte 7 contract unchanged (keep the v5 format, so no cache wipe is needed).
4. Existing wrong cache entries: do nothing automatic. Previews already corrupted on disk stay until the cache is cleared or pruned. Put this in the PR body: "users who saw black PNG backgrounds: Settings → Clear cache once". **Option (b)** instead bumps the format to v6 (`.pv6`), which invalidates all old entries automatically at the cost of one re-decode of everything. Choose it only if the user prefers automatic cleanup (Q-R1).
5. **IMG-03** (`PreloadScheduler`):
   - Replace the `examinedSinceYield == 0 &&` gate with a cached probe: `if (!HasPreloadHeadroomCached())`, where the cached helper stores `(bool value, long tick)` and re-runs `HasPreloadHeadroom()` only when more than 50 ms have passed or a decode was queued since the last check.
   - Keep the existing log and perf-event emission when the result is false.

Tests:
- `PreviewImageServiceTests.TransparentPng_IsNotPersistedToDiskCache`: decode `TransparentPng` downscaled through the real service with a temp disk cache, `ClearCache()` the RAM tier (not the disk), and fetch again. Assert the result's `Format` still has alpha, and that no cache file was written for that key. **Mutation:** remove the `!HasAlpha` gate, and the test fails (the result is `Bgr32`).
- `PreviewImageServiceTests.OpaquePng_IsStillPersisted`: the same flow with an opaque PNG. Assert a cache file exists. This guards against over-blocking: **mutation** `HasAlpha => true` makes it fail.
- `PreviewCacheFileTests.WriteAtomically_RejectsAlphaBitmap`: an `ArgumentException` for `Pbgra32`.
- `PreloadSafetyTests.HeadroomLost_MidBatch_StopsNewStarts`: a fake `IMemoryProbe` flips to "no headroom" after the first start, with `workers: 8`. Assert that at most 1 more decode starts, using `GatedTarget`'s start events, not delays. **Mutation:** restore the per-batch check, and up to 7 more starts happen.

**Acceptance:**
- §4 is green.
- On F4 (62 PNGs), open the folder, view a transparent PNG, restart the app and view it again: the background is still transparent (checkerboard or window background, not black).
- The perf smoke `tools/benchmark-folder.ps1` on F4 regresses by no more than 5 % in the P50 of image switching.

**Rollback:** revert (no format change under option a).

**Parallel:** 1a, 2a, 2b and 3 are disjoint. Wave 5b edits the same files, so run it after this wave.

### Wave 1c — Action destination policy (needs Q-R2, L12 merged)
**PR title:** `fix(actions): validate action destinations at save and execute time`
**Files:** `src/PhotoReview.Core/FileActions/FileActionService.cs:84-93`, a new `src/PhotoReview.Core/FileActions/ActionDestinationPolicy.cs`, `src/PhotoReview.App/ActionProfilesWindow.xaml.cs:42` (Apply), `Core/Localization/Languages/en.json` + `vi.json` (+ `en.notes.json`), `Core/Localization/Tr*.cs` (generated accessors if any), tests `tests/PhotoReview.Core.Tests/FileActions/FileActionServiceTests.cs`, a new `ActionDestinationPolicyTests.cs`, `tests/PhotoReview.Integration.Tests` (ActionProfiles save).

Steps (Q-R2 option **a**, recommended):
1. `ActionDestinationPolicy.Validate(string destination)` returns `Ok | Empty | InvalidChars | EscapesSourceFolder`.
   - Relative destination: split it on `\` and `/`. Any `..` segment makes it `EscapesSourceFolder`. Invalid path characters (`Path.GetInvalidPathChars()`, plus `:` outside a drive root) make it `InvalidChars`.
   - Rooted destination (`D:\Backup`, UNC): `Ok`, because it is an explicit user choice.
2. In `FileActionService`, after computing `destinationFolder`, for relative destinations also check that `destinationFolder` starts with `sourceFolder + Path.DirectorySeparatorChar` (`OrdinalIgnoreCase`). If not, throw `JournalCodedException` with a new code `DestinationOutsideSource` **before** Prepared is written, so the journal gets no entry and nothing is moved.
3. In `ActionProfilesWindow` Apply, validate every action. On failure, show a message box naming the action and the reason, and do not close. Use the dialog service or pattern already used in that window.
4. Add the new catalog keys **appended at the end** of `en.json`, `vi.json` and `en.notes.json` to minimise conflicts:
   - `core.fileAction.destinationOutsideSource`
   - `actionProfiles.error.destinationEscapes`
   - `actionProfiles.error.destinationInvalid`

   Vietnamese copy, e.g. "Thư mục đích không được nằm ngoài thư mục ảnh đang duyệt". Then run `tools/i18n-check.ps1`.

Tests:
- `ActionDestinationPolicyTests.Validate_ClassifiesDestinations` (Theory):
  - `"Loai-2"` → Ok
  - `"sub\\deeper"` → Ok
  - `"..\\x"` → Escapes
  - `"a\\..\\..\\x"` → Escapes
  - `"D:\\Backup"` → Ok
  - `"a|b"` → InvalidChars
  - `""` → Empty

  **Mutation:** drop the `..` check, and the Escapes rows fail.
- `FileActionServiceTests.ExecuteAsync_RelativeDestinationOutsideSource_FailsWithoutJournalOrMove`: the fake FS shows no Move call and the journal has no Prepared entry. **Mutation:** remove the step-2 guard, and the test fails.
- An Integration test for the Settings side: Apply with `..\\x` keeps the window open and does not save the settings.

**Acceptance:** §4 is green, and existing default actions (`Loai-2…4`, `Backup`) work unchanged.

**Rollback:** revert. Settings saved in the meantime stay valid, because the policy only rejects values.

**Parallel:** can run with 1a, 1b and 2b. It shares `ActionProfilesWindow` with Wave 4, so Wave 4 goes after this one.

### Wave 2a — CI and architecture rules
**PR title:** `test(ci): enforce ConfigureAwait in lower layers, GlobalState rule, no real Recycle Bin in gate tests, strict verify-release`
**Files:** `.editorconfig`, `.github/workflows/ci.yml`, `tools/verify-release.ps1`, `tools/verify-all.ps1`, `src/PhotoReview.Benchmarking/BenchmarkWorkloadRunner.cs:100` (+ its constructor/DI), `tests/PhotoReview.Integration.Tests/BenchmarkWorkloadRunnerTests.cs`, `tests/PhotoReview.App.Tests/HotPath/RealPhotosManualTests.cs`, `tests/PhotoReview.Architecture.Tests/` (a new `TestIsolationRulesTests.cs`).

Steps:
1. **ADR 0005 rule 2 (CORE-01/DOC-06):** add to `.editorconfig`:
   ```
   # ADR 0005 rule 2: Core/Imaging/Platform never resume on a captured context (review 2026-09-25, CORE-01)
   [src/PhotoReview.{Core,Imaging,Imaging.TurboJpeg,Platform.Windows}/**.cs]
   dotnet_diagnostic.CA2007.severity = error
   ```
   Build, and fix any remaining site. Wave 1a must already be merged. `Task.Yield()` in `ImmediateUiScheduler` is not flagged by CA2007. This is an analyzer, not a source-text test, so it complies with AGENTS "Tests".
2. **TEST-10:**
   - Give `BenchmarkWorkloadRunner` an `IRecycleBin` dependency, defaulting to `WindowsRecycleBin` in the CLI composition.
   - Replace the direct `FileSystem.DeleteFile(..., SendToRecycleBin)` at `:100` with `_recycleBin.SendToRecycleBin(temp)`.
   - In `BenchmarkWorkloadRunnerTests`, pass a recording fake and assert it was called once per "delete" iteration.
3. **TEST-02 (Q-R3 option a):** remove `&Category!=Integration` from the 5 filters in `ci.yml` and from the default in `verify-all.ps1:33-39`. Do this after step 2, so the interleaved-action test no longer touches the real bin. The 6 tests it re-enables are cheap: journal JSONL, concurrent append, 2 benchmark-runner tests, and `PlatformPrimitivesTests`.
4. **HYG-08:** add a job-level `env: TEST_FILTER: "Category!=Manual&Category!=Native&Category!=Slow&Category!=Stress"` and use `--filter "$env:TEST_FILTER"` in all 5 steps (`shell: pwsh`).
5. **TEST-01:**
   - Put `RealPhotosManualTests` in `[Collection("GlobalState")]`.
   - Replace the inline `SetEnvironmentVariable` with the existing `DataRootFixture` (`tests/PhotoReview.TestSupport/TempRoot.cs:45`), which restores the previous value and deletes the root on dispose.
6. **TEST-09:** add `TestIsolationRulesTests.EnvironmentMutatingTests_AreInGlobalStateCollection`. It scans `tests/**/*.cs` with `RepoScan`, excluding `PhotoReview.TestSupport*`. Every file that contains `Environment.SetEnvironmentVariable(` or `DataRootFixture` must contain `[Collection("GlobalState")]`. **Mutation:** delete the attribute from `DiagOptionsTests`, and the rule fails.
7. **TOOL-01/TOOL-04** (`verify-release.ps1`):
   - Add `$ErrorActionPreference = 'Stop'` after `param()`.
   - Change the default `-ReleaseDirectory` to `src\PhotoReview.App\bin\Release\net10.0-windows\publish`.
   - After every native call (`dotnet msbuild`), add `if ($LASTEXITCODE -ne 0) { throw "…" }`.
8. **TEST-07** (code side only): remove the `-Stress` switch from `verify-all.ps1` and `Category!=Stress` from `ci.yml`, **or** keep both if the user wants a Stress category (Q-R3 note). The AGENTS.md filter text is changed in Wave 3, after #72.

Tests and checks:
- The build fails when you remove `.ConfigureAwait(false)` at `UndoService.cs:185` (CA2007 error). Revert afterwards.
- `pwsh tools/verify-release.ps1 -ReleaseDirectory $env:TEMP\empty` exits non-zero on the first missing file.
- The CI run on the PR is green, and its test count grows by the 6 Integration-trait tests.

**Acceptance:** §4 is green, and CI is green.

**Rollback:** revert. To go back to the old CI filter, restore the `Category!=Integration` string.

**Parallel:** runs after 1a. It can run with 1b, 1c and 3. It overlaps with 2b only in `tests/` of different files, so there is no conflict.

### Wave 2b — Deterministic negative asserts and missing coverage
**PR title:** `test: signal-based negative asserts, Settings→journal durability test, Recycle Bin orphan sweep`
**Files:** `tests/PhotoReview.Integration.Tests/MainWindowBehaviorTests.Explorer.cs`, `tests/PhotoReview.Imaging.Tests/BurstPreloadTests.cs:284`, a new `tests/PhotoReview.Integration.Tests/SettingsJournalDurabilityTests.cs`, `tests/PhotoReview.App.Tests/HotPath/{TestRecycleBinCleanup,NativeRecycleBinTests}.cs`. Where a signal is missing, add the smallest production seam: e.g. `MainViewModel.ExplorerOrderTask` (read-only `Task`) or an `internal` completion event. Keep MainViewModel changes to at most 5 lines, because it is a hot spot (§7).

Steps:
1. **TEST-04:** for each `NeverWindow` use (`:82,137,148,206`), find the operation whose completion makes the "never" final: the Explorer-order apply task or folder-load completion.
   - Await that operation's completion (`StaTestHost.WaitForAsync` on the signal, with the normal timeout), then assert the negative state once.
   - Delete `NeverWindow`.
   - If no signal exists, expose one (read-only) and write in the PR body why it is safe.
2. **TEST-05:** replace `await Task.Delay(100)` with: wait for the scheduler's idle/pending signal (or `target.NoStartWithin` built on `GatedTarget` counting under a gate), then assert `Started.Count == cap`.
3. **TEST-06 `SettingsJournalDurabilityTests.PowerLossSafe_SelectedInSettings_JournalWritesDurably`** (STA, AppHost graph with a temp data root via `DataRootFixture`, `[Collection("GlobalState")]`):
   - Open `SettingsWindow`, check `JournalSafeRadio`, and Apply.
   - Execute one Move through `FileActionController`.
   - Assert the `IFileSystem`/journal writer was called with `durable: true`. Use a recording `IFileSystem` through the AppHost override seam, or an `internal OperationJournal.LastWriteWasDurable` property if no seam exists.
   - Also assert the reverse for Fast.
   - **Mutation:** make `OperationJournal` ignore the settings delegate, and the test fails.
4. **TEST-03:**
   - Add `TestRecycleBinCleanup.RemoveOrphansWithPrefix(string tempPrefix, TimeSpan minAge)`. It deletes only bin items whose `DeletedFrom` starts with `%TEMP%\TC06_RecycleBin_` and whose deletion date is older than `minAge` (10 min), so it cannot touch a parallel run.
   - Call it once from the `NativeRecycleBinTests` class fixture constructor.
   - It must never touch other items: assert the prefix and log the count.
   - This test stays `Category=Native`.

Tests: the items above are the tests. Mutation for TEST-04: make the Explorer-order code apply twice (the negative assertion must now fail deterministically, not in about 1 s by luck).

**Acceptance:**
- §4 is green, 3 times in a row, with no new flakes.
- The Native run once removes the old `TC06_RecycleBin_*` orphans. Record the count in the PR body. The user empties anything else by hand.

**Rollback:** revert.

**Parallel:** with 1a, 1b, 1c and 3. The only production seam is the small MainViewModel read-only task, so coordinate with any open MainViewModel PR (§7).

### Wave 3 — Docs single source of truth, stale docs, ADR 0008, ADR 0007/0001 addenda
**PR title:** `docs: single status source, fix stale OC14/PR-batch text, ADR 0008 zoom, ADR 0007/0001 addenda`
**Start only after #72 and #73 are merged.** Re-read each file first, because #72 moves text into `docs/refactoring/archive/*-detail.md`.
**Files:** `docs/ACTIVE-TASKS.md`, `docs/refactoring/{REFACTOR-STATUS,STRUCTURE-OPTIMIZE-STATUS,OPTIMIZE-CLEAN-SUMMARY,TEST-CLEANUP-SUMMARY,OPEN-DECISIONS}.md`, `task_on_progress.md`, `AGENTS.md` (filter line only), a new ADR file `0008-zoom-source-pixel.md` in `docs/adr/`, `docs/adr/0007-io-durability-contract.md`, `docs/adr/0001-image-decoder.md`, `docs/architecture.md` (Zoom paragraph: link only), `docs/INDEX.md` (ADR range `0001-0008`; remove the TC00 row), and `git mv docs/refactoring/TC00-BASELINE-REPORT.md docs/archive/historical/` plus the CQ plan compression.

Steps:
1. **DOC-10:** `docs/ACTIVE-TASKS.md` becomes the only place with per-group status.
   - In `task_on_progress.md`, replace the "Status by Group" table with one line: `Group status: see docs/ACTIVE-TASKS.md`. This also saves about 1.3 KB of T0.
   - In `REFACTOR-STATUS.md`, `OPTIMIZE-CLEAN-SUMMARY.md` and `STRUCTURE-OPTIMIZE-STATUS.md`, remove the status summaries that duplicate ACTIVE-TASKS. Keep each file's own task detail and a link line.
2. **DOC-01 (rest):**
   - In `ACTIVE-TASKS.md`, change the header "PR batch #64-#70 open" to the merged state, with the version from `git describe`.
   - Rewrite the OC, IO and ST rows from `git log` (IO03 #65, IO04+IO05 #66, Recovery #67 merged; ST08/09 and OC15–18 per #73).
3. **DOC-02/09/13:** remove every "blocked by OC14" and "most critical blocker: OC14" line. `grep -rn "OC14" docs/ task_on_progress.md` must afterwards show only historical mentions.
4. **DOC-03:** fix `TEST-CLEANUP-SUMMARY.md` to say TC06/TC07 live in `tests/PhotoReview.App.Tests/HotPath/`.
5. **DOC-05:** write ADR 0008 in the same language and format as 0007 (Vietnamese is fine; use the Edit/Write tool). Content:
   - Context: #43/#47, Q-Z1.
   - Decision: 100 % = 1 source pixel per device pixel, previews decoded to the viewport box, the original decoded on demand for the current image only, not in the RAM LRU or disk cache.
   - Consequences: memory is limited to one original.
   - Links to `architecture.md:94` and `PERF-STATUS.md`.
   - Then set Q-Z1's plan link in `OPEN-DECISIONS` to ADR 0008.
6. **DOC-04:** add a "Triển khai (cập nhật 2026-09-2x)" addendum to ADR 0007: IO03 #65, IO04+IO05 #66 merged, with commit SHAs.
7. **DOC-14:** add an ADR 0001 addendum saying WicDirect now applies the embedded ICC with a WIC color transform, and falls back to WPF only on failure (perf night #40).
8. **DOC-08/DOC-12:**
   - `git mv` TC00-BASELINE-REPORT to `docs/archive/historical/` and fix its links.
   - Compress `CQ-WARNINGS-PLAN.md` to a 3-line DONE digest. `git mv` the full plan to `docs/archive/historical/CQ-WARNINGS-PLAN-2026-09.md`.
9. **TOOL-03/TEST-07:** make the AGENTS.md "Tests" filter match whatever Wave 2a left in CI. It must be the same string as `ci.yml`'s `TEST_FILTER`.
10. Mark this plan's §3 rows as done for waves already merged.

Checks:
- §4 is green (docs only, so the build is optional).
- `tools/docs-budget.ps1 -Check` stays at or under 12 KB for T0.
- `check-doc-links` shows 0 broken.
- The mojibake grep prints 0 on ADR 0007/0008 and `architecture.md`.

**Acceptance:** `grep -rn "PR batch #64" docs task_on_progress.md` finds nothing outside the archive, and there is exactly one status table (ACTIVE-TASKS).

**Rollback:** revert.

**Parallel:** with every code wave. Conflict hot spots: `task_on_progress.md` and `ACTIVE-TASKS.md`, which every wave's closing line also touches. Rebase just before merging.

### Wave 4 — Accessibility and theme tokens
**PR title:** `feat(a11y): label every input for screen readers; use Dark.* brushes instead of hex literals`
**Files:** `src/PhotoReview.App/{ActionProfilesWindow,SettingsWindow,BatchReviewWindow,DiagnosticsWindow,RecoveryWindow,RecoveryPathPanel,BenchmarkWindow,MainWindow}.xaml`, `Themes/DarkControls.xaml`, a new `tests/PhotoReview.Integration.Tests/AccessibilityNamesTests.cs`, and a new architecture rule in `tests/PhotoReview.Architecture.Tests/LocalizationGuardTests.cs` or a new `XamlThemeRulesTests.cs`.

Steps:
1. **APP-01/02:** for every `TextBox`, `ComboBox`, `CheckBox` and `RadioButton` that has a visible label `TextBlock`:
   - give the label an `x:Name`;
   - add `AutomationProperties.LabeledBy="{Binding ElementName=…}"`.

   This needs no new catalog keys. `CheckBox`/`RadioButton`/`Button` with text `Content` are already named by UIA, so leave them. Only icon-only or glyph buttons get `AutomationProperties.Name="{loc:Tr …}"` with an existing key if one fits, or a new key appended at the end of the catalogs (after L12).
   Scope follows Q-R6. Recommended: all user-facing windows plus BatchReview. Benchmark and Diagnostics are best-effort.
2. **APP-03:** replace each `Background|Foreground|BorderBrush="#…"` with `{DynamicResource Dark.*}` using the mapping table in `DarkControls.xaml:10-21`.
   - Fold `#505050` into `Dark.BorderStrong` (`#5A5A5A`).
   - Any colour without an exact brush gets a new named brush in `DarkControls.xaml` (e.g. `Dark.Window` = `#171717`), not a literal.
3. Take a before/after screenshot of each window on the real machine and attach it to the PR. Colours must be identical, apart from the #505050 → #5A5A5A change.

Tests:
- `AccessibilityNamesTests.EveryInputHasAnAccessibleName` (STA, Theory over the window types from Q-R6):
  - Build the window from the AppHost graph and walk it with `AutomationPeer`: `UIElementAutomationPeer.CreatePeerForElement(control).GetName()`.
  - Assert the name is not empty for every `TextBox`, `ComboBox`, `CheckBox`, `RadioButton` and `Button`.
  - **Mutation:** remove one `LabeledBy`, and the test fails, naming the control.
- `XamlThemeRulesTests.NoHexColorLiteralsOutsideTheme`: a XAML architecture rule like the existing XAML localization guard, with an allowlist file for justified exceptions. **Mutation:** add `Background="#123456"` to any window, and the rule fails.

**Acceptance:**
- §4 is green.
- Narrator smoke test on the real machine: Tab through Action Profiles and Settings, and each field is announced by its label.
- Screenshots are unchanged.

**Rollback:** revert. The change is only visual and attributes.

**Parallel:** after 1c (`ActionProfilesWindow`). It can run with 1a, 1b, 2a, 2b and 3. The only conflict is catalog JSON, and only if new keys are needed.

### Wave 5a — Tooling and repository hygiene
**PR title:** `chore: repo hygiene (csproj platforms, gitignore, slnx folders, outputs/, work/ retention, harness warm-up)`
Steps (each a separate commit):
1. HYG-01: remove `NoWarn CA1707` from `tests/PhotoReview.Architecture.Tests/*.csproj:8`.
2. HYG-02: add `<Platforms>AnyCPU;x64</Platforms>` to both TestSupport csproj files.
3. HYG-03: regroup `PhotoReview.slnx` folders (`/src/`, `/tests/`, `/tools/`), then open-build once.
4. HYG-05: comment `.gitignore:98`.
5. HYG-07: remove `.gitignore:378`.
6. HYG-10: add a "Roslyn package ≤ SDK compiler" note to `Directory.Packages.props` and README Build.
7. HYG-04: `tools/clean-work.ps1 [-OlderThanDays 14] [-WhatIf default]`, PS 5.1 compatible, `$ErrorActionPreference='Stop'`.
8. HYG-06 per Q-R4:
   - Option a: `git mv outputs/*.ps1 outputs/*.json deploy/`, then update README `:58,64,133,139` and any tool references.
   - Option b: add `outputs/README.md`.
9. TOOL-02: one untimed warm-up decode in `PerformanceTestHarness.MeasureColdAsync`. Test: a call-count spy shows exactly one decode before the first timed sample.
10. APP-04 (after #73): wrap the three `_ = ApplyFitViewAsync()` calls in a helper that catches, logs and sets the status text. **Do not** change the T89 multi-pass loop.
11. Optional HYG-09: add `binary` rules for `*.jpg *.jpeg *.png *.dll` to `.gitattributes`.

**Acceptance:** §4 is green, and `tools/verify-all.ps1` runs end to end.

**Parallel:** with 5b and 5c.

### Wave 5b — Imaging nits and perf
**PR title:** `perf(imaging): bounded original-dimension cache, accurate ICC diagnostics, factory decoder cache, prune cost`
Steps:
1. IMG-02: replace `_originalDimensions` with `BoundedLruCache<ImageCacheKey,(int,int)>(200_000)`. Add the test-only `KnownOriginalDimensionsCount`. Test: decode 300 distinct keys with the cap set to 100, and assert the count is 100 or less.
2. IMG-04: narrow the `catch … when (colorChain.IsActive)` to the color-chain build + `CopyPixels`. Test: a forced converter failure on an ICC image gives a message that does not mention ICC.
3. IMG-08: cache decoders per backend inside `ImageDecoderFactory`. `FactoryTests`: `Create(b)` returns the same instance twice.
4. IMG-07: share one JPEG segment walker between `HasEmbeddedIccProfile` and `ReadExifOrientation`, and keep `TurboJpegTests` green.
5. IMG-09: add an XML-doc warning on `SourceBytesCache.GetOrRead`.
6. IMG-10: incremental size tracking in `DiskCacheStore`, with a full enumeration only when over quota. For LRU order, use `LastWriteTimeUtc` and touch it on a disk-cache hit (one metadata write, no content read), **or** document creation-order eviction. Measure a prune pass on 20 k files, before and after.
7. IMG-11: at startup, clamp the preview and source-bytes budgets to 50 % of physical RAM (from `IMemoryProbe`), and log the effective values.

**Acceptance:**
- §4 is green.
- `tools/benchmark-folder.ps1` on F4 shows no P50/P95 regression over 5 %.
- Peak working set is at or below the AR02e baseline in `PERF-STATUS.md`.

**Parallel:** after 1b. It can run with 5a and 5c.

### Wave 5c — Core nits
**PR title:** `refactor(core): shared journal transaction helper, journal reverse-read perf, logging nits`
Steps:
1. CORE-07: extract the `JournalTransaction` helper (Prepared → mutate → verify length → Committed/Failed) used by `FileActionService` and the async `RecoveryRetryService` from 1a. The existing tests must stay green unchanged.
2. CORE-08: make the reverse reader parse only the new prefix on each window doubling. Add a Slow perf test with 1 Move per 1,000 Recycle entries over 100 k lines, and assert it takes less than 100 ms (ADR 0003).
3. CORE-09: log inside the `InstanceLock` catch.
4. CORE-10: separate log messages for the transient and corrupt load paths.
5. CORE-11: add a comment for the fail-closed behaviour and unit tests for the `MemorySnapshot` logic.
6. CORE-12: add a comment only.

**Parallel:** after 1a and 1c (shared `FileActionService`). It can run with 5a and 5b.

## 6. Decisions needed from the user

| ID | Question | Options | Pros / cons | Recommendation |
|---|---|---|---|---|
| **Q-R1** | Transparent images in the preview disk cache (IMG-01) | (a) Never persist previews that have alpha. Opaque images, which is nearly all JPEG photos, keep the JPEG cache. (b) New v6 format with a lossless (PNG) payload for alpha entries. (c) As (a), plus bump the format version so existing bad entries are dropped automatically. | (a) is S effort and zero risk. Transparent PNGs are re-decoded on each revisit (RAM LRU still helps), and old bad entries stay until Clear cache. (b) caches everything, but PNG encode is slow on the persist worker and it is M effort. (c) is (a) plus one full re-decode of the whole cache after update. | **(a)** now. (b) only if revisiting transparent PNGs turns out to be slow on F4. |
| **Q-R2** | Where can an action's destination point (CORE-03)? | (a) Relative paths must stay inside the photo folder; absolute paths (`D:\Backup`) allowed; checked at Settings save and at run time. (b) Allow anything, but warn at save time. (c) Keep as is. | (a) blocks accidental `..\..` while keeping backup-to-another-drive. Someone who uses `..\Sorted` must switch to an absolute path. (b) never blocks, so a corrupt config still moves files. | **(a)** |
| **Q-R3** | Run the 6 `Category=Integration` tests in CI (TEST-02), and keep the unused `Stress` category (TEST-07)? | (a) Run them, which adds a few seconds; drop `Stress`. (b) Keep them excluded and document it. | (a) gives one filter everywhere and more coverage, but needs TEST-10 first (real Recycle Bin). (b) keeps the drift between the docs and CI. | **(a)** and drop `Stress` |
| **Q-R4** | `outputs/` folder (HYG-06) | (a) Move the install/uninstall scripts and the example config to `deploy/`, and update the README. (b) Keep them, and add `outputs/README.md`. | (a) gives a clear name, but README commands change for users. (b) needs no move, but the folder stays confusing. | **(a)** |
| **Q-R5** | Shutdown session write when the disk is slow (CORE-02) | (a) Wait up to 2 s, then skip the last write (the last viewed position may be lost). (b) Always wait. | (a) means the app always closes quickly. (b) never loses the position, but closing can hang on a bad disk or antivirus. | **(a) 2 s** |
| **Q-R6** | Accessibility scope (APP-01/02) | (a) All user-facing windows (Main, Settings, Action Profiles, Recovery, Batch Review). (b) (a) plus developer windows (Benchmark, Diagnostics). | (b) is about 2 h more for windows few people use. | **(a)**, with (b) best-effort |

Record the answers in `OPEN-DECISIONS.md` (Wave 3, or the first wave after the answer arrives).

## 7. Running this plan with several agents

- **Concurrent sets:** {1a, 1b, 2b, 3\*} can start in parallel today; 3 starts once #72 and #73 are merged. Next come {2a (after 1a), 1c (after Q-R2 + L12)}, then {4 (after 1c)}, then {5a, 5b (after 1b), 5c (after 1a + 1c)}.
- **Merge order:** 1a → 1b → 2b → 2a → 1c → 3 → 4 → 5a/5b/5c. Earlier waves carry the higher risk. 2a needs 1a because of the CA2007 build rule. 3 goes after 2a so the AGENTS filter text matches CI.
- **No stacked PRs** (AGENTS + memory): every wave branches from the latest `origin/master`. Never branch from another wave's branch. Before opening a PR, run `git merge-base --is-ancestor origin/master HEAD` (it must succeed). After a dependency merges, rebase onto master; do not merge the other branch in.
- **Conflict hot spots:**

  | File | Touched by | Rule |
  |---|---|---|
  | `Core/Localization/Languages/{en,vi,en.notes}.json` | L12, 1c, 4 (maybe) | Append new keys at the end; merge L12 first; re-run `i18n-check`. |
  | `App/ViewModels/MainViewModel.cs`, `MainWindow.xaml.cs` | #73, 2b (a small seam), 5a (APP-04) | Only one open PR at a time may edit it; 5a APP-04 goes after #73. |
  | `docs/architecture.md` | 3 (ADR 0008 link) | Only Wave 3 edits it. Code waves put architecture notes in the PR body, and Wave 3 folds them in. |
  | `task_on_progress.md`, `docs/ACTIVE-TASKS.md` | every wave (closing line), #72, 3 | Each wave adds exactly one line under "Now" and rebases just before merging. Wave 3 owns the structural rewrite. |
  | `ActionProfilesWindow.xaml(.cs)` | 1c, 4 | 1c first. |
  | `PreviewImageService.cs`, `PreloadScheduler.cs` | 1b, 5b, backlog IMG-06 | Run them in that order, never in parallel. |
  | `FileActionService.cs`, `RecoveryRetryService.cs` | 1a, 1c, 5c | 1a, then 1c, then 5c. |
  | `.github/workflows/ci.yml`, `tools/verify-all.ps1` | 2a only | — |
- **Isolation:** each agent works in its own git worktree (`.claude/worktrees/<name>`), never in the main checkout, which other agents share.
- **Handoff line** that each wave adds to `task_on_progress.md` "Now": `REVIEW W<n> · <state> · PR #<n> · <1-line result>`.
