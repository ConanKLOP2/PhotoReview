# Test Parity Matrix — CLI Checks vs. xUnit Coverage (T10)

Generated: 2026-09-16 (rebuilt)
Task: T10 — Ma trận parity test
Objective: Ghép các CLI checks từ `Program.cs` với test xUnit để xác định COVERED/MISSING/DROP, hỗ trợ T11, T12

**Cách dựng bảng 1:** ghép chính xác chuỗi mô tả (`Desc`) của từng lệnh gọi `Check(condition, "<mô tả>", failures)` trong `PhotoReview.Tests/Program.cs` với `DisplayName` của `[Fact]`/`[Theory]` trong `PhotoReview.Tests.Unit/*.cs`, bằng khớp chuỗi **chính xác** (exact string match), không suy đoán hay đổi tên. Định nghĩa `static void Check(...)` ở dòng 563 của `Program.cs` không phải là một check và không được đếm. Toàn bộ 146 check tìm được trong `Program.cs` đều khớp được với đúng một test xUnit tồn tại thật (đã xác minh bằng script ở cuối tài liệu) — không có `MISSING`, không có `DROP` trong Bảng 1.

---

## Bảng 1: CLI Checks — Parity Status (146 dòng)

| # | Dòng | Mô tả | Test xUnit tương ứng | Trạng thái |
|---|------|-------|----------------------|-----------|
| 1 | 100 | Expanded benchmark profile registry | BenchmarkProfileTests::ExpandedBenchmarkProfileRegistry | COVERED |
| 2 | 101 | Original is correctness-only | BenchmarkProfileTests::OriginalIsCorrectnessOnly | COVERED |
| 3 | 102 | Speed ranking excludes correctness-only profile | BenchmarkProfileTests::SpeedRankingExcludesCorrectnessOnlyProfile | COVERED |
| 4 | 107 | Relative performance samples complete without hard timing failure | BenchmarkProfileTests::RelativePerformanceSamplesCompleteWithoutHardTimingFailure | COVERED |
| 5 | 112 | Logging defaults off | AppLogTests::LoggingDefaultsOff | COVERED |
| 6 | 114 | Existing config without logging flag keeps logging off | AppLogTests::ExistingConfigWithoutLoggingFlagKeepsLoggingOff | COVERED |
| 7 | 117 | Disabled logging creates no directory or file, including errors | AppLogTests::DisabledLoggingCreatesNoDirectoryOrFile | COVERED |
| 8 | 123 | Explicitly enabled logging writes diagnostics | AppLogTests::ExplicitlyEnabledLoggingWritesDiagnostics | COVERED |
| 9 | 127 | Turning logging off stops all diagnostic writes | AppLogTests::TurningLoggingOffStopsAllDiagnosticWrites | COVERED |
| 10 | 134 | Concurrent logging preserves every entry | AppLogTests::ConcurrentLoggingPreservesEveryEntry | COVERED |
| 11 | 135 | Concurrent log entries remain line-delimited | AppLogTests::ConcurrentLogEntriesRemainLineDelimited | COVERED |
| 12 | 143 | Session save/load | AppLogTests::SessionSaveAndLoad | COVERED |
| 13 | 156 | Journal committed Move | OperationJournalTests::JournalCommittedMove | COVERED |
| 14 | 157 | Journal has no pending committed Move | OperationJournalTests::JournalHasNoPendingCommittedMove | COVERED |
| 15 | 159 | Journal entries are durably written as JSONL | OperationJournalTests::JournalEntriesAreDurablyWrittenAsJsonl | COVERED |
| 16 | 161 | Journal readers tolerate an invalid JSONL line | OperationJournalTests::JournalReadersTolerateInvalidJsonlLine | COVERED |
| 17 | 163 | Journal concurrent append/read remains line-consistent | OperationJournalTests::JournalConcurrentAppendReadRemainsLineConsistent | COVERED |
| 18 | 164 | Move preserves bytes | OperationJournalTests::MovePreservesBytes | COVERED |
| 19 | 169 | LRU retains recently accessed entry | ServiceBehaviorTests::LruRetainsRecentlyAccessedEntry | COVERED |
| 20 | 171 | LRU evicts least recently used entry | ServiceBehaviorTests::LruEvictsLeastRecentlyUsedEntry | COVERED |
| 21 | 172 | LRU enforces byte capacity | ServiceBehaviorTests::LruEnforcesByteCapacity | COVERED |
| 22 | 193 | Interleaved actions advance viewer before filesystem operation and exactly once (source presence, not behavior) | SourcePresenceTests::InterleavedActionsAdvanceViewerBeforeFilesystemOperation | COVERED |
| 23 | 197 | Interleaved actions reject duplicate concurrent file actions (source presence; FileActionConcurrencyTests covers the behavior) | SourcePresenceTests::InterleavedActionsRejectDuplicateConcurrentFileActions | COVERED |
| 24 | 200 | LoadingMode defaults to Preview | AppSettingsTests::LoadingModeDefaultsToPreview | COVERED |
| 25 | 201 | LoadingMode has Fast, Preview, and Original options and accepts them case-insensitively | AppSettingsTests::LoadingModeHasFastPreviewOriginalOptions | COVERED |
| 26 | 204 | LoadingMode validation rejects unknown, null, and empty values | AppSettingsTests::LoadingModeValidationRejectsUnknownNullAndEmpty | COVERED |
| 27 | 206 | LoadingMode normalization always yields a supported mode for corrupt config values | AppSettingsTests::LoadingModeNormalizationAlwaysYieldsSupportedMode | COVERED |
| 28 | 208 | ImageSortMode defaults to Name, validates known modes, and normalizes unknown ones | AppSettingsTests::ImageSortModeDefaultsValidatesAndNormalizes | COVERED |
| 29 | 212 | MainWindow reads LoadingMode (source presence, not behavior) | SourcePresenceTests::MainWindowReadsLoadingMode | COVERED |
| 30 | 213 | MainWindow loading modes have thumbnail/preview contract (source presence, not behavior) | SourcePresenceTests::MainWindowLoadingModesHaveThumbnailPreviewContract | COVERED |
| 31 | 218 | Keyboard navigation uses arrow keys (defaults asserted; handler wiring is source presence) | SourcePresenceTests::KeyboardNavigationUsesArrowKeys | COVERED |
| 32 | 219 | Enter/action profiles drive configurable operations (defaults asserted; handler wiring is source presence) | SourcePresenceTests::EnterActionProfilesDriveConfigurableOperations | COVERED |
| 33 | 220 | Delete maps to Recycle Bin (default asserted; handler wiring is source presence) | SourcePresenceTests::DeleteMapsToRecycleBin | COVERED |
| 34 | 223 | File actions advance viewer before filesystem operation (source presence, not behavior) | SourcePresenceTests::FileActionsAdvanceViewerBeforeFilesystemOperation | COVERED |
| 35 | 224 | File actions snapshot source and next paths (source presence, not behavior) | SourcePresenceTests::FileActionsSnapshotSourceAndNextPaths | COVERED |
| 36 | 225 | Filesystem action is detached from UI thread (source presence, not behavior) | SourcePresenceTests::FilesystemActionIsDetachedFromUiThread | COVERED |
| 37 | 226 | File action completion does not advance twice (source presence, not behavior) | SourcePresenceTests::FileActionCompletionDoesNotAdvanceTwice | COVERED |
| 38 | 227 | No number-1 shortcut required | SourcePresenceTests::NoNumberOneShortcutRequired | COVERED |
| 39 | 228 | Space skip shortcut exists (source presence, not behavior) | SourcePresenceTests::SpaceSkipShortcutExists | COVERED |
| 40 | 229 | Skip, undo, and fullscreen shortcuts are configurable (source presence, not behavior) | SourcePresenceTests::SkipUndoFullscreenShortcutsAreConfigurable | COVERED |
| 41 | 230 | Shortcut conflicts are validated across global and action bindings (source presence; the validator itself is asserted behaviorally below) | SourcePresenceTests::ShortcutConflictsAreValidatedAcrossScopes | COVERED |
| 42 | 233 | Shortcut validator reports cross-scope conflicts | ServiceBehaviorTests::ShortcutValidatorReportsCrossScopeConflicts | COVERED |
| 43 | 234 | Configured shortcuts navigate sibling folders (source presence; SiblingFolderService is asserted behaviorally below) | SourcePresenceTests::ConfiguredShortcutsNavigateSiblingFolders | COVERED |
| 44 | 240 | Sibling folder navigation uses natural order | ServiceBehaviorTests::SiblingFolderNavigationUsesNaturalOrder | COVERED |
| 45 | 241 | Home navigates to first image (source presence, not behavior) | SourcePresenceTests::HomeNavigatesToFirstImage | COVERED |
| 46 | 242 | Configurable navigation and zoom shortcuts are wired at runtime (source presence, not behavior) | SourcePresenceTests::ConfigurableNavigationAndZoomShortcutsAreWired | COVERED |
| 47 | 248 | Drag-drop folder parser | ServiceBehaviorTests::DragDropFolderParser | COVERED |
| 48 | 249 | Drag-drop image selects initial image | ServiceBehaviorTests::DragDropImageSelectsInitialImage | COVERED |
| 49 | 250 | Drag-drop rejects unsupported input | ServiceBehaviorTests::DragDropRejectsUnsupportedInput | COVERED |
| 50 | 256 | Drag-drop events are wired on the main window (source presence; DragDropInputService is asserted behaviorally above) | SourcePresenceTests::DragDropEventsAreWiredOnMainWindow | COVERED |
| 51 | 257 | Compare shortcut toggles compare panel (source presence, not behavior) | SourcePresenceTests::CompareShortcutTogglesComparePanel | COVERED |
| 52 | 258 | Compare hash is optional (source presence, not behavior) | SourcePresenceTests::CompareHashIsOptional | COVERED |
| 53 | 259 | Compare size is optional (source presence, not behavior) | SourcePresenceTests::CompareSizeIsOptional | COVERED |
| 54 | 260 | Compare hashes are requested concurrently (source presence; FileHashService concurrency is asserted behaviorally below) | SourcePresenceTests::CompareHashesAreRequestedConcurrently | COVERED |
| 55 | 261 | Settings defaults reset compare options (source presence, not behavior) | SourcePresenceTests::SettingsDefaultsResetCompareOptions | COVERED |
| 56 | 268 | Recovery retry validates fingerprint and journals success | OperationJournalTests::RecoveryRetryValidatesFingerprintAndJournalsSuccess | COVERED |
| 57 | 269 | Recovery retry rejects invalid source state | OperationJournalTests::RecoveryRetryRejectsInvalidSourceState | COVERED |
| 58 | 270 | Recovery retry is exposed with confirmation in UI (source presence, not behavior) | SourcePresenceTests::RecoveryRetryIsExposedWithConfirmation | COVERED |
| 59 | 271 | Recovery retry button is accessible (source presence, not behavior) | SourcePresenceTests::RecoveryRetryButtonIsAccessible | COVERED |
| 60 | 272 | Name sort uses Windows Explorer logical ordering (source presence; natural ordering is asserted behaviorally below) | SourcePresenceTests::NameSortUsesExplorerLogicalOrdering | COVERED |
| 61 | 273 | Size sort is configurable (source presence; size ordering is asserted behaviorally below) | SourcePresenceTests::SizeSortIsConfigurable | COVERED |
| 62 | 274 | Size sort direction is configurable (source presence; both directions are asserted behaviorally below) | SourcePresenceTests::SizeSortDirectionIsConfigurable | COVERED |
| 63 | 275 | Image sorting is isolated in a testable service (source presence, not behavior) | SourcePresenceTests::ImageSortingIsIsolatedInTestableService | COVERED |
| 64 | 279 | Compare pair detection works from numbered filename | ServiceBehaviorTests::ComparePairFromNumberedFilename | COVERED |
| 65 | 281 | Compare pair detection works from original filename | ServiceBehaviorTests::ComparePairFromOriginalFilename | COVERED |
| 66 | 284 | Compare pair detection stays within selected folder | ServiceBehaviorTests::ComparePairStaysWithinSelectedFolder | COVERED |
| 67 | 285 | Compare pair detection rejects an incomplete pair | ServiceBehaviorTests::ComparePairRejectsIncompletePair | COVERED |
| 68 | 288 | Natural filename sort orders numeric suffixes | ServiceBehaviorTests::NaturalFilenameSortOrdersNumericSuffixes | COVERED |
| 69 | 290 | Natural filename sort handles numeric runs over 12 digits | ServiceBehaviorTests::NaturalFilenameSortHandlesLongNumericRuns | COVERED |
| 70 | 293 | Size sort orders files by descending bytes | ServiceBehaviorTests::SizeSortOrdersFilesByDescendingBytes | COVERED |
| 71 | 295 | Size sort orders files by ascending bytes | ServiceBehaviorTests::SizeSortOrdersFilesByAscendingBytes | COVERED |
| 72 | 301 | Explorer snapshot accepts a complete native order | ServiceBehaviorTests::ExplorerSnapshotAcceptsCompleteNativeOrder | COVERED |
| 73 | 302 | Explorer snapshot rejects missing images | ServiceBehaviorTests::ExplorerSnapshotRejectsMissingImages | COVERED |
| 74 | 303 | Explorer snapshot rejects duplicate paths | ServiceBehaviorTests::ExplorerSnapshotRejectsDuplicatePaths | COVERED |
| 75 | 304 | Explorer snapshot rejects paths outside the folder | ServiceBehaviorTests::ExplorerSnapshotRejectsPathsOutsideFolder | COVERED |
| 76 | 305 | Explorer snapshot exposes provider fallback reason | ServiceBehaviorTests::ExplorerSnapshotExposesProviderFallbackReason | COVERED |
| 77 | 307 | Explorer provider contract is fakeable without COM | ServiceBehaviorTests::ExplorerProviderContractIsFakeableWithoutCom | COVERED |
| 78 | 312 | Explorer order applies below and above 100 files and rejects stale folder results (source presence, not behavior) | SourcePresenceTests::ExplorerOrderAppliesRegardlessOfCountAndRejectsStaleResults | COVERED |
| 79 | 313 | Direct file open waits for complete Explorer snapshot and folder size before first preload (source presence, not behavior) | SourcePresenceTests::DirectFileOpenWaitsForSnapshotAndFolderSize | COVERED |
| 80 | 314 | Folder open replaces untouched fallback with the first native Explorer item (source presence, not behavior) | SourcePresenceTests::FolderOpenReplacesUntouchedFallback | COVERED |
| 81 | 315 | Native reindex refreshes counter and preload without duplicate render (source presence, not behavior) | SourcePresenceTests::NativeReindexRefreshesCounterAndPreload | COVERED |
| 82 | 316 | Explorer snapshot cannot reindex after user catalog interaction (source presence, not behavior) | SourcePresenceTests::ExplorerSnapshotCannotReindexAfterCatalogInteraction | COVERED |
| 83 | 317 | Navigation and file actions advance catalog interaction generation (source presence, not behavior) | SourcePresenceTests::NavigationAndFileActionsAdvanceCatalogInteractionGeneration | COVERED |
| 84 | 318 | Actions and batch operations require confirmation (source presence; ShowDialog needs a real WPF Window) | SourcePresenceTests::ActionsAndBatchOperationsRequireConfirmation | COVERED |
| 85 | 321 | Config supports multiple review actions with distinct shortcuts | AppSettingsTests::ConfigSupportsMultipleReviewActionsWithDistinctShortcuts | COVERED |
| 86 | 324 | Config carries an explicit version that round-trips through JSON | AppSettingsTests::ConfigCarriesExplicitVersionThatRoundTrips | COVERED |
| 87 | 329 | Config has versioned migration and durable atomic save (source presence, not behavior) | SourcePresenceTests::ConfigHasVersionedMigrationAndDurableAtomicSave | COVERED |
| 88 | 330 | RAM cache policy targets 16 GB and full-folder preload threshold (source presence: AppConstants is internal to the app assembly) | SourcePresenceTests::RamCachePolicyTargetsSixteenGbAndPreloadThreshold | COVERED |
| 89 | 331 | Hash service is isolated with bounded cache lifecycle (source presence; FileHashService is asserted behaviorally below) | SourcePresenceTests::HashServiceIsIsolatedWithBoundedCacheLifecycle | COVERED |
| 90 | 333 | Image click does not navigate; compare owns click selection (source absence, not behavior) | SourcePresenceTests::ImageClickDoesNotNavigate | COVERED |
| 91 | 334 | Compare selection drives file actions (source presence, not behavior) | SourcePresenceTests::CompareSelectionDrivesFileActions | COVERED |
| 92 | 346 | File hash service caches and invalidates by file fingerprint | ServiceBehaviorTests::FileHashServiceCachesAndInvalidates | COVERED |
| 93 | 348 | File hash service deduplicates concurrent reads | ServiceBehaviorTests::FileHashServiceDeduplicatesConcurrentReads | COVERED |
| 94 | 349 | Compare selection resets on navigation (source presence, not behavior) | SourcePresenceTests::CompareSelectionResetsOnNavigation | COVERED |
| 95 | 350 | Context-menu Undo and Escape exit are wired (source presence, not behavior) | SourcePresenceTests::ContextMenuUndoAndEscapeExitAreWired | COVERED |
| 96 | 351 | Delete Undo restores through Recycle Bin Shell (source presence: restoring needs the real Recycle Bin) | SourcePresenceTests::DeleteUndoRestoresThroughRecycleBinShell | COVERED |
| 97 | 352 | Primary controls expose accessible names (source presence, not behavior) | SourcePresenceTests::PrimaryControlsExposeAccessibleNames | COVERED |
| 98 | 353 | Settings exposes an accessible Open log location control (source presence, not behavior) | SourcePresenceTests::SettingsExposesAccessibleOpenLogLocationControl | COVERED |
| 99 | 354 | Open log location follows the configured AppLog path (source presence, not behavior) | SourcePresenceTests::OpenLogLocationFollowsConfiguredAppLogPath | COVERED |
| 100 | 355 | Compare previews expose accessible selection names (source presence, not behavior) | SourcePresenceTests::ComparePreviewsExposeAccessibleSelectionNames | COVERED |
| 101 | 356 | Compare previews wire up keyboard selection handlers (source presence, not behavior) | SourcePresenceTests::ComparePreviewsWireUpKeyboardSelectionHandlers | COVERED |
| 102 | 374 | Background preload memory guard decodes nothing when there is no memory headroom | PreviewImageServiceTests::MemoryGuardDecodesNothingWithoutHeadroom | COVERED |
| 103 | 383 | Background preload warms the cache around the current index when memory headroom allows | PreviewImageServiceTests::PreloadWarmsCacheAroundCurrentIndex | COVERED |
| 104 | 385 | Background preload prioritizes the next image after the current index | PreviewImageServiceTests::PreloadPrioritizesNextImage | COVERED |
| 105 | 390 | Cancelling preload does not poison the scheduler; the next request starts a fresh lifetime | PreviewImageServiceTests::CancellingPreloadDoesNotPoisonScheduler | COVERED |
| 106 | 393 | Clearing preloaded keys drops every warmed-key record | PreviewImageServiceTests::ClearingPreloadedKeysDropsEveryRecord | COVERED |
| 107 | 413 | A preloaded key is reported once and then consumed so a hit is not counted twice | PreviewImageServiceTests::PreloadedKeyIsReportedOnceThenConsumed | COVERED |
| 108 | 418 | Window shutdown disposes preload and thumbnail resources (source presence, not behavior) | SourcePresenceTests::WindowShutdownDisposesPreloadAndThumbnailResources | COVERED |
| 109 | 424 | Main window restores and saves native placement instead of always using the startup default (source presence, not behavior) | SourcePresenceTests::MainWindowRestoresAndSavesNativePlacement | COVERED |
| 110 | 425 | Main window does not force maximized state in XAML (source absence, not behavior) | SourcePresenceTests::MainWindowDoesNotForceMaximizedState | COVERED |
| 111 | 426 | Image uses the full client area while toolbar remains a compact overlay (source presence, not behavior) | SourcePresenceTests::ImageUsesFullClientAreaWithCompactToolbarOverlay | COVERED |
| 112 | 427 | Current folder is shown in the native window title bar (source presence, not behavior) | SourcePresenceTests::CurrentFolderIsShownInNativeTitleBar | COVERED |
| 113 | 428 | Each build exposes a unique informational build stamp in Settings (source presence, not behavior) | SourcePresenceTests::EachBuildExposesUniqueInformationalBuildStamp | COVERED |
| 114 | 429 | Native window placement restores after Loaded and persists monitor, bounds, and maximized state (source presence: needs a real Window handle) | SourcePresenceTests::NativeWindowPlacementRestoresAndPersists | COVERED |
| 115 | 430 | Saved placement is rejected when its monitor is no longer connected (source presence: needs real multi-monitor hardware) | SourcePresenceTests::SavedPlacementIsRejectedWhenMonitorIsGone | COVERED |
| 116 | 435 | Disk thumbnail cache has quota and clear operation (source presence, not behavior) | SourcePresenceTests::DiskThumbnailCacheHasQuotaAndClearOperation | COVERED |
| 117 | 436 | Disk cache cleanup tolerates filesystem access failures (source presence, not behavior) | SourcePresenceTests::DiskCacheCleanupToleratesFilesystemAccessFailures | COVERED |
| 118 | 437 | Disk cache can be cleared from UI without changing source images (source presence, not behavior) | SourcePresenceTests::DiskCacheCanBeClearedFromUi | COVERED |
| 119 | 448 | Preview decode records exactly one source read with the real source byte count | PreviewImageServiceTests::PreviewDecodeRecordsExactlyOneSourceRead | COVERED |
| 120 | 454 | Source byte metrics exclude cache deliveries and the cache returns the same decoded bitmap | PreviewImageServiceTests::SourceByteMetricsExcludeCacheDeliveries | COVERED |
| 121 | 458 | Evicting a path drops its decoded bitmap from the preview cache | PreviewImageServiceTests::EvictingPathDropsDecodedBitmap | COVERED |
| 122 | 461 | Eviction forces a fresh source read on the next request | PreviewImageServiceTests::EvictionForcesFreshSourceReadOnNextRequest | COVERED |
| 123 | 464 | Clearing the preview cache drops every decoded bitmap | PreviewImageServiceTests::ClearingPreviewCacheDropsEveryDecodedBitmap | COVERED |
| 124 | 468 | Original loading mode decodes at full size while Preview mode uses the target decode width | PreviewImageServiceTests::OriginalLoadingModeDecodesAtFullSize | COVERED |
| 125 | 474 | Original dimensions are read from the real source header | PreviewImageServiceTests::OriginalDimensionsAreReadFromRealSourceHeader | COVERED |
| 126 | 478 | Disk-cache deliveries are excluded from source byte metrics (source presence: priming the real disk cache is out of scope) | SourcePresenceTests::DiskCacheDeliveriesAreExcludedFromSourceByteMetrics | COVERED |
| 127 | 481 | Viewer present latency is recorded and surfaced in diagnostics (source presence, not behavior) | SourcePresenceTests::ViewerPresentLatencyIsRecordedAndSurfaced | COVERED |
| 128 | 487 | Review metrics snapshot preserves counters | ServiceBehaviorTests::ReviewMetricsSnapshotPreservesCounters | COVERED |
| 129 | 497 | Concurrent preload diagnostics retain every delivery and timing event | ServiceBehaviorTests::ConcurrentPreloadDiagnosticsRetainEveryEvent | COVERED |
| 130 | 505 | Decoded cache identity separates resize and Original quality | ImageCacheKeyTests::DecodedCacheIdentitySeparatesResizeAndOriginalQuality | COVERED |
| 131 | 509 | Replacing a source at the same path invalidates its decoded bitmap | ImageCacheKeyTests::ReplacingSourceAtSamePathInvalidatesDecodedBitmap | COVERED |
| 132 | 512 | Full-folder preload prioritizes the next 32, then prior 8, and queues every other image once | ServiceBehaviorTests::FullFolderPreloadPrioritizesNextThenPrior | COVERED |
| 133 | 516 | Navigating changes preload priority to the new Next without duplicate jobs | ServiceBehaviorTests::NavigatingChangesPreloadPriority | COVERED |
| 134 | 519 | Performance metrics have an in-app diagnostics view (source presence, not behavior) | SourcePresenceTests::PerformanceMetricsHaveInAppDiagnosticsView | COVERED |
| 135 | 520 | Batch duplicate operation has dry-run review dialog (source presence; ShowDialog needs a real WPF Window) | SourcePresenceTests::BatchDuplicateOperationHasDryRunReviewDialog | COVERED |
| 136 | 521 | Recovery UI exposes pending operations without replay (source presence; journal reconciliation is asserted behaviorally below) | SourcePresenceTests::RecoveryUiExposesPendingOperationsWithoutReplay | COVERED |
| 137 | 522 | Recovery UI includes failed journal operations (source presence, not behavior) | SourcePresenceTests::RecoveryUiIncludesFailedJournalOperations | COVERED |
| 138 | 523 | Batch operations journal success and failures (source presence; OperationJournal is asserted behaviorally below) | SourcePresenceTests::BatchOperationsJournalSuccessAndFailures | COVERED |
| 139 | 529 | Pending recycle is reconciled without replay when source remains | OperationJournalTests::PendingRecycleIsReconciledWithoutReplay | COVERED |
| 140 | 537 | Pending move is committed only when source is absent and destination fingerprint matches | OperationJournalTests::PendingMoveIsCommittedWhenSourceAbsentAndFingerprintMatches | COVERED |
| 141 | 544 | Pending copy with mismatched destination is failed without replay | OperationJournalTests::PendingCopyWithMismatchedDestinationIsFailed | COVERED |
| 142 | 552 | Pending move with source still present is failed without replay | OperationJournalTests::PendingMoveWithSourceStillPresentIsFailed | COVERED |
| 143 | 556 | File association command is registered (source presence: registering needs the real Windows registry) | SourcePresenceTests::FileAssociationCommandIsRegistered | COVERED |
| 144 | 563 | Sequence Next then Move keeps next image without skipping | ServiceBehaviorTests::SequenceNextThenMoveKeepsNextImage | COVERED |
| 145 | 592 | Sequence Next then Delete keeps next image without skipping | ServiceBehaviorTests::SequenceNextThenDeleteKeepsNextImage | COVERED |
| 146 | 600 | Sequence Delete at end selects the prior surviving slot | ServiceBehaviorTests::SequenceDeleteAtEndSelectsPriorSurvivingSlot | COVERED |

