# Round 2 fresh review (read-only) - PhotoReview

Code base: branch `review/2026-09-25-integration` (worktree `.claude/worktrees/integ`). Static reading only; nothing was run,
no Recycle Bin / C:\Xiuren access. Findings already in `docs/archive/evidence/review-2026-09-25/{core,imaging,app,docs-tests}.md`
(CORE-01..12, IMG-01..11, APP-01..04, TOOL-01..03, DOC/TEST/HYG) are NOT repeated.

Legend: `V` = verified by reading the cited code; `S` = suspicion (mechanism visible, outcome not measured). Effort S/M/L.

Counts: High 6, Medium 14, Low 20 (40 total). Critical 0.

Top 6: F-02 (every Move/Recycle evicts the *next* image + discards in-flight preloads), F-03 (Settings save leaves file actions on stale
profiles), F-04 (any startup fault = invisible zombie process), F-06 (held Delete key mass-recycles), F-01 (preload keeps scanning the
old folder), F-05 (Recycle on removable/oversized files can be a permanent delete, unverified).

---------------------------------------------------------------------------------------------------------------------------------
## HIGH

### R2-F-01 - Preload scheduler keeps a stale catalog snapshot across folder switch / Explorer re-order  (High, S-confidence medium)
- File: `src/PhotoReview.Imaging/Preload/PreloadScheduler.cs:210-250` (snapshot taken only at 248), `:273-290` (loop rebuilds `order` from the OLD `entries`);
  `src/PhotoReview.App/Coordinators/FolderLoadCoordinator.cs:151-155` (no preload Cancel on folder change);
  `src/PhotoReview.App/ViewModels/MainViewModel.cs:586` (re-center after `ReplaceOrder`).
- Evidence: `PreloadAroundAsync` returns the running `_preloadSchedulerTask` when `_preloadSchedulerCts` is unchanged (lines 240-245) and only bumps
  `_preloadCenter`; `RunPreloadSchedulerAsync(_snapshotEntries(), ...)` is the only place `entries` is read. Grep of `.Cancel()` on the preload controller shows it
  is called only from FileActionController:108, DuplicateCleanupController:111/163 and MainViewModel:464 - never on folder load or order apply. `ResetCaches` is a no-op in
  production (composition root passes no `resetCachesAction`, `MainViewModelCompositionRoot.cs:85-90`). In whole-folder mode (16 GB default budget) the loop lives for the whole folder.
- Scenario: user switches to the next sibling folder (NextFolder key) or opens a photo directly while the previous folder is still being warmed: the loop keeps decoding the
  OLD folder's paths around the NEW center index; the new folder gets no preload until the old loop drains -> first Next/Prev after every folder switch is a cold decode;
  RAM/CPU wasted on a folder the user left. Same with Explorer order: `OnOrderApplied` re-centers on the old (fallback-order) array, exactly what the comment says it wants to avoid.
- Fix (small): in `PreloadAroundAsync` compare `catalog.StructuralVersion` (or array reference) captured with the snapshot; if changed, cancel the lifetime and start a new one. Or call
  `_preloadController.Cancel()` from `IFolderLoadSink.OnCatalogReady` / `OnOrderApplied`.
- Effort: S. Confidence: medium (verify with the `Preload policy:` log lines after a quick folder switch).

### R2-F-02 - Every Move/Recycle evicts the NEXT image and discards in-flight preloads  (High, V)
- File: `src/PhotoReview.App/ViewModels/MainViewModel.cs:608-616`; `src/PhotoReview.App/Coordinators/FileActionController.cs:116-117`; `src/PhotoReview.Core/Catalog/ReviewCatalog.cs:210-227`;
  `src/PhotoReview.Imaging/Caching/PreviewImageService.cs:533-541, 460, 527`.
- Evidence: `FileActionController` calls `_catalog.Remove(source)` (which sets `CurrentIndex = nextIndex`, ReviewCatalog.cs:224-225) and then `_sink.OnCatalogChanged()`.
  `OnCatalogChanged` evicts `_catalog.Current?.Path` - i.e. the image that is about to be shown, not the removed `source`. `EvictCachedPath` also does `_cacheEpoch++` (line 538); results of decodes
  started under the old epoch are not cached (line 460: `if (cacheEpoch == _cacheEpoch)`) and `HasInflightPreview` keys on the new epoch (line 527), so the viewer cannot join an in-flight preload.
- Scenario (the hottest review loop: triage key -> next photo): the just-preloaded next image is dropped from RAM, all up to 8 in-flight preload decodes are thrown away, and the next image is
  re-read and re-decoded from disk (100-300 ms visible latency per action). The removed file's bitmap stays in RAM until LRU.