---

## Bảng 2: 5 file trùng giữa `PhotoReview.Tests` (CLI) và `PhotoReview.Tests.Unit` (xUnit)

Phương pháp: `git diff --no-index -w PhotoReview.Tests/<File>.cs PhotoReview.Tests.Unit/<File>.cs`, đối chiếu số `Check(...)` (CLI, trừ định nghĩa hàm `Check`) với số `[Fact]` (xUnit), và `grep` `Program.cs` xem file CLI còn được gọi ở đâu.

### BenchmarkScenarioTests
- **CLI** (`PhotoReview.Tests/BenchmarkScenarioTests.cs`): lớp `static`, `Run(root, failures)`, **5** `Check(...)` thật (không tính định nghĩa `Check`) + 1 method `CancellationStopsAsync(string root)` không được gọi ở đâu trong `PhotoReview.Tests` (grep xác nhận chỉ có định nghĩa, không có lời gọi) — code chết.
- **xUnit** (`PhotoReview.Tests.Unit/BenchmarkScenarioTests.cs`): lớp `sealed ... : IDisposable`, dùng `TempRoot`, **5** `[Fact]` (`AdvancesExactlyOnceBeforeMove/Delete/Copy`, `InterleavedSequenceHasNoDoubleNavigation`, `PerformsNoRetrySideEffect`) khớp đúng 1-1 nội dung 5 check của CLI (chỉ đổi diễn đạt từ 1 kịch bản tuần tự sang state được chụp trong constructor rồi assert riêng từng Fact).
- **Program.cs**: gọi `BenchmarkScenarioTests.Run(root, failures);` ở **dòng 99**.
- **Kết luận T12**: xóa được `PhotoReview.Tests/BenchmarkScenarioTests.cs` (bao gồm cả `CancellationStopsAsync` chết) và bỏ lời gọi dòng 99 trong `Program.cs`.