- Fix (small): pass the removed path to the sink (`OnCatalogChanged(string removedPath)`), evict that path only, and avoid the epoch bump for a single-path eviction (remove keys, keep epoch), or
  skip eviction entirely for Move/Recycle (key includes path+length+mtime).
- Effort: S. Confidence: high.

### R2-F-03 - `FileActionController` keeps the settings instance captured at start-up; Settings > Save is not applied to actions  (High, V)
- File: `src/PhotoReview.App/ViewModels/MainViewModel.cs:104-106` (`Settings` evaluated once), `src/PhotoReview.App/Coordinators/FileActionController.cs:28,49,56,66`;
  `src/PhotoReview.Core/Settings/SettingsStore.cs:97` (`_current = settings`); `src/PhotoReview.App/SettingsWindow.xaml.cs:36,224` (window edits a clone, saves a NEW instance).
- Evidence: the controller stores `AppSettings _settings` (readonly). `MainWindow` refreshes its own `_settings`/`ShortcutRouter` on `Changed` (MainWindow.xaml.cs:74) and sets `_viewModel.Settings`
  (a separate override property), but nothing rebuilds/updates the controller. `--perf-session` mutates the shared instance in place (PerfSession.cs, `settings.Actions = ...`), which hides this in harness/tests.
- Scenario: user edits an action profile (destination, operation, Confirm flag, name, order) and saves. The shortcut router (rebuilt) resolves index i from the NEW list, but
  `RunActionAsync(i)` reads `_settings.Actions[i]` from the OLD list: file moved to the old destination, `Confirm=true` ignored, new actions beyond the old count silently do nothing, removed/reordered
  actions run the wrong operation (Copy vs Move vs Recycle) - until restart.
- Fix (small): give the controller a `Func<AppSettings>` (like `ImagePresenter._getSettings`) or read `_settingsStore.Current` at use time.
- Effort: S. Confidence: high.

### R2-F-04 - Any failure after the handlers are installed leaves an invisible, unkillable-looking process  (High, V)
- File: `src/PhotoReview.App/App.xaml.cs:241, 255-259` (also `:245`); `src/PhotoReview.App/App.xaml` (no `ShutdownMode`); triggers: `src/PhotoReview.Core/Caching/BoundedLruCache.cs:16`,
  `src/PhotoReview.Core/Settings/SettingsStore.cs:41-45`, `src/PhotoReview.App/Input/ShortcutRouter.cs:59-60`, `src/PhotoReview.Core/Catalog`/`InstanceLock` (F-08).
- Evidence: `App_Startup` is `async void`; after `DispatcherUnhandledException += ... a.Handled = true` (line 241) an exception from `GetRequiredService<MainWindow>()` / window ctor / `InstanceLock` is logged (only if
  logging is enabled) and swallowed. No window was ever shown, `ShutdownMode` is the default `OnLastWindowClose`, so the process never exits and (once past line 245) holds the instance mutex.
  Cheap ways to reach it from a hand-edited/corrupt `config.json`: `ImageCacheCapacityBytes` or `SourceBytesCapacityBytes` <= 0 (BoundedLruCache ctor throws `ArgumentOutOfRangeException`,
  nothing clamps <= 0: `RamBudgetPolicy.cs:34-39`), `"Actions":[null]` (NRE in `ShortcutRouter.Rebuild`), `PreloadWorkerCount` <= 0. SettingsStore only handles `JsonException`/IO errors; numeric ranges are never validated.
- Scenario: user edits a size in config.json to 0/-1 -> app "does nothing" when launched, Task Manager shows PhotoReview.App.exe with no window; every relaunch on that folder now says "folder already open in another instance".
- Fix (small): wrap the body of `App_Startup` in try/catch -> show message box + `Shutdown(1)`; validate/clamp numeric settings after `Load` (SettingsValidator).
- Effort: S. Confidence: high.

### R2-F-05 - "Recycle" can silently be a permanent delete and is never verified  (High, S-confidence medium)
- File: `src/PhotoReview.Platform.Windows/WindowsRecycleBin.cs:24-28`; `src/PhotoReview.Core/FileActions/FileActionService.cs:185-186`; doc claim `docs/APP-MECHANISMS-VI.md:47`.
- Evidence: `FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)` maps to `SHFileOperation` with FOF_NOCONFIRMATION|ALLOWUNDO. For files the shell cannot recycle
  (removable drives/SD cards/USB, network shares, items larger than the bin quota, bin disabled) that combination deletes permanently without the usual "permanently delete?" prompt. `FileActionService`
  marks the mutation completed and Commits the journal without checking that the source is gone or that an item now exists in the bin; there is no drive-type check anywhere (grep `DriveType|Removable` = 0 hits).
- Scenario: reviewing photos straight from a camera card or NAS: Delete -> file irrecoverably gone, journal says `Committed`, Ctrl+Z / Recovery report "restored/unverifiable" and the user believes it is in the bin.
- Fix (small): before deleting, check `DriveInfo(path).DriveType` (Fixed only) or use `IFileOperation` with `FOFX_RECYCLEONDELETE`/`FOF_WANTNUKEWARNING`; after delete verify `!File.Exists`. Show a confirm for non-fixed drives.
- Effort: M. Confidence: medium (documented shell behaviour; not exercised here because the Recycle Bin must not be touched).

### R2-F-06 - Key auto-repeat is not filtered: holding Delete / an action key mass-deletes or mass-moves  (High, V)
- File: `src/PhotoReview.App/MainWindow.xaml.cs:342-375` (`Window_KeyDown`); grep `IsRepeat` over `src` = 0 hits.
- Evidence: every repeat KeyDown resolves to `Recycle`/`RunAction`/`Undo`. `FileActionGate` only rejects overlap; each action finishes in tens of ms, `FileActionController` removes the current entry and presents the next
  one *before* the I/O (INV-3), so the next repeat acts on the image that was only just started to load.
- Scenario: user holds Delete a moment too long -> 5-10 photos/second are sent to the bin without ever being displayed; Undo is single-level for Recycle (`UndoService._lastUndoAction`) so only the last one is undoable via Ctrl+Z.
- Fix (small): `if (e.IsRepeat && cmd is Recycle/RunAction/Undo/Skip) { e.Handled = true; return; }` (keep repeat for Next/Previous/zoom).
- Effort: S. Confidence: high.

---------------------------------------------------------------------------------------------------------------------------------
## MEDIUM

### R2-F-07 - Vanished folder/drive: recursive present loop can overflow the stack; transient IO errors are read as "missing"  (Medium, S-confidence medium)
- File: `src/PhotoReview.App/Coordinators/ImagePresenter.cs:186-190, 436-454, 485-503`.
- Evidence: `PresentAsync` -> `TryGetFileInfo` false -> `await RemoveMissingCatalogItemAsync` -> `_catalog.Remove` + `await PresentAsync(nextIndex)`. All awaited calls complete synchronously (stat fails immediately),
  so N consecutive missing files = N nested async frames on the UI thread (1 MB stack). `TryGetFileInfo` has a bare `catch { return false; }` (line 502) and `FileExists` returns false on any error (offline share, ejected card).
- Scenario: SD card pulled / NAS drops with a 3-5 k image catalog open, user presses Next -> stack overflow (process dies, uncatchable) or, on a transient hiccup, the whole catalog is silently drained.
- Fix (small): loop instead of recursion (`while (missing) { remove; next }`), and stop after e.g. 3 consecutive misses when the parent folder is unreachable (`Directory.Exists(folder)==false` -> OnFailed).
- Effort: S. Confidence: medium (frame size not measured).