### CacheExplorerRegressionTests
- **CLI** (`PhotoReview.Tests/CacheExplorerRegressionTests.cs`): lớp `internal static`, `Run(root, failures)`, **12** `Check(...)` thật.
- **xUnit** (`PhotoReview.Tests.Unit/CacheExplorerRegressionTests.cs`): lớp `sealed ... : IDisposable`, **12** `[Fact]`, khớp 1-1 (bao gồm 2 check cuối đọc source `MainWindow.xaml.cs` qua `ProjectSources.MainWindow` — vẫn là source-presence nhưng đã có trong bộ test xUnit).
- **Program.cs**: gọi `CacheExplorerRegressionTests.Run(root, failures);` ở **dòng 98**.
- **Kết luận T12**: xóa được `PhotoReview.Tests/CacheExplorerRegressionTests.cs`, bỏ lời gọi dòng 98.

### FileActionConcurrencyTests
- **CLI** (`PhotoReview.Tests/FileActionConcurrencyTests.cs`): lớp `static`, `Run(root, failures)` gọi 4 sub-method, **4** `Check(...)` thật.
- **xUnit** (`PhotoReview.Tests.Unit/FileActionConcurrencyTests.cs`): lớp `sealed ... : IDisposable`, **4** `[Fact]`, khớp 1-1 (`MoveSucceedsWhileReadIsOpen`, `DeleteSucceedsWhileReadIsOpen`, `CopyPreservesSourceAndBytesWhileReadIsOpen`, `InterleavedSequenceLeavesDeterministicState`).
- **Program.cs**: gọi `FileActionConcurrencyTests.Run(root, failures);` ở **dòng 97**.
- **Kết luận T12**: xóa được `PhotoReview.Tests/FileActionConcurrencyTests.cs`, bỏ lời gọi dòng 97.