### R2-F-08 - `InstanceLock`: relative path argument crashes start-up; lock key not normalised  (Medium, V)
- File: `src/PhotoReview.Platform.Windows/InstanceLock.cs:18`; `src/PhotoReview.App/App.xaml.cs:198-200, 245`.
- Evidence: `PhotoReview.App.exe photo.jpg` (relative, file exists) -> `lockFolder = Path.GetDirectoryName("photo.jpg") == ""` -> `Path.GetFullPath("")` throws `ArgumentException` (then F-04 zombie). The key hashes the
  string as given: `C:\Photos`, `C:\Photos\`, `c:\photos` and a folder passed from a file (`GetDirectoryName`) yield different mutexes, so two instances can open one folder. No-arg launch keys on `GetFullPath("PhotoReview")`, i.e. on the
  *current directory*: a Start-Menu launch and a shortcut with another working directory get different locks, while two launches from the same cwd report "folder already open in another instance" with no folder.
- Fix (small): `Path.GetFullPath(initial)` before `GetDirectoryName`; in `InstanceLock` normalise with `TrimEndingDirectorySeparator` + `ToUpperInvariant`; use a constant name for the no-folder case.
- Effort: S. Confidence: high (relative path), medium (rest).

### R2-F-09 - Opening another photo from Explorer while the app is open shows an error box instead of forwarding; multi-select opens N dialogs  (Medium, V)
- File: `src/PhotoReview.App/App.xaml.cs:245-253`; `deploy/install-photo-review-association.ps1:12` (`"%1"` verb).
- Evidence: lock is per folder, no IPC. Double-clicking a second photo of the same folder, or "Open" on several selected photos (Explorer starts one process per file for a `"%1"` verb), makes each extra process show a modal
  `FolderAlreadyOpenInOtherInstance` message and exit.
- Fix (small): on lock failure send the path to the owner (named pipe / `WM_COPYDATA` / `FindWindow` + `SetForegroundWindow`) and exit silently; at minimum skip the dialog.
- Effort: M. Confidence: high.

### R2-F-10 - Session resume is discarded whenever Explorer order is applied; `Skipped` is write-only  (Medium, V/S)
- File: `src/PhotoReview.App/Coordinators/FolderLoadCoordinator.cs:165, 184, 245-254`; `src/PhotoReview.Core/Session/SessionStore.cs:15`; `src/PhotoReview.App/ViewModels/MainViewModel.cs:312`.
- Evidence: `resumePath = initialPath ?? session.CurrentPath`, but if the snapshot is applied early `resumePath = null` (line 184), and on the late path `mayReplaceInitialFallback` re-presents index 0 (lines 245-254) - so with a working Explorer
  snapshot (the normal case) the saved `CurrentPath` is never used. `SessionState.Skipped` is appended and persisted (SkipAsync, duplicates allowed) but never read by any code (grep) -> the file grows unboundedly with no effect.
- Scenario: user reopens a large folder expecting to continue where they stopped; always starts at the first image. (If this is intended, the whole session-restore feature is dead code and `SessionWriter` I/O per navigation is pure cost.)
- Fix (small): apply session `CurrentPath` when `initialPath is null` and `CurrentPath` is in the catalog (also in the Explorer-applied branches); dedupe/drop `Skipped` or use it.
- Effort: S. Confidence: medium (intent unclear; behaviour verified).

### R2-F-11 - Preload thread reads the catalog off the UI thread (ADR 0005 contract)  (Medium, V/S)
- File: `src/PhotoReview.Core/Catalog/SourceSizeTracker.cs:45-49`; `src/PhotoReview.Imaging/Preload/PreloadScheduler.cs:280` (called after `await ...ConfigureAwait(false)` at :345, i.e. on a pool thread);
  wiring `src/PhotoReview.App/Composition/MainViewModelCompositionRoot.cs:56-59`.
- Evidence: `GetTotal()` locks its own gate but enumerates `_catalog.Entries` (`List<CatalogEntry>.AsReadOnly()`) and reads `StructuralVersion` (plain int) while the UI thread runs `Remove/Restore/ReplaceOrder/Reset`. `AssertOwnerThread` only guards mutations, and only in Debug.
- Scenario: recycle/move (structural change) while a navigation bumps the priority version -> `InvalidOperationException: Collection was modified` in the scheduler loop -> caught by the generic handler ("Preload scheduler failed"), preload for that lifetime stops silently. (Also stale/torn total -> wrong whole-folder decision.)
- Fix (small): make the tracker take an `EntriesSnapshot()` on the UI thread (or have the scheduler pass `entries` it already holds) instead of touching the live catalog.
- Effort: S. Confidence: medium.

### R2-F-12 - Unhandled exceptions are swallowed and, by default, not recorded anywhere  (Medium, V)
- File: `src/PhotoReview.App/App.xaml.cs:241-243`.
- Evidence: `DispatcherUnhandledException` -> `AppLog.Error(...)` + `Handled = true`; `AppLog` writes nothing unless logging is enabled (default off, INV-10) and the writer is a background thread. The `AppDomain.UnhandledException` handler does not
  `Flush()`, so a fatal exception is queued and lost when the process dies. The forced-log helper used for config errors (`LogStartupErrorForced`, line 269) is not used here. The user gets no dialog.
- Scenario: any UI-thread bug -> operation silently does nothing, no trace to diagnose; a fatal crash leaves no log even with logging on.
- Fix (small): use the forced-log path (enable+flush) for these three handlers; show a one-time non-modal notice.
- Effort: S. Confidence: high.

### R2-F-13 - Zoom-detail full-resolution decodes are unbounded when InitialViewMode is 100/200/400 %  (Medium, S)
- File: `src/PhotoReview.App/Coordinators/ZoomDetailLoader.cs:93-127`; `src/PhotoReview.Imaging/Caching/PreviewImageService.cs:630-653`.
- Evidence: with a non-Fit initial view, every presented image calls `OnPreviewPresented -> Update -> LoadAsync -> DecodeOriginalAsync`, which uses `Task.Factory.StartNew(..., LongRunning)` (a new dedicated thread each), cancellable only
  before it starts, bypasses RAM/preload budgets and re-reads the file (disk) each time; nothing throttles or checks `IsMemoryPressureHigh`.
- Scenario: review at 100 % while paging quickly: a stream of 24-100 MP decodes (~96 MB+ each) runs concurrently with viewer + preload decodes -> RAM spikes, slower switches, extra disk reads.
- Fix (small): gate on a single in-flight original + short debounce (e.g. 150 ms) + `HasHeadroom`; reuse `SourceBytesCache` if enabled.
- Effort: S-M. Confidence: medium (not measured).

### R2-F-14 - Benchmark samples include their own setup I/O; "cold" first-frame is not cold  (Medium, V)
- File: `src/PhotoReview.Benchmarking/BenchmarkWorkloadRunner.cs:77 (File.WriteAllBytesAsync(temp, ReadAllBytesAsync(...)))`, `:38 (EvictForColdDecode: ClearDisk + evict)`; timing wrapper `BenchmarkEngine.cs:55-72`.
- Evidence: the stopwatch wraps the whole `operation(...)`. FileAction samples therefore contain a full read + write of the source photo; FirstFrame samples contain the disk-cache directory deletion. FirstFrame always decodes `files[0]`
  (SelectIndex => 0), so after iteration 1 the OS page cache is warm - the number is a warm-file decode, labelled cold.
- Effect: FileAction p50/p95 dominated by copy time; profiles compare unfairly; conclusions about race timing are unreliable.
- Fix (small): do setup before `Stopwatch.StartNew()` (split `prepare`/`measure` delegates); rotate files or document warm-OS-cache.
- Effort: S. Confidence: high.

### R2-F-15 - `smoke-test.ps1` / `fault-injection-test.ps1` gates test only .NET primitives, and smoke-test uses the real Recycle Bin  (Medium, V)
- File: `tools/smoke-test.ps1:37` (VB `DeleteFile` ... SendToRecycleBin on every `verify-all` run), `:33-41` (PASS lines printed unconditionally, e.g. `PASS: fixture cleanup scope`); `tools/fault-injection-test.ps1:14-27`; called from `tools/verify-all.ps1:273-278`.
- Evidence: both scripts call `[IO.File]::Move`, `WriteAllBytes` and assert what they just did; no PhotoReview code (FileActionService, journal, recovery) is invoked. The "journal Prepared" step writes a file with PowerShell and checks it exists.
- Effect: two "safety gates" that can never fail for a product regression (false confidence) and that leave a 3-byte item in the user's real Recycle Bin per run.
- Fix (small): replace with `dotnet test` categories that drive `FileActionService` on a temp dir with a fake bin, or delete the scripts from the gate.
- Effort: S-M. Confidence: high.

### R2-F-16 - "Clear cache" / backend change do heavy directory deletes on the UI thread  (Medium, V)
- File: `src/PhotoReview.App/Coordinators/DuplicateCleanupController.cs:155-171` (`ClearCacheAsync` is `async` with an all-synchronous body), `:164-167`; `src/PhotoReview.App/ViewModels/MainViewModel.cs:464-469` (`ShowSettings`); implementations `ThumbnailCache.cs:205-211`, `PreviewImageService.cs:556-568`, `DiskCacheStore.cs:264-273`.
- Evidence: `ClearDisk()` enumerates and deletes every cache file (up to the 4 GB disk-cache quota, thousands of files) synchronously, plus `CleanupLegacyCacheFiles`. `ClearSourceBytesCache()` exists (PreviewImageService.cs:575) but is never called, so "Clear cache" leaves up to 16 GB source bytes in RAM when that option is on.
- Fix (small): `await Task.Run(...)` around the disk clears; also call `ClearSourceBytesCache()` and `_hashService.Clear()`.
- Effort: S. Confidence: high.

### R2-F-17 - Recovery window: two full journal parses under the journal lock on the UI thread; journal is never compacted  (Medium, V)
- File: `src/PhotoReview.App/Services/WpfDialogService.cs:78`; `src/PhotoReview.Core/FileActions/OperationJournal.cs:196-231` (`ReadEntries` holds `_gate` for the whole parse), `:57-100` (append-only, no rotation).
- Evidence: `ReadPendingOperations().Concat(ReadFailedOperations())` = two full-file `ComputeLatestEntries` passes; with the journal growing by 2+ lines per action forever (ADR 0003 only optimises the Move tail read) the Recovery click freezes the UI and any concurrent file action blocks on `_gate` at `AppendLines`.
- Fix (small): compute both lists in one pass and run it in `Task.Run` before showing the window; add periodic compaction (rewrite only non-terminal ids) or size-based rotation.
- Effort: S (single pass) / M (compaction). Confidence: high.

### R2-F-18 - `verify-release.ps1` accepts a `-nogit` build; CI never asserts the version  (Medium, V)
- File: `tools/verify-release.ps1:51,54` (`[1]` of a 1-element split is `$null`, `-replace` -> `''` on both sides), `:47-50`; `Directory.Build.targets:68-73`; `.github/workflows/ci.yml:66-71`.
- Evidence: when git or the `v2.0.0` tag is missing (`--no-tags` clone, dubious-ownership, shallow fetch) MSBuild silently emits `2.0.0-nogit`; verify-release compares `2.0.0.0 == 2.0.0.0` and `'' == ''` and passes; "Show version" only prints.
  The tag job (`ci.yml:157-178`) derives numbers with its own git command, so a nogit artifact would be uploaded while tags are still created.
- Fix (small): fail when `InformationalVersion` contains `-nogit` or has no `+sha`; add the same assertion to the CI "Show version" step.
- Effort: S. Confidence: high.

### R2-F-19 - `install-photo-review-association.ps1` has no `$ErrorActionPreference = 'Stop'`  (Medium, V)
- File: `deploy/install-photo-review-association.ps1:1-17` (uninstall script sets it, install does not).
- Evidence: a wrong `-ExePath` makes `Resolve-Path` write a non-terminating error, `$resolvedExe` is `$null`, and the script still registers `"" "%1"` under `HKCU\Software\Classes\PhotoReview.App\shell\open\command` and prints "was registered". Extensions limited to `.jpg/.jpeg/.png` although the app opens more types.
- Fix (small): add `$ErrorActionPreference='Stop'`, check `Test-Path` on the exe, add the remaining supported extensions.
- Effort: S. Confidence: high.

### R2-F-20 - Duplicate cleanup bypasses `FileActionGate` and can run twice concurrently  (Medium, V)
- File: `src/PhotoReview.App/ViewModels/MainViewModel.cs:419-420` (no `_fileActionGate.RunExclusiveAsync`); `src/PhotoReview.App/Coordinators/DuplicateCleanupController.cs:63, 73-83, 118-125`.
- Evidence: only a start-of-method `IsBusy` snapshot; hashing (long, `CancellationToken.None`, no progress) and the modal review happen before any lock. Two menu clicks during hashing, or a normal Recycle/Move/Undo in between, interleave with the batch; the batch never registers undo and is not cancellable. Hashing the whole folder goes through `SourceBytesCache.GetOrRead`
  (FileHashService.cs:34-40 region) and evicts the preview working set when that cache is enabled.
- Fix (small): run the whole method inside the gate, add a cancellation token + progress status, hash via streaming (skip source-bytes cache).
- Effort: S-M. Confidence: high.

---------------------------------------------------------------------------------------------------------------------------------
## LOW

### R2-F-21 - `--benchmark-all` always fails: two profiles are "not implemented"  (Low, V)
- `src/PhotoReview.Benchmarking/BenchmarkProfiles.cs:31-32` (`cache-recovery`, `explorer-reindex`, `CorrectnessOnly=false`), `BenchmarkWorkloadRunner.cs:20-27` (throws `NotSupportedException`), `tools/PhotoReview.Benchmark.Cli/Program.cs:86`.
  `--benchmark-all` includes them, so two FAIL reports and exit code 1 on every run. Fix: mark them `CorrectnessOnly` or drop until implemented. Effort S.

### R2-F-22 - All benchmark profiles disable the disk cache  (Low, V)
- `BenchmarkProfiles.cs:45` (`new(..., reserve, false, detailed, workload)`; line 40 also `false`) -> `BenchmarkImageExecutor.cs:37-38 disableDiskCacheOverride: !profile.DiskCache` is always true. Production runs with the v5 disk cache, so profile numbers never include disk-hit behaviour despite comments describing it. Fix: profile flag or a `--disk-cache` CLI switch. Effort S.

### R2-F-23 - `FileLog`: unbounded queue when the file cannot be written; 4 s shutdown stall; full paths in log  (Low, V)
- `src/PhotoReview.Core/Diagnostics/FileLog.cs:159-165` (enqueue without bound), `:195-204` (any open/rotate failure is swallowed before dequeue, so the queue only grows; rotation `File.Move(overwrite)` fails while another instance holds `.1`), `:123-130` (`Shutdown` = `Join(2000)` + `Flush` up to 2000 ms on the UI thread).
  Messages carry full photo paths (`ImagePresenter.cs:181-182`, `App.xaml.cs:229` startup args): privacy note for shared logs. Fix: cap the queue (drop oldest) and log a single "log unavailable"; document path content. Effort S.

### R2-F-24 - Journal line with missing `Id` throws out of `ReadEntries`  (Low, V)
- `src/PhotoReview.Core/FileActions/OperationJournal.cs:210, 225-228`: only `JsonException` is caught; `latest[entry.Id]` with `Id == null` (valid JSON `{"Type":"Move","State":"Prepared"}`, records don't require members) throws `ArgumentNullException` and disables the Recovery window until the file is edited. Fix: skip entries with null/empty `Id`/`Source`. Effort S.

### R2-F-25 - `SessionState.Skipped` = null from JSON crashes `SessionWriter.Update`  (Low, V)
- `src/PhotoReview.Core/Session/SessionStore.cs:48` (Deserialize, no null-repair, `Skipped` property is non-nullable but STJ leaves null) -> `SessionWriter.cs:49` `[.. state.Skipped]` NRE on the first navigation of that folder. Fix: `state.Skipped ??= []` after load. Effort S.

### R2-F-26 - Window placement file: `ShowCommand` not validated  (Low, V)
- `src/PhotoReview.App/WindowPlacementService.cs:29-34`: only `2` (minimized) is normalised; a value such as `0` (SW_HIDE) or `7` from a damaged `window-placement.json` restores a hidden/minimised window (see F-04 for the zombie effect). Fix: accept only 1 and 3. Effort S.

### R2-F-27 - `ShortcutRouter.Rebuild` on every key press  (Low, V)
- `src/PhotoReview.App/MainWindow.xaml.cs:344-345`: 13 x `Enum.TryParse<Key>` + list rebuild + property write per KeyDown (30/s on repeat) although `SettingsStore.Changed` already rebuilds (line 74). Remove both lines. Effort S.

### R2-F-28 - Hot-path metadata stats: 3 per navigation, partly uncounted  (Low, V)
- `src/PhotoReview.App/Coordinators/ImagePresenter.cs:186 (FileExists before decode), 384 (second FileExists + `new FileInfo` + Length/LastWriteTimeUtc = another stat, not through `IFileSystem`/`CountingFileSystem`), 194` (`initialInfo.Length` when catalog metadata missing).
  Contradicts the "minimise disk reads" goal on network shares and makes `StatCount` under-report. Fix: one `GetFileStat` via `IFileSystem`, reuse the result. Effort S.

### R2-F-29 - Superseded navigation leaves `previewTask` unobserved  (Low, V)
- `ImagePresenter.cs:260` (thumbnail wins, then `return` on stale token) and `:299`: the still-running `previewTask` is never observed; if that decode later fails (bad file) the fault is reported by `TaskScheduler.UnobservedTaskException` -> `AppLog.Error` at GC time, with no context. Add `_ = previewTask.ContinueWith(observe)` like line 286. Effort S.

### R2-F-30 - Closing the window can block the UI up to 5 s on a stuck Explorer COM call  (Low, V)
- `src/PhotoReview.Platform.Windows/Explorer/ExplorerOrderService.cs:73-79` (`_thread.Join(5s)` in `Dispose`) called from `MainWindow.xaml.cs:203` (`Window_Closed`) on the UI thread. Fix: `Join(200 ms)` then abandon (thread is background). Effort S.

### R2-F-31 - Three different percentile definitions; harness writes its report into the photo folder  (Low, V)
- `Core/Diagnostics/BenchmarkStatistics.cs:5-13` (linear interpolation), `Benchmarking/PerformanceTestHarness.cs:103-107` (nearest-rank on whole-ms `long`), `PerfAnalysis/PerfAnalyzeStats.cs` (`NearestRank`): P95 of the same 30 samples differs by report; `ElapsedMilliseconds` truncation makes the `p95 <= max(1,p50)*8` rule coarse at small p50.
  Default report path is `<photo folder>\photoreview-performance-report.json` (`PerformanceTestHarness.cs:~51`) - writes into the user's source folder. Fix: one shared helper; default to temp. Effort S.

### R2-F-32 - `PerfCsvReader` splits on physical lines but the writer quotes embedded newlines  (Low, S)
- `src/PhotoReview.PerfAnalysis/PerfAnalyzeCsv.cs:61` (`File.ReadLines`) vs `src/PhotoReview.Core/Diagnostics/PerfCsvListener.cs:262-267` (`CsvEscape` wraps `\n`/`\r`). A text field containing a newline is dropped as two malformed rows. Fix: sanitise newlines in the writer. Effort S. (No current event known to emit one.)

### R2-F-33 - Dead / inconsistent performance knobs  (Low, V)
- `AppSettings.MemoryReserveBytes` (`AppSettings.cs:21`) is never read in production (only defaults: `PhysicalMemory.cs:16,21`); `PerformanceOptions.PreloadMemoryLoadLimit=0.90` (`PerformanceOptions.cs:11`) vs `RamBudgetPolicy.PerformanceOptionsDefaults.PreloadMemoryLoadLimit=0.80` (`RamBudgetPolicy.cs:94`) used for the whole-folder decision; `IsMemoryPressureHigh` hard-codes 0.85. Users editing the reserve see no effect. Fix: wire settings through or delete. Effort S.

### R2-F-34 - Orphan `*.tmp` files from interrupted atomic writes are never swept  (Low, V)
- `src/PhotoReview.Core/IO/PhysicalFileSystem.cs:88-113`: temp name is `path.<guid>.tmp`; a kill between create and move leaves it forever in `Sessions\` / the config folder (session writes are frequent). Fix: delete `*.tmp` older than a day at start-up. Effort S.

### R2-F-35 - Version computation spawns ~5 `git` processes per project per build  (Low, V)
- `Directory.Build.targets:27-52` runs for every project (`BeforeTargets="GetAssemblyVersion"`): rev-parse, merge-base (x2 when origin/master is missing), two rev-list. ~12+ projects -> 60+ process spawns per build, more on incremental. Fix: compute once per solution build (a single `Exec` in a `CalculateVersion` target with `Inputs`) or cache in an imported props file. Effort M.

### R2-F-36 - CI workflow has no `permissions:` block  (Low, V)
- `.github/workflows/ci.yml:1-24`: `build-test-publish` inherits the repository default token (write on older repos) although it needs read; only `tag-version` (`:145-146`) declares `contents: write`. Add `permissions: contents: read` at workflow level. Actions are tag-pinned (`@v4`), acceptable for first-party. Effort S.

### R2-F-37 - `fetch-native.ps1`: download errors are not detected where they happen  (Low, V)
- `tools/fetch-native.ps1:26` (`curl.exe -sL` without `--fail/--retry`, HTML error page is saved as the zip) and `:29` (`tar.exe` exit code ignored under `$ErrorActionPreference='Stop'`); the failure then shows as a confusing `Move-Item` error. SHA-256 pinning itself is fine. A mismatching DLL is left in `native/x64` after the throw (line 35). Fix: `--fail --retry 3`, check `$LASTEXITCODE`, delete the bad file. Effort S.

### R2-F-38 - `docs-budget.ps1` measures `HEAD`, not the working tree  (Low, V)
- `tools/docs-budget.ps1:24-26`: `git show "HEAD:$_"` returns `$null` for staged-new files (`GetByteCount($null)` throws) and hides uncommitted growth of T0 files until after commit; `git ls-files` with `core.quotepath` breaks non-ASCII names. Fix: read the working-tree file, `-c core.quotepath=off`. Effort S.

### R2-F-39 - `verify-all.ps1`: `-TestReport` cannot work; release directory is wiped without a guard  (Low, V)
- `tools/verify-all.ps1:64` searches `tests\<p>\obj\<cfg>` for `.trx`, but `dotnet test --logger trx` writes to `TestResults` (no `--results-directory`), so the report always says "No .trx files"; `:165,168` use `${duration:F2}` which PowerShell parses as drive `duration:` variable `F2` (would throw once trx were found).
  `:49` `Remove-Item -Recurse -Force $Directory` accepts any `-ReleaseDirectory` (e.g. `.` or the repo root). Fix: pass `--results-directory`, use `("{0:F2}" -f $duration)`, require the target to end in `\publish`/`PhotoReview-self-contained`. Effort S.

### R2-F-40 - Benchmark window creates a second preview cache + preload scheduler inside the live app  (Low, S)
- `src/PhotoReview.Benchmarking/BenchmarkImageExecutor.cs:37-41` (own `PreviewImageService` with `PerformanceOptions.ImageCacheCapacityBytes`, own `PreloadScheduler`), used from `BenchmarkWindow.xaml.cs` (`RunAsync`, no file cap unlike the CLI `Take(64)`): two budgets, each clamped to 50 % of physical RAM, coexist with the app's own cache while the user's photos stay open; also toggles the global `AppLog.Enabled` per profile. The headroom probe limits the damage but the run perturbs (and is perturbed by) the app it measures. Fix: run in-process only with a reduced cap or point to the CLI. Effort S.

---------------------------------------------------------------------------------------------------------------------------------
## Areas checked with no new finding
- ADR 0005 in App: no `.Result/.Wait/GetAwaiter().GetResult` in `src/PhotoReview.App`; `WpfPresentationSink` marshals only as a safety net; `DispatcherUiScheduler` correct. Only exceptions are the F-11 catalog read and the (documented) `PreloadScheduler.Dispose` drain.
- Event handlers: `Localizer.CurrentChanged` paired in MainWindow/BenchmarkWindow; `LocalizationSource` is a process-lifetime singleton; other `+=` are same-lifetime (MainWindow lambdas on store/viewer are app-lifetime).
- Localization: missing key returns the key (`Localizer.cs:311-314`), placeholders validated against English, oversize/broken user files skipped with warnings, culture switch guarded (`LocalizationService.cs:71-80`).
- Atomic write helper (`PhysicalFileSystem.WriteAllTextAtomic`) is correct (CreateNew temp + Move overwrite + cleanup).
- `PerfSession.ValidatePaths`/`DeleteTempDir` use marker files and overlap checks - safe.
- Explorer COM interop: releases PIDLs/`IUnknown` pointers on all paths reviewed; STA pump + timeout/cancel semantics OK apart from F-30.
- `WindowsRecycleBin.TryRestore` COM release logic (CORE-05) is now correct on this branch (`kept` flag); shell verb matching relies on English/Vietnamese/German names only (documented).