### ImageCacheKeyTests
- **CLI** (`PhotoReview.Tests/ImageCacheKeyTests.cs`): lớp `internal static`, `Run(root, failures)`, **4** `Check(...)` thật.
- **xUnit** (`PhotoReview.Tests.Unit/ImageCacheKeyTests.cs`): lớp `sealed ... : IDisposable`, **6** `[Fact]` — 4 test đầu (`CacheKeyReusesUnchangedSourceModeWidth`, `CacheKeySeparatesDecodeWidthAndOriginalMode`, `CacheKeyRejectsReplacementAtSamePath`, `CacheKeyRejectsRemovedSource`) khớp 1-1 với 4 check CLI; 2 test sau (`DecodedCacheIdentitySeparatesResizeAndOriginalQuality`, `ReplacingSourceAtSamePathInvalidatesDecodedBitmap`) là bản xUnit của 2 check **khác**, nằm trực tiếp trong `Program.cs` dòng 505 và 509 (xem Bảng 1, dòng #130–131), không thuộc file CLI trùng tên này.
- **Program.cs**: gọi `ImageCacheKeyTests.Run(root, failures);` ở **dòng 96**.
- **Kết luận T12**: xóa được `PhotoReview.Tests/ImageCacheKeyTests.cs`, bỏ lời gọi dòng 96. Không đụng tới 2 check inline ở dòng 505/509 (đã COVERED qua Bảng 1, độc lập với file này).

### PerformanceTestHarness
- **CLI** (`PhotoReview.Tests/PerformanceTestHarness.cs`, 101 dòng) và **xUnit** (`PhotoReview.Tests.Unit/PerformanceTestHarness.cs`, 101 dòng): diff chỉ khác `namespace PhotoReview.Tests;` ↔ `namespace PhotoReview.Tests.Unit;` — không phải bộ test, mà là **lớp hạ tầng benchmark dùng chung** (`CreateFixture`, `RunAsync`).
- **Bản CLI** được `Program.cs` dùng trực tiếp: `PerformanceTestHarness.CreateFixture(root, 30)` (**dòng 104**) và `PerformanceTestHarness.RunAsync(...)` (**dòng 105**), phục vụ `--benchmark`/CLI harness.
- **Bản xUnit** được `PhotoReview.Tests.Unit/BenchmarkProfileTests.cs` dùng (dòng 34–35) cho test `RelativePerformanceSamplesCompleteWithoutHardTimingFailure` (Bảng 1, #4).
- Không tìm thấy `LocalImageBenchmark.cs`/`LocalUiNextProbe.cs` gọi `PerformanceTestHarness` trong repo hiện tại (chỉ `Program.cs` và `BenchmarkProfileTests.cs` tham chiếu).
- **Kết luận T12**: hai bản chỉ khác namespace và bản CLI **vẫn được `Program.cs` dùng** → **giữ tới T50b** (T50b sẽ di chuyển `Program.cs` + benchmark CLI sang `tools/PhotoReview.Benchmark.Cli/`, lúc đó mới xử lý gộp/xóa trùng lặp này).

---

## Bảng 3: 69 test trong `SourcePresenceTests.cs` — phân loại

Cột **Dòng** là số dòng của attribute `[Fact(DisplayName = ...)]` trong `PhotoReview.Tests.Unit/SourcePresenceTests.cs`. `KEEP-XAML` = chỉ kiểm thuộc tính trong file `.xaml` (đã xác minh lại từng dòng; các test kiểm cả `.cs` code-behind không được xếp vào nhóm này). `DROP: <test>` = đã có test hành vi khác bao phủ cùng hợp đồng (tên test được xác minh tồn tại thật bằng `Select-String`, xem cuối tài liệu). `REPLACE-BY:<task>` dùng đúng danh sách được giao (T14b/c/d, T43a, T45b, T45c, T46a/b/c, T31a/b/T32/T33a) khi khớp; khi nội dung thuộc về một task khác đã xác nhận trong `REFACTOR-TASKS.md` (ví dụ T25a, T33b, T46d, T64), ghi task đó kèm lý do trong Ghi chú.

| Dòng | Method | DisplayName (rút gọn) | Phân loại | Ghi chú |
|---|---|---|---|---|
| 26 | InterleavedActionsAdvanceViewerBeforeFilesystemOperation | Interleaved actions advance viewer before filesystem operation and exactly once (source... | REPLACE-BY:T14b | Advance-before-action; T14b viết test thật qua STA harness (INV-3/4) |
| 32 | InterleavedActionsRejectDuplicateConcurrentFileActions | Interleaved actions reject duplicate concurrent file actions (source presence; FileActi... | REPLACE-BY:T14b | Chặn action đồng thời (INV-4) |
| 36 | MainWindowReadsLoadingMode | MainWindow reads LoadingMode (source presence, not behavior) | REPLACE-BY:T45c | LoadingMode dùng trong ImagePresenter (bước 4) |
| 40 | MainWindowLoadingModesHaveThumbnailPreviewContract | MainWindow loading modes have thumbnail/preview contract (source presence, not behavior) | REPLACE-BY:T45c | Hợp đồng thumbnail/preview chuyển vào ImagePresenter |
| 49 | KeyboardNavigationUsesArrowKeys | Keyboard navigation uses arrow keys (defaults asserted; handler wiring is source presence) | REPLACE-BY:T43a | Wiring phím mũi tên → ShortcutRouter.TryResolve |
| 58 | EnterActionProfilesDriveConfigurableOperations | Enter/action profiles drive configurable operations (defaults asserted; handler wiring ... | REPLACE-BY:T43a | Enter/ReviewAction là ReviewCommand.RunAction trong ShortcutRouter |
| 63 | DeleteMapsToRecycleBin | Delete maps to Recycle Bin (default asserted; handler wiring is source presence) | REPLACE-BY:T43a | Delete → ReviewCommand.Recycle |
| 71 | FileActionsAdvanceViewerBeforeFilesystemOperation | File actions advance viewer before filesystem operation (source presence, not behavior) | REPLACE-BY:T14b | Cùng nhóm advance-before-action |
| 75 | FileActionsSnapshotSourceAndNextPaths | File actions snapshot source and next paths (source presence, not behavior) | REPLACE-BY:T14b | Snapshot sourcePath/nextPath là 1 phần cơ chế advance-before-action |
| 80 | FilesystemActionIsDetachedFromUiThread | Filesystem action is detached from UI thread (source presence, not behavior) | REPLACE-BY:T14b | Task.Run tách UI thread, cùng cơ chế advance-before-action |
| 85 | FileActionCompletionDoesNotAdvanceTwice | File action completion does not advance twice (source presence, not behavior) | REPLACE-BY:T14b | INV-4: chỉ present một lần |
| 90 | NoNumberOneShortcutRequired | No number-1 shortcut required | REPLACE-BY:T43a | Default mapping, ShortcutRouterTests bao phủ |
| 94 | SpaceSkipShortcutExists | Space skip shortcut exists (source presence, not behavior) | REPLACE-BY:T43a | Shortcuts.Skip wiring |
| 98 | SkipUndoFullscreenShortcutsAreConfigurable | Skip, undo, and fullscreen shortcuts are configurable (source presence, not behavior) | REPLACE-BY:T43a | Skip/Undo/Fullscreen là ReviewCommand |
| 104 | ShortcutConflictsAreValidatedAcrossScopes | Shortcut conflicts are validated across global and action bindings (source presence; th... | REPLACE-BY:T43a | Wiring gọi validator; bản thân validator đã có ServiceBehaviorTests::ShortcutValidatorReportsCrossScopeConflicts |
| 108 | ConfiguredShortcutsNavigateSiblingFolders | Configured shortcuts navigate sibling folders (source presence; SiblingFolderService is... | REPLACE-BY:T46a | Sibling folder navigation qua MainViewModel.NextFolder/PreviousFolder |
| 113 | HomeNavigatesToFirstImage | Home navigates to first image (source presence, not behavior) | REPLACE-BY:T46a | MainViewModel.First |
| 117 | ConfigurableNavigationAndZoomShortcutsAreWired | Configurable navigation and zoom shortcuts are wired at runtime (source presence, not b... | REPLACE-BY:T46a | Navigation+zoom command wiring lúc runtime |
| 128 | DragDropEventsAreWiredOnMainWindow | Drag-drop events are wired on the main window (source presence; DragDropInputService is... | REPLACE-BY:T46a | Drag-drop; DragDropInputService đã có test hành vi, còn thiếu wiring cửa sổ (kiểm cả .cs nên không phải KEEP-XAML thuần) |
| 133 | CompareShortcutTogglesComparePanel | Compare shortcut toggles compare panel (source presence, not behavior) | REPLACE-BY:T45b | CompareViewModel.Toggle/IsVisible |
| 137 | CompareHashIsOptional | Compare hash is optional (source presence, not behavior) | REPLACE-BY:T45b | CompareViewModel.LoadAsync tham số getHash tùy chọn |
| 142 | CompareSizeIsOptional | Compare size is optional (source presence, not behavior) | REPLACE-BY:T45b | CompareViewModel size tùy chọn |
| 147 | CompareHashesAreRequestedConcurrently | Compare hashes are requested concurrently (source presence; FileHashService concurrency... | REPLACE-BY:T45b | CompareViewModel.LoadAsync hash song song |
| 152 | SettingsDefaultsResetCompareOptions | Settings defaults reset compare options (source presence, not behavior) | REPLACE-BY:T45b | Compare option defaults; hiện nằm ở SettingsWindow, T45b sẽ test CompareViewModel defaults |
| 157 | RecoveryRetryIsExposedWithConfirmation | Recovery retry is exposed with confirmation in UI (source presence, not behavior) | REPLACE-BY:T46c | IDialogService.ShowRecovery |
| 162 | RecoveryRetryButtonIsAccessible | Recovery retry button is accessible (source presence, not behavior) | KEEP-XAML | Chỉ AutomationProperties.Name trong RecoveryWindow.xaml (đề xuất gộp, xem Tổng kết) |
| 167 | NameSortUsesExplorerLogicalOrdering | Name sort uses Windows Explorer logical ordering (source presence; natural ordering is ... | DROP: ServiceBehaviorTests::NaturalFilenameSortOrdersNumericSuffixes, ServiceBehaviorTests::NaturalFilenameSortHandlesLongNumericRuns | Comment gốc: "natural ordering asserted behaviorally below" |
| 172 | SizeSortIsConfigurable | Size sort is configurable (source presence; size ordering is asserted behaviorally below) | DROP: ServiceBehaviorTests::SizeSortOrdersFilesByDescendingBytes, ServiceBehaviorTests::SizeSortOrdersFilesByAscendingBytes | Comment gốc: "size ordering asserted behaviorally below" |
| 178 | SizeSortDirectionIsConfigurable | Size sort direction is configurable (source presence; both directions are asserted beha... | DROP: ServiceBehaviorTests::SizeSortOrdersFilesByDescendingBytes, ServiceBehaviorTests::SizeSortOrdersFilesByAscendingBytes | Comment gốc: "both directions asserted behaviorally below" |
| 182 | ImageSortingIsIsolatedInTestableService | Image sorting is isolated in a testable service (source presence, not behavior) | REPLACE-BY:T46a | Call site ImageSortService.Sort rời khỏi MainWindow khi T46a chuyển open/navigate sang MainViewModel (bản thân service đã chuyển ở T23a, ngoài phạm vi Files T10) |
| 191 | ExplorerOrderAppliesRegardlessOfCountAndRejectsStaleResults | Explorer order applies below and above 100 files and rejects stale folder results (sour... | REPLACE-BY:T14d | Explorer order + từ chối stale results |
| 197 | DirectFileOpenWaitsForSnapshotAndFolderSize | Direct file open waits for complete Explorer snapshot and folder size before first prel... | REPLACE-BY:T14d | INV-9a: mở file chờ snapshot |
| 202 | FolderOpenReplacesUntouchedFallback | Folder open replaces untouched fallback with the first native Explorer item (source pre... | REPLACE-BY:T14d | INV-9b: mở folder present trước, áp lại thứ tự sau |
| 206 | NativeReindexRefreshesCounterAndPreload | Native reindex refreshes counter and preload without duplicate render (source presence,... | REPLACE-BY:T14d | Explorer reindex counter/preload |
| 212 | ExplorerSnapshotCannotReindexAfterCatalogInteraction | Explorer snapshot cannot reindex after user catalog interaction (source presence, not b... | REPLACE-BY:T14d | INV-7: không đổi thứ tự sau tương tác |
| 217 | NavigationAndFileActionsAdvanceCatalogInteractionGeneration | Navigation and file actions advance catalog interaction generation (source presence, no... | REPLACE-BY:T14d | Generation gắn với cơ chế chặn Explorer reindex (INV-7) |
| 221 | ActionsAndBatchOperationsRequireConfirmation | Actions and batch operations require confirmation (source presence; ShowDialog needs a ... | REPLACE-BY:T46c | IDialogService.Confirm cho action/batch |
| 228 | ConfigHasVersionedMigrationAndDurableAtomicSave | Config has versioned migration and durable atomic save (source presence, not behavior) | REPLACE-BY:T25a | SettingsStore.Migrate + Save durable (T25a là chủ sở hữu thực tế theo REFACTOR-TASKS.md, ngoài danh sách ví dụ T14-T46) |
| 232 | RamCachePolicyTargetsSixteenGbAndPreloadThreshold | RAM cache policy targets 16 GB and full-folder preload threshold (source presence: AppC... | REPLACE-BY:T64 | RamBudgetPolicy mục tiêu 16GB (R-4a) |
| 237 | HashServiceIsIsolatedWithBoundedCacheLifecycle | Hash service is isolated with bounded cache lifecycle (source presence; FileHashService... | DROP: ServiceBehaviorTests::FileHashServiceCachesAndInvalidates, ServiceBehaviorTests::FileHashServiceDeduplicatesConcurrentReads | Comment gốc: "FileHashService asserted behaviorally below" |
| 242 | ImageClickDoesNotNavigate | Image click does not navigate; compare owns click selection (source absence, not behavior) | REPLACE-BY:T45b | Compare sở hữu click selection |
| 247 | CompareSelectionDrivesFileActions | Compare selection drives file actions (source presence, not behavior) | REPLACE-BY:T46b | Khớp bước 1 kế hoạch T46b: "chọn nguồn compare.SelectedPath ?? catalog.Current" |
| 252 | CompareSelectionResetsOnNavigation | Compare selection resets on navigation (source presence, not behavior) | REPLACE-BY:T45b | CompareViewModel.Clear() khi navigate |
| 257 | ContextMenuUndoAndEscapeExitAreWired | Context-menu Undo and Escape exit are wired (source presence, not behavior) | REPLACE-BY:T43a | Esc thoát fullscreen/đóng cửa sổ đúng mô tả T43a; Undo context-menu wiring đi kèm |
| 262 | DeleteUndoRestoresThroughRecycleBinShell | Delete Undo restores through Recycle Bin Shell (source presence: restoring needs the re... | REPLACE-BY:T33b | RecycleBinRestoreService/WindowsRecycleBin.TryRestore chuyển sang Platform |
| 267 | PrimaryControlsExposeAccessibleNames | Primary controls expose accessible names (source presence, not behavior) | KEEP-XAML | Chỉ AutomationProperties.Name trong MainWindow.xaml (đề xuất gộp) |
| 272 | SettingsExposesAccessibleOpenLogLocationControl | Settings exposes an accessible Open log location control (source presence, not behavior) | REPLACE-BY:T46c | Tính năng Settings |
| 277 | OpenLogLocationFollowsConfiguredAppLogPath | Open log location follows the configured AppLog path (source presence, not behavior) | REPLACE-BY:T46c | Tính năng Settings |
| 282 | ComparePreviewsExposeAccessibleSelectionNames | Compare previews expose accessible selection names (source presence, not behavior) | KEEP-XAML | Chỉ text trong MainWindow.xaml (đề xuất gộp) |
| 287 | ComparePreviewsWireUpKeyboardSelectionHandlers | Compare previews wire up keyboard selection handlers (source presence, not behavior) | REPLACE-BY:T45b | Compare keyboard selection (kiểm cả .cs handler nên không phải KEEP-XAML thuần) |
| 294 | WindowShutdownDisposesPreloadAndThumbnailResources | Window shutdown disposes preload and thumbnail resources (source presence, not behavior) | REPLACE-BY:T46c | Dispose resource khi đóng cửa sổ (phần còn lại của MainViewModel lifecycle) |
| 304 | MainWindowRestoresAndSavesNativePlacement | Main window restores and saves native placement instead of always using the startup def... | KEEP-XAML | Chỉ Loaded/Closing attribute trong MainWindow.xaml (đề xuất gộp) |
| 309 | MainWindowDoesNotForceMaximizedState | Main window does not force maximized state in XAML (source absence, not behavior) | KEEP-XAML | Chỉ absence check trong MainWindow.xaml (đề xuất gộp) |
| 313 | ImageUsesFullClientAreaWithCompactToolbarOverlay | Image uses the full client area while toolbar remains a compact overlay (source presenc... | KEEP-XAML | Chỉ layout attribute trong MainWindow.xaml (đề xuất gộp) |
| 318 | CurrentFolderIsShownInNativeTitleBar | Current folder is shown in the native window title bar (source presence, not behavior) | REPLACE-BY:T46a | "title" nằm trong danh mục T46a (kiểm cả .cs Title= nên không phải KEEP-XAML) |
| 323 | EachBuildExposesUniqueInformationalBuildStamp | Each build exposes a unique informational build stamp in Settings (source presence, not... | REPLACE-BY:T46c | Build stamp hiển thị trong Settings |
| 328 | NativeWindowPlacementRestoresAndPersists | Native window placement restores after Loaded and persists monitor, bounds, and maximiz... | REPLACE-BY:T46d | WindowPlacementService ở lại App (quyết định T33b); T46d giữ code-behind placement, vẫn cần real HWND nên vẫn là source-presence sau refactor — coordinator xác nhận thủ công |
| 336 | SavedPlacementIsRejectedWhenMonitorIsGone | Saved placement is rejected when its monitor is no longer connected (source presence: n... | REPLACE-BY:T46d | Cần multi-monitor thật; ghi chú như trên |
| 345 | DiskThumbnailCacheHasQuotaAndClearOperation | Disk thumbnail cache has quota and clear operation (source presence, not behavior) | REPLACE-BY:T31a | T31a sửa cả ThumbnailCache.cs (cách dùng DiskCacheStore) |
| 351 | DiskCacheCleanupToleratesFilesystemAccessFailures | Disk cache cleanup tolerates filesystem access failures (source presence, not behavior) | REPLACE-BY:T31a | ThumbnailCache trong phạm vi Files của T31a |
| 356 | DiskCacheCanBeClearedFromUi | Disk cache can be cleared from UI without changing source images (source presence, not ... | REPLACE-BY:T46c | ClearCache action trong MainViewModel |
| 362 | DiskCacheDeliveriesAreExcludedFromSourceByteMetrics | Disk-cache deliveries are excluded from source byte metrics (source presence: priming t... | REPLACE-BY:T31b | Chỉ kiểm ProjectSources.PreviewImageServiceSource, không đụng MainWindow |
| 368 | ViewerPresentLatencyIsRecordedAndSurfaced | Viewer present latency is recorded and surfaced in diagnostics (source presence, not be... | REPLACE-BY:T45c | ImagePresenter bước 7: "ghi metric Presented" |
| 374 | PerformanceMetricsHaveInAppDiagnosticsView | Performance metrics have an in-app diagnostics view (source presence, not behavior) | REPLACE-BY:T46c | ShowDiagnostics |
| 379 | BatchDuplicateOperationHasDryRunReviewDialog | Batch duplicate operation has dry-run review dialog (source presence; ShowDialog needs ... | REPLACE-BY:T46c | BatchReviewWindow qua dialog service |
| 383 | RecoveryUiExposesPendingOperationsWithoutReplay | Recovery UI exposes pending operations without replay (source presence; journal reconci... | REPLACE-BY:T46c | ShowRecovery |
| 387 | RecoveryUiIncludesFailedJournalOperations | Recovery UI includes failed journal operations (source presence, not behavior) | REPLACE-BY:T46c | ShowRecovery + journal thất bại |
| 392 | BatchOperationsJournalSuccessAndFailures | Batch operations journal success and failures (source presence; OperationJournal is ass... | REPLACE-BY:T46b | Recycle action journal Prepared/Committed/Failed |
| 397 | FileAssociationCommandIsRegistered | File association command is registered (source presence: registering needs the real Win... | KEEP-SCRIPT | Kiểm `outputs/install-photo-review-association.ps1`, không thuộc App/MainWindow, không có task refactor nào sở hữu trong `REFACTOR-TASKS.md`; đề xuất giữ nguyên vĩnh viễn, không tính vào KEEP-XAML (xem Tổng kết) |

---

## Tổng kết

### Bảng 1 (CLI checks, 146 dòng)
- COVERED: **146/146**. MISSING: **0**. DROP: **0**.
- Kết luận cho T11: không có việc phải làm — mọi check CLI đã có test xUnit khớp tên thật. T11 chỉ cần xác nhận lại (không cần viết `MigratedCliChecksTests.cs`).

### Bảng 2 (5 file trùng)
- **Xóa được ngay ở T12** (4 file): `BenchmarkScenarioTests.cs`, `CacheExplorerRegressionTests.cs`, `FileActionConcurrencyTests.cs`, `ImageCacheKeyTests.cs` trong `PhotoReview.Tests/` — cùng bỏ 4 lời gọi `.Run(root, failures)` ở `PhotoReview.Tests/Program.cs` dòng **96–99**.
- **Giữ tới T50b** (1 file): `PerformanceTestHarness.cs` — bản CLI vẫn được `Program.cs` dùng (dòng 104–105) cho harness benchmark; T50b sẽ di chuyển toàn bộ CLI benchmark sang `tools/PhotoReview.Benchmark.Cli/` và xử lý trùng lặp lúc đó.

### Bảng 3 (69 test SourcePresenceTests) — phân loại theo REPLACE-BY task
| Task | Số test |
|---|---|
| T14b | 6 |
| T14d | 6 |
| T43a | 8 |
| T45b | 8 |
| T45c | 3 |
| T46a | 6 |
| T46b | 2 |
| T46c | 11 |
| T31a | 2 |
| T31b | 1 |
| T33b | 1 |
| T46d | 2 |
| T25a | 1 |
| T64 | 1 |
| **Tổng REPLACE-BY** | **58** |
| DROP | 4 |
| KEEP-XAML | 6 |
| KEEP-SCRIPT | 1 |
| **Tổng** | **69** |

- **DROP (4):** `NameSortUsesExplorerLogicalOrdering`, `SizeSortIsConfigurable`, `SizeSortDirectionIsConfigurable`, `HashServiceIsIsolatedWithBoundedCacheLifecycle` — mỗi test đã ghi rõ tên test hành vi thay thế (đã xác minh tồn tại thật, xem script cuối tài liệu).
- **KEEP-XAML (6, vượt trần 5 test mà kế hoạch cho phép):** `RecoveryRetryButtonIsAccessible`, `PrimaryControlsExposeAccessibleNames`, `ComparePreviewsExposeAccessibleSelectionNames`, `MainWindowRestoresAndSavesNativePlacement`, `MainWindowDoesNotForceMaximizedState`, `ImageUsesFullClientAreaWithCompactToolbarOverlay`.
  - **Đề xuất gộp cho T47:** 3 test kiểm `AutomationProperties.Name` (`RecoveryRetryButtonIsAccessible`, `PrimaryControlsExposeAccessibleNames`, `ComparePreviewsExposeAccessibleSelectionNames`) có thể viết lại thành **một** test XAML duy nhất (`AccessibleNamesArePresentInXaml`, dùng `XDocument` để kiểm cả `MainWindow.xaml` lẫn `RecoveryWindow.xaml` trong một lần assert), đưa tổng số KEEP-XAML về **4** — nằm trong giới hạn plan cho phép (tối đa 5).
- **KEEP-SCRIPT (1, ngoài 3 nhóm chuẩn của T10):** `FileAssociationCommandIsRegistered` kiểm nội dung `outputs/install-photo-review-association.ps1` — một script cài đặt, không phải mã nguồn `PhotoReview.App`, và không có task refactor nào trong `REFACTOR-TASKS.md` sở hữu nó. Không xếp được vào `REPLACE-BY` (không có task), không phải `KEEP-XAML` (không kiểm `.xaml`), không phải `DROP` (không có test hành vi thay thế). Đề xuất: T47 giữ nguyên test này vĩnh viễn, không tính vào giới hạn KEEP-XAML của plan; nếu coordinator muốn nghiêm ngặt theo đúng 3 nhãn gốc, có thể coi nó là biến thể của `KEEP-XAML` (kiểm nội dung tệp tĩnh không phải `.cs` ứng dụng) nhưng nên ghi chú rõ trong T47.
- **MISSING:** không có (không có dòng nào trong Bảng 1 hay Bảng 3 thiếu ánh xạ) — T11 chỉ cần xác nhận lại kết quả này, không cần tạo test mới.
- **Kết luận cho T12** (nhắc lại, xem Bảng 2): xóa `BenchmarkScenarioTests.cs`, `CacheExplorerRegressionTests.cs`, `FileActionConcurrencyTests.cs`, `ImageCacheKeyTests.cs` trong `PhotoReview.Tests/` + bỏ 4 lời gọi dòng 96–99 trong `Program.cs`; **không** đụng `PerformanceTestHarness.cs`, `LocalImageBenchmark.cs`, `LocalUiNextProbe.cs`.

---

## Kiểm thử (script xác minh, chạy lại được)

1. **Số dòng bảng:** Bảng 1 = 146 dòng dữ liệu (khớp `parity.csv`, đếm bằng `Import-Csv`). Bảng 3 = 69 dòng dữ liệu (khớp `presence.csv`).
2. **Mọi `File::Method` trong Bảng 1 tồn tại thật** trong `PhotoReview.Tests.Unit/*.cs`:
   ```powershell
   $rows = Import-Csv parity.csv   # cột Line, Desc, Test="File::Method"
   foreach ($r in $rows) {
     $file, $method = $r.Test -split "::"
     Select-String -Pattern "void $method\b|Task $method\b" -Path "PhotoReview.Tests.Unit\$file.cs"
   }
   # Kết quả thực tế: 0 tên không tồn tại (146/146 khớp)
   ```
3. **Mọi `Method` trong Bảng 3 tồn tại thật** trong `SourcePresenceTests.cs`:
   ```powershell
   $rows = Import-Csv presence.csv   # cột Line, Method, DisplayName
   foreach ($r in $rows) {
     Select-String -Pattern "void $($r.Method)\b|Task $($r.Method)\b" -Path "PhotoReview.Tests.Unit\SourcePresenceTests.cs"
   }
   # Kết quả thực tế: 0 tên không tồn tại (69/69 khớp)
   ```
4. **Tên test tham chiếu trong cột DROP tồn tại thật** trong `ServiceBehaviorTests.cs` (`NaturalFilenameSortOrdersNumericSuffixes` dòng 235, `NaturalFilenameSortHandlesLongNumericRuns` dòng 244, `SizeSortOrdersFilesByDescendingBytes` dòng 252, `SizeSortOrdersFilesByAscendingBytes` dòng 260, `FileHashServiceCachesAndInvalidates` dòng 338, `FileHashServiceDeduplicatesConcurrentReads` dòng 355) — cả 6/6 FOUND.
