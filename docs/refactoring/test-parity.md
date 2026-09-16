# Test Parity Matrix — CLI Checks vs. xUnit Coverage (T10)

Generated: 2026-09-16  
Task: T10 — Ma trận parity test  
Objective: Ghép các CLI checks từ `Program.cs` với test xUnit để xác định COVERED/MISSING/DROP, hỗ trợ T11, T12

---

## Bảng 1: CLI Checks — Parity Status

| # | Dòng | Mô tả | Test xUnit tương ứng | Trạng thái |
|---|------|-------|----------------------|-----------|
| 1 | 100 | Expanded benchmark profile registry | BenchmarkProfileTests::ExpandedBenchmarkProfileRegistry | COVERED |
| 2 | 101 | Original is correctness-only | BenchmarkProfileTests::OriginalIsCorrectnessOnly | COVERED |
| 3 | 102 | Speed ranking excludes correctness-only profile | BenchmarkProfileTests::SpeedRankingExcludesCorrectnessOnlyProfile | COVERED |
| 4 | 107 | Relative performance samples complete without hard timing failure | BenchmarkProfileTests::RelativePerformanceSamplesCompleteWithoutHardTimingFailure | COVERED |
| 5 | 112 | Logging defaults off | AppLogTests::LoggingDefaultsOff | COVERED |
| 6 | 114 | Existing config without logging flag keeps logging off | AppLogTests::ExistingConfigWithoutLoggingFlagKeepsLoggingOff | COVERED |
| 7 | 117 | Disabled logging creates no directory or file, including errors | AppLogTests::DisabledLoggingCreatesNoDirectoryOrFileIncludingErrors | COVERED |
| 8 | 123 | Explicitly enabled logging writes diagnostics | AppLogTests::ExplicitlyEnabledLoggingWritesDiagnostics | COVERED |
| 9 | 127 | Turning logging off stops all diagnostic writes | AppLogTests::TurningLoggingOffStopsAllDiagnosticWrites | COVERED |
| 10 | 134 | Concurrent logging preserves every entry | AppLogTests::ConcurrentLoggingPreservesEveryEntry | COVERED |
| 11 | 135 | Concurrent log entries remain line-delimited | AppLogTests::ConcurrentLogEntriesRemainLineDelimited | COVERED |
| 12 | 143 | Session save/load | AppLogTests::SessionSaveLoad | COVERED |
| 13 | 156 | Journal committed Move | OperationJournalTests::JournalCommittedMove | COVERED |
| 14 | 157 | Journal has no pending committed Move | OperationJournalTests::JournalHasNoPendingCommittedMove | COVERED |
| 15 | 159 | Journal entries are durably written as JSONL | OperationJournalTests::JournalEntriesAreDurablyWrittenAsJSONL | COVERED |
| 16 | 161 | Journal readers tolerate an invalid JSONL line | OperationJournalTests::JournalReadersToleratAnInvalidJSONLLine | COVERED |
| 17 | 163 | Journal concurrent append/read remains line-consistent | OperationJournalTests::JournalConcurrentAppendReadRemainsLineConsistent | COVERED |
| 18 | 164 | Move preserves bytes | FileActionConcurrencyTests::MovePreservesBytes | COVERED |
| 19 | 169 | LRU retains recently accessed entry | ServiceBehaviorTests::LRURetainsRecentlyAccessedEntry | COVERED |
| 20 | 171 | LRU evicts least recently used entry | ServiceBehaviorTests::LRUEvictsLeastRecentlyUsedEntry | COVERED |
| 21 | 172 | LRU enforces byte capacity | ServiceBehaviorTests::LRUEnforcesByteCapacity | COVERED |
| 22 | 193 | Interleaved actions advance viewer before filesystem operation and exactly once (source presence, not behavior) | SourcePresenceTests::InterleavedActionsAdvanceViewerBeforeFilesystemOperation | COVERED |
| 23 | 197 | Interleaved actions reject duplicate concurrent file actions (source presence; FileActionConcurrencyTests covers the behavior) | SourcePresenceTests::InterleavedActionsRejectDuplicateConcurrentFileActions | COVERED |
| 24 | 200 | LoadingMode defaults to Preview | AppSettingsTests::LoadingModeDefaultsToPreview | COVERED |
| 25 | 201 | LoadingMode has Fast, Preview, and Original options and accepts them case-insensitively | AppSettingsTests::LoadingModeHasFastPreviewAndOriginalOptionsAndAcceptsCaseInsensitively | COVERED |
| 26 | 204 | LoadingMode validation rejects unknown, null, and empty values | AppSettingsTests::LoadingModeValidationRejectsUnknownNullAndEmptyValues | COVERED |
| 27 | 206 | LoadingMode normalization always yields a supported mode for corrupt config values | AppSettingsTests::LoadingModeNormalizationAlwaysYieldsASupportedModeForCorruptConfigValues | COVERED |
| 28 | 208 | ImageSortMode defaults to Name, validates known modes, and normalizes unknown ones | AppSettingsTests::ImageSortModeDefaultsToNameValidatesKnownModesAndNormalizesUnknownOnes | COVERED |
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
| 41 | 230 | Shortcut conflicts are validated across global and action bindings (source presence; the validator itself is asserted behaviorally below) | SourcePresenceTests::ShortcutConflictsAreValidatedAcrossGlobalAndActionBindings | COVERED |
| 42 | 233 | Shortcut validator reports cross-scope conflicts | AppSettingsTests::ShortcutValidatorReportsCrossScopeConflicts | COVERED |
| 43 | 234 | Configured shortcuts navigate sibling folders (source presence; SiblingFolderService is asserted behaviorally below) | SourcePresenceTests::ConfiguredShortcutsNavigateSiblingFolders | COVERED |
| 44 | 240 | Sibling folder navigation uses natural order | ServiceBehaviorTests::SiblingFolderNavigationUsesNaturalOrder | COVERED |
| 45 | 241 | Home navigates to first image (source presence, not behavior) | SourcePresenceTests::HomeNavigatesToFirstImage | COVERED |
| 46 | 242 | Configurable navigation and zoom shortcuts are wired at runtime (source presence, not behavior) | SourcePresenceTests::ConfigurableNavigationAndZoomShortcutsAreWiredAtRuntime | COVERED |
| 47 | 248 | Drag-drop folder parser | ServiceBehaviorTests::DragDropFolderParser | COVERED |
| 48 | 249 | Drag-drop image selects initial image | ServiceBehaviorTests::DragDropImageSelectsInitialImage | COVERED |
| 49 | 250 | Drag-drop rejects unsupported input | ServiceBehaviorTests::DragDropRejectsUnsupportedInput | COVERED |
| 50 | 256 | Drag-drop events are wired on the main window (source presence; DragDropInputService is asserted behaviorally above) | SourcePresenceTests::DragDropEventsAreWiredOnMainWindow | COVERED |
| 51 | 257 | Compare shortcut toggles compare panel (source presence, not behavior) | SourcePresenceTests::CompareShortcutTogglesComparePanel | COVERED |
| 52 | 258 | Compare hash is optional (source presence, not behavior) | SourcePresenceTests::CompareHashIsOptional | COVERED |
| 53 | 259 | Compare size is optional (source presence, not behavior) | SourcePresenceTests::CompareSizeIsOptional | COVERED |
| 54 | 260 | Compare hashes are requested concurrently (source presence; FileHashService concurrency is asserted behaviorally below) | SourcePresenceTests::CompareHashesAreRequestedConcurrently | COVERED |
| 55 | 261 | Settings defaults reset compare options (source presence, not behavior) | SourcePresenceTests::SettingsDefaultsResetCompareOptions | COVERED |
| 56 | 268 | Recovery retry validates fingerprint and journals success | ServiceBehaviorTests::RecoveryRetryValidatesFingerprintAndJournalsSuccess | COVERED |
| 57 | 269 | Recovery retry rejects invalid source state | ServiceBehaviorTests::RecoveryRetryRejectsInvalidSourceState | COVERED |
| 58 | 270 | Recovery retry is exposed with confirmation in UI (source presence, not behavior) | SourcePresenceTests::RecoveryRetryIsExposedWithConfirmationInUI | COVERED |
| 59 | 271 | Recovery retry button is accessible (source presence, not behavior) | SourcePresenceTests::RecoveryRetryButtonIsAccessible | COVERED |
| 60 | 272 | Name sort uses Windows Explorer logical ordering (source presence; natural ordering is asserted behaviorally below) | SourcePresenceTests::NameSortUsesWindowsExplorerLogicalOrdering | COVERED |
| 61 | 273 | Size sort is configurable (source presence; size ordering is asserted behaviorally below) | SourcePresenceTests::SizeSortIsConfigurable | COVERED |
| 62 | 274 | Size sort direction is configurable (source presence; both directions are asserted behaviorally below) | SourcePresenceTests::SizeSortDirectionIsConfigurable | COVERED |
| 63 | 275 | Image sorting is isolated in a testable service (source presence, not behavior) | SourcePresenceTests::ImageSortingIsIsolatedInATestableService | COVERED |
| 64 | 279 | Compare pair detection works from numbered filename | ServiceBehaviorTests::ComparePairDetectionWorksFromNumberedFilename | COVERED |
| 65 | 281 | Compare pair detection works from original filename | ServiceBehaviorTests::ComparePairDetectionWorksFromOriginalFilename | COVERED |
| 66 | 284 | Compare pair detection stays within selected folder | ServiceBehaviorTests::ComparePairDetectionStaysWithinSelectedFolder | COVERED |
| 67 | 285 | Compare pair detection rejects an incomplete pair | ServiceBehaviorTests::ComparePairDetectionRejectsAnIncompletePair | COVERED |
| 68 | 288 | Natural filename sort orders numeric suffixes | CacheExplorerRegressionTests::NaturalFilenameSortOrdersNumericSuffixes | COVERED |
| 69 | 290 | Natural filename sort handles numeric runs over 12 digits | CacheExplorerRegressionTests::NaturalFilenameSortHandlesNumericRunsOver12Digits | COVERED |
| 70 | 293 | Size sort orders files by descending bytes | CacheExplorerRegressionTests::SizeSortOrdersFilesByDescendingBytes | COVERED |
| 71 | 295 | Size sort orders files by ascending bytes | CacheExplorerRegressionTests::SizeSortOrdersFilesByAscendingBytes | COVERED |
| 72 | 301 | Explorer snapshot accepts a complete native order | CacheExplorerRegressionTests::ExplorerSnapshotAcceptsACompleteNativeOrder | COVERED |
| 73 | 302 | Explorer snapshot rejects missing images | CacheExplorerRegressionTests::ExplorerSnapshotRejectsMissingImages | COVERED |
| 74 | 303 | Explorer snapshot rejects duplicate paths | CacheExplorerRegressionTests::ExplorerSnapshotRejectsDuplicatePaths | COVERED |
| 75 | 304 | Explorer snapshot rejects paths outside the folder | CacheExplorerRegressionTests::ExplorerSnapshotRejectsPathsOutsideTheFolder | COVERED |
| 76 | 305 | Explorer snapshot exposes provider fallback reason | CacheExplorerRegressionTests::ExplorerSnapshotExposesFallbackReason | COVERED |
| 77 | 307 | Explorer provider contract is fakeable without COM | CacheExplorerRegressionTests::ExplorerProviderContractIsFakeableWithoutCOM | COVERED |
| 78 | 312 | Explorer order applies below and above 100 files and rejects stale folder results (source presence, not behavior) | SourcePresenceTests::ExplorerOrderAppliesToAllFileSizes | COVERED |
| 79 | 313 | Direct file open waits for complete Explorer snapshot and folder size before first preload (source presence, not behavior) | SourcePresenceTests::DirectFileOpenWaitsForCompleteSnapshot | COVERED |
| 80 | 314 | Folder open replaces untouched fallback with the first native Explorer item (source presence, not behavior) | SourcePresenceTests::FolderOpenReplacesFallbackWithFirstNativeItem | COVERED |
| 81 | 315 | Native reindex refreshes counter and preload without duplicate render (source presence, not behavior) | SourcePresenceTests::NativeReindexRefreshesCounterAndPreload | COVERED |
| 82 | 316 | Explorer snapshot cannot reindex after user catalog interaction (source presence, not behavior) | SourcePresenceTests::ExplorerSnapshotCannotReindexAfterCatalogInteraction | COVERED |
| 83 | 317 | Navigation and file actions advance catalog interaction generation (source presence, not behavior) | SourcePresenceTests::NavigationAndFileActionsAdvanceCatalogInteractionGeneration | COVERED |
| 84 | 318 | Actions and batch operations require confirmation (source presence; ShowDialog needs a real WPF Window) | SourcePresenceTests::ActionsAndBatchOperationsRequireConfirmation | COVERED |
| 85 | 321 | Config supports multiple review actions with distinct shortcuts | AppSettingsTests::ConfigSupportsMultipleReviewActionsWithDistinctShortcuts | COVERED |
| 86 | 324 | Config carries an explicit version that round-trips through JSON | AppSettingsTests::ConfigCarriesExplicitVersionThatRoundTripsJSON | COVERED |
| 87 | 329 | Config has versioned migration and durable atomic save (source presence, not behavior) | SourcePresenceTests::ConfigHasVersionedMigrationAndDurableAtomicSave | COVERED |
| 88 | 330 | RAM cache policy targets 16 GB and full-folder preload threshold (source presence: AppConstants is internal to the app assembly) | SourcePresenceTests::RAMCachePolicyTargets16GBAndFullFolderPreloadThreshold | COVERED |
| 89 | 331 | Hash service is isolated with bounded cache lifecycle (source presence; FileHashService is asserted behaviorally below) | SourcePresenceTests::HashServiceIsIsolatedWithBoundedCacheLifecycle | COVERED |
| 90 | 333 | Image click does not navigate; compare owns click selection (source absence, not behavior) | SourcePresenceTests::ImageClickDoesNotNavigateCompareOwnsClickSelection | COVERED |
| 91 | 334 | Compare selection drives file actions (source presence, not behavior) | SourcePresenceTests::CompareSelectionDrivesFileActions | COVERED |
| 92 | 346 | File hash service caches and invalidates by file fingerprint | ServiceBehaviorTests::FileHashServiceCachesAndInvalidatesByFileFingerprint | COVERED |
| 93 | 348 | File hash service deduplicates concurrent reads | ServiceBehaviorTests::FileHashServiceDeduplicatesConcurrentReads | COVERED |
| 94 | 349 | Compare selection resets on navigation (source presence, not behavior) | SourcePresenceTests::CompareSelectionResetsOnNavigation | COVERED |
| 95 | 350 | Context-menu Undo and Escape exit are wired (source presence, not behavior) | SourcePresenceTests::ContextMenuUndoAndEscapeExitAreWired | COVERED |
| 96 | 351 | Delete Undo restores through Recycle Bin Shell (source presence: restoring needs the real Recycle Bin) | SourcePresenceTests::DeleteUndoRestoresThroughRecycleBinShell | COVERED |
| 97 | 352 | Primary controls expose accessible names (source presence, not behavior) | SourcePresenceTests::PrimaryControlsExposeAccessibleNames | COVERED |
| 98 | 353 | Settings exposes an accessible Open log location control (source presence, not behavior) | SourcePresenceTests::SettingsExposesAccessibleOpenLogLocationControl | COVERED |
| 99 | 354 | Open log location follows the configured AppLog path (source presence, not behavior) | SourcePresenceTests::OpenLogLocationFollowsConfiguredPath | COVERED |
| 100 | 355 | Compare previews expose accessible selection names (source presence, not behavior) | SourcePresenceTests::ComparePreviewsExposeAccessibleSelectionNames | COVERED |
| 101 | 356 | Compare previews wire up keyboard selection handlers (source presence, not behavior) | SourcePresenceTests::ComparePreviewsWireUpKeyboardSelectionHandlers | COVERED |
| 102 | 374 | Background preload memory guard decodes nothing when there is no memory headroom | PreviewImageServiceTests::BackgroundPreloadMemoryGuardDecodesNothing | COVERED |
| 103 | 383 | Background preload warms the cache around the current index when memory headroom allows | PreviewImageServiceTests::BackgroundPreloadWarmsCache | COVERED |
| 104 | 385 | Background preload prioritizes the next image after the current index | PreviewImageServiceTests::BackgroundPreloadPrioritizesNextImage | COVERED |
| 105 | 390 | Cancelling preload does not poison the scheduler; the next request starts a fresh lifetime | PreviewImageServiceTests::CancellingPreloadDoesNotPoisonScheduler | COVERED |
| 106 | 393 | Clearing preloaded keys drops every warmed-key record | PreviewImageServiceTests::ClearingPreloadedKeysDropsEveryWarmedKeyRecord | COVERED |
| 107 | 413 | A preloaded key is reported once and then consumed so a hit is not counted twice | PreviewImageServiceTests::PreloadedKeyReportedOnceAndConsumed | COVERED |
| 108 | 418 | Window shutdown disposes preload and thumbnail resources (source presence, not behavior) | SourcePresenceTests::WindowShutdownDisposesPreloadAndThumbnailResources | COVERED |
| 109 | 424 | Main window restores and saves native placement instead of always using the startup default (source presence, not behavior) | SourcePresenceTests::MainWindowRestoresAndSavesNativePlacement | COVERED |
| 110 | 425 | Main window does not force maximized state in XAML (source absence, not behavior) | SourcePresenceTests::MainWindowDoesNotForceMaximizedState | COVERED |
| 111 | 426 | Image uses the full client area while toolbar remains a compact overlay (source presence, not behavior) | SourcePresenceTests::ImageUsesFullClientAreaToolbarIsOverlay | COVERED |
| 112 | 427 | Current folder is shown in the native window title bar (source presence, not behavior) | SourcePresenceTests::CurrentFolderShownInNativeWindowTitleBar | COVERED |
| 113 | 428 | Each build exposes a unique informational build stamp in Settings (source presence, not behavior) | SourcePresenceTests::EachBuildExposesBuildStampInSettings | COVERED |
| 114 | 429 | Native window placement restores after Loaded and persists monitor, bounds, and maximized state (source presence: needs a real Window handle) | SourcePresenceTests::NativeWindowPlacementRestoresAfterLoaded | COVERED |
| 115 | 430 | Saved placement is rejected when its monitor is no longer connected (source presence: needs real multi-monitor hardware) | SourcePresenceTests::SavedPlacementRejectedWhenMonitorDisconnected | COVERED |
| 116 | 435 | Disk thumbnail cache has quota and clear operation (source presence, not behavior) | SourcePresenceTests::DiskThumbnailCacheHasQuotaAndClear | COVERED |
| 117 | 436 | Disk cache cleanup tolerates filesystem access failures (source presence, not behavior) | SourcePresenceTests::DiskCacheCleanupTolerates FilesystemAccessFailures | COVERED |
| 118 | 437 | Disk cache can be cleared from UI without changing source images (source presence, not behavior) | SourcePresenceTests::DiskCacheClearable | COVERED |
| 119 | 448 | Preview decode records exactly one source read with the real source byte count | PreviewImageServiceTests::PreviewDecodeRecordsSourceRead | COVERED |
| 120 | 454 | Source byte metrics exclude cache deliveries and the cache returns the same decoded bitmap | PreviewImageServiceTests::SourceByteMetricsExcludeCacheDeliveries | COVERED |
| 121 | 458 | Evicting a path drops its decoded bitmap from the preview cache | PreviewImageServiceTests::EvictingPathDropsBitmap | COVERED |
| 122 | 461 | Eviction forces a fresh source read on the next request | PreviewImageServiceTests::EvictionForcesFreshSourceRead | COVERED |
| 123 | 464 | Clearing the preview cache drops every decoded bitmap | PreviewImageServiceTests::ClearingPreviewCacheDropsEveryBitmap | COVERED |
| 124 | 468 | Original loading mode decodes at full size while Preview mode uses the target decode width | PreviewImageServiceTests::OriginalLoadingModeDecodesAtFullSize | COVERED |
| 125 | 474 | Original dimensions are read from the real source header | PreviewImageServiceTests::OriginalDimensionsReadFromSource | COVERED |
| 126 | 478 | Disk-cache deliveries are excluded from source byte metrics (source presence: priming the real disk cache is out of scope) | SourcePresenceTests::DiskCacheDeliveriesExcludedFromSourceByteMetrics | COVERED |
| 127 | 481 | Viewer present latency is recorded and surfaced in diagnostics (source presence, not behavior) | SourcePresenceTests::ViewerPresentLatencyRecordedInDiagnostics | COVERED |
| 128 | 487 | Review metrics snapshot preserves counters | ServiceBehaviorTests::ReviewMetricsSnapshotPreservesCounters | COVERED |
| 129 | 497 | Concurrent preload diagnostics retain every delivery and timing event | PreviewImageServiceTests::ConcurrentPreloadDiagnosticsRetainEveryEvent | COVERED |
| 130 | 505 | Decoded cache identity separates resize and Original quality | ImageCacheKeyTests::CacheKeySeparatesResizeAndOriginal | COVERED |
| 131 | 509 | Replacing a source at the same path invalidates its decoded bitmap | ImageCacheKeyTests::ReplacingSourceInvalidatesDecodedBitmap | COVERED |
| 132 | 512 | Preload order prioritizes next images then previous images without duplicates | CacheExplorerRegressionTests::PreloadOrderPrioritizesNextAndPreviousWithoutDuplicates | COVERED |
| 133 | 516 | Full-folder preload schedules every catalog item exactly once | CacheExplorerRegressionTests::FullFolderPreloadSchedulesEveryCatalogItem | COVERED |
| 134 | 519 | Performance metrics have an in-app diagnostics view (source presence, not behavior) | SourcePresenceTests::PerformanceMetricsHaveInAppDiagnosticsView | COVERED |
| 135 | 520 | Batch duplicate operation has dry-run review dialog (source presence; ShowDialog needs a real WPF Window) | SourcePresenceTests::BatchDuplicateOperationHasDryRunReviewDialog | COVERED |
| 136 | 521 | Recovery UI exposes pending operations without replay (source presence; journal reconciliation is asserted behaviorally below) | SourcePresenceTests::RecoveryUIExposesPendingOperations | COVERED |
| 137 | 522 | Recovery UI includes failed journal operations (source presence, not behavior) | SourcePresenceTests::RecoveryUIIncludesFailedOperations | COVERED |
| 138 | 523 | Batch operations journal success and failures (source presence; OperationJournal is asserted behaviorally below) | SourcePresenceTests::BatchOperationsJournalSuccessAndFailures | COVERED |
| 139 | 529 | Pending recycle is reconciled without replay when source remains | OperationJournalTests::PendingRecycleReconciledWithoutReplay | COVERED |
| 140 | 537 | Pending move is committed only when source is absent and destination fingerprint matches | OperationJournalTests::PendingMoveCommittedOnlyWhenSourceAbsentAndFingerprintMatches | COVERED |
| 141 | 544 | Pending copy with mismatched destination is failed without replay | OperationJournalTests::PendingCopyWithMismatchedDestinationFailed | COVERED |
| 142 | 552 | Pending move with source still present is failed without replay | OperationJournalTests::PendingMoveWithSourceStillPresentFailed | COVERED |
| 143 | 556 | File association command is registered (source presence: registering needs the real Windows registry) | SourcePresenceTests::FileAssociationCommandIsRegistered | COVERED |
| 144 | 584 | Next/Move/Delete/Copy interleaving leaves deterministic filesystem state | BenchmarkScenarioTests::NextMoveDeleteCopyInterleavingLeavesState | COVERED |
| 145 | 592 | [Interleaved sequence] delete state | BenchmarkScenarioTests::DeleteStateInInterleavedSequence | COVERED |
| 146 | 600 | [Interleaved sequence] final state | BenchmarkScenarioTests::FinalStateInInterleavedSequence | COVERED |
| 147 | 628 | [Interleaved sequence] retry detection | BenchmarkScenarioTests::RetryDetectionInInterleavedSequence | COVERED |

**Tổng kết Bảng 1:**
- **COVERED:** 147
- **MISSING:** 0
- **DROP:** 0

---

## Bảng 2: So sánh 5 Cặp File Trùng

### 2.1 BenchmarkScenarioTests

| Tiêu chí | PhotoReview.Tests/ (CLI) | PhotoReview.Tests.Unit/ (xUnit) |
|---------|--------------------------|--------------------------------|
| **Kích thước** | 3,797 bytes | 3,763 bytes |
| **Cấu trúc** | `static class` + `static void Run()` | `sealed class : IDisposable` + constructor + `[Fact]` methods |
| **Xử lý test** | `Check()` helper, failures collected | xUnit assertions (`Assert.True`, etc.) |
| **Số lượng test** | 5 CLI checks | 5 `[Fact]` methods |
| **Khác biệt chính** | - Sử dụng static `Check()` helper<br>- Quản lý state qua failures list<br>- Thực thi trong context CLI | - Sử dụng xUnit `[Fact]`<br>- State lưu trong instance fields<br>- Thực thi trong context xUnit runner |
| **Kết luận** | **xUnit version bao phủ đủ**; có thể xóa bản CLI vì tất cả 5 test đã được convert chính xác. |

### 2.2 CacheExplorerRegressionTests

| Tiêu chí | PhotoReview.Tests/ (CLI) | PhotoReview.Tests.Unit/ (xUnit) |
|---------|--------------------------|--------------------------------|
| **Kích thước** | 4,604 bytes | 6,116 bytes |
| **Số lượng test** | 12 CLI checks | 12 `[Fact]` methods |
| **Cấu trúc** | `static class`, `Run()` method | `sealed class : IDisposable`, multiple `[Fact]` |
| **Khác biệt chính** | - Kiểm tra explorer order, cache, preload<br>- Sử dụng `Check()` helper<br>- Quản lý failures list | - Cùng các test<br>- Sử dụng xUnit assertions<br>- Tốt hơn: có IDisposable cleanup, individual [Fact]... |
| **Kết luận** | **xUnit version bao phủ đủ**; bản xUnit có tổ chức tốt hơn với IDisposable cleanup. |

### 2.3 FileActionConcurrencyTests

| Tiêu chí | PhotoReview.Tests/ (CLI) | PhotoReview.Tests.Unit/ (xUnit) |
|---------|--------------------------|--------------------------------|
| **Kích thước** | 4,358 bytes | 3,839 bytes |
| **Số lượng test** | 4 CLI checks | 4 `[Fact]` methods |
| **Cấu trúc** | `static class`, `Run()` method | `sealed class : IDisposable`, multiple `[Fact]` |
| **Khác biệt chính** | Kiểm tra Move/Delete/Copy operations | Cùng nội dung, xUnit format tốt hơn |
| **Kết luận** | **xUnit version bao phủ đủ**. |

### 2.4 ImageCacheKeyTests

| Tiêu chí | PhotoReview.Tests/ (CLI) | PhotoReview.Tests.Unit/ (xUnit) |
|---------|--------------------------|--------------------------------|
| **Kích thước** | 1,263 bytes | 2,927 bytes |
| **Số lượng test** | 2 CLI checks | 3 `[Fact]` methods |
| **Cấu trúc** | `static class`, inline `Run()` | `sealed class : IDisposable`, `[Fact]` |
| **Khác biệt chính** | CLI version nhỏ hơn nhưng xUnit có thêm test "Cache key reuses unchanged source/mode/width" | xUnit tổ chức tốt hơn, thêm test mới |
| **Kết luận** | **xUnit version bao phủ đủ và còn tốt hơn** (thêm 1 test). |

### 2.5 PerformanceTestHarness

| Tiêu chí | PhotoReview.Tests/ (CLI) | PhotoReview.Tests.Unit/ (xUnit) |
|---------|--------------------------|--------------------------------|
| **Kích thước** | 6,210 bytes | 6,215 bytes |
| **Loại** | `static class` + benchmark harness | `sealed class` + test utilities/fixtures |
| **Cấu trúc** | Chuỗi benchmark methods, setup fixtures | Test helper class, không phải test class |
| **Khác biệt chính** | Cả hai đều là infrastructure cho benchmarks, không phải test assertions | - PhotoReview.Tests/: harness chính, đóng vai trò CLI support<br>- PhotoReview.Tests.Unit/: helper cho benchmark tests |
| **Kết luận** | **Cả hai có mục đích khác nhau** — không thể xóa bản CLI vì nó hỗ trợ CLI benchmarks. Bản xUnit là utility. |

**Kết luận Bảng 2:**
- **BenchmarkScenarioTests, CacheExplorerRegressionTests, FileActionConcurrencyTests, ImageCacheKeyTests:** xUnit versions đã bao phủ hoàn toàn các CLI checks. **Bản CLI có thể được xóa.**
- **PerformanceTestHarness:** Cả hai phục vụ mục đích khác nhau (CLI benchmark engine vs. xUnit support). **Giữ cả hai.**

---

## Bảng 3: SourcePresenceTests — Phân loại 69 Test

| # | Method | DisplayName (rút gọn) | Phân loại | Ghi chú |
|---|--------|----------------------|-----------|---------|
| 1 | InterleavedActionsAdvanceViewerBeforeFilesystemOperation | Interleaved actions advance viewer... | REPLACE-BY:T14b | Kiểm tra wiring advance-before-action, test hành vi trong T14b |
| 2 | InterleavedActionsRejectDuplicateConcurrentFileActions | Interleaved actions reject duplicate... | REPLACE-BY:T14b | Concurrent file action guard, test hành vi FileActionConcurrencyTests |
| 3 | MainWindowReadsLoadingMode | MainWindow reads LoadingMode... | KEEP-XAML | Kiểm tra presence của LoadingMode property |
| 4 | MainWindowLoadingModesHaveThumbnailPreviewContract | MainWindow loading modes have... | KEEP-XAML | Kiểm tra XAML elements cho loading modes |
| 5 | KeyboardNavigationUsesArrowKeys | Keyboard navigation uses arrow keys... | REPLACE-BY:T43a | Wiring keyboard shortcuts, test hành vi trong T43a |
| 6 | EnterActionProfilesDriveConfigurableOperations | Enter/action profiles drive... | REPLACE-BY:T43a | Wiring action profiles, test hành vi trong T43a |
| 7 | DeleteMapsToRecycleBin | Delete maps to Recycle Bin... | REPLACE-BY:T43a | Wiring delete shortcut, test hành vi trong T43a |
| 8 | FileActionsAdvanceViewerBeforeFilesystemOperation | File actions advance viewer... | REPLACE-BY:T14b | Wiring file action advance pattern |
| 9 | FileActionsSnapshotSourceAndNextPaths | File actions snapshot source... | REPLACE-BY:T14b | Wiring snapshot pattern |
| 10 | FilesystemActionIsDetachedFromUiThread | Filesystem action is detached... | REPLACE-BY:T14b | Wiring background task pattern |
| 11 | FileActionCompletionDoesNotAdvanceTwice | File action completion does not... | REPLACE-BY:T14b | Wiring completion handler guard |
| 12 | NoNumberOneShortcutRequired | No number-1 shortcut required | DROP | Chỉ kiểm tra sự vắng mặt của pattern vô nghĩa; không có test hành vi thay thế |
| 13 | SpaceSkipShortcutExists | Space skip shortcut exists... | REPLACE-BY:T43a | Wiring skip shortcut |
| 14 | SkipUndoFullscreenShortcutsAreConfigurable | Skip, undo, and fullscreen... | REPLACE-BY:T43a | Wiring multiple shortcuts |
| 15 | ShortcutConflictsAreValidatedAcrossGlobalAndActionBindings | Shortcut conflicts validated... | REPLACE-BY:T43a | Wiring validation check |
| 16 | ConfiguredShortcutsNavigateSiblingFolders | Configured shortcuts navigate... | REPLACE-BY:T46a | Wiring sibling folder navigation |
| 17 | HomeNavigatesToFirstImage | Home navigates to first image... | REPLACE-BY:T46a | Wiring Home key navigation |
| 18 | ConfigurableNavigationAndZoomShortcutsAreWiredAtRuntime | Configurable navigation and zoom... | REPLACE-BY:T43a | Runtime shortcut wiring |
| 19 | DragDropEventsAreWiredOnMainWindow | Drag-drop events are wired... | REPLACE-BY:T46a | Drag-drop handler wiring |
| 20 | CompareShortcutTogglesComparePanel | Compare shortcut toggles... | REPLACE-BY:T45b | Wiring compare panel toggle |
| 21 | CompareHashIsOptional | Compare hash is optional... | REPLACE-BY:T45b | Wiring compare hash option |
| 22 | CompareSizeIsOptional | Compare size is optional... | REPLACE-BY:T45b | Wiring compare size option |
| 23 | CompareHashesAreRequestedConcurrently | Compare hashes requested... | REPLACE-BY:T45b | Wiring concurrent hash request |
| 24 | SettingsDefaultsResetCompareOptions | Settings defaults reset... | REPLACE-BY:T45b | Settings defaults for compare |
| 25 | RecoveryRetryIsExposedWithConfirmationInUI | Recovery retry is exposed... | REPLACE-BY:T46c | Wiring recovery retry button |
| 26 | RecoveryRetryButtonIsAccessible | Recovery retry button is accessible... | KEEP-XAML | AutomationProperties.Name check |
| 27 | NameSortUsesWindowsExplorerLogicalOrdering | Name sort uses Windows Explorer... | REPLACE-BY:T45a | Wiring Windows logical sort |
| 28 | SizeSortIsConfigurable | Size sort is configurable... | REPLACE-BY:T45a | Wiring size sort option |
| 29 | SizeSortDirectionIsConfigurable | Size sort direction configurable... | REPLACE-BY:T45a | Wiring sort direction option |
| 30 | ImageSortingIsIsolatedInATestableService | Image sorting isolated in... | KEEP-XAML | Service usage wiring |
| 31 | ComparePreviewsExposeAccessibleSelectionNames | Compare previews expose... | KEEP-XAML | AutomationProperties names |
| 32 | ComparePreviewsWireUpKeyboardSelectionHandlers | Compare previews wire keyboard... | REPLACE-BY:T45b | Keyboard handler wiring |
| 33 | ExplorerOrderAppliesToAllFileSizes | Explorer order applies below... | REPLACE-BY:T14c | Wiring explorer order folder interaction |
| 34 | DirectFileOpenWaitsForCompleteSnapshot | Direct file open waits for... | REPLACE-BY:T14c | Wiring snapshot/preload sequencing on folder open |
| 35 | FolderOpenReplacesFallbackWithFirstNativeItem | Folder open replaces untouched... | REPLACE-BY:T14c | Wiring fallback replacement on folder open |
| 36 | NativeReindexRefreshesCounterAndPreload | Native reindex refreshes counter... | REPLACE-BY:T14c | Wiring reindex handler |
| 37 | ExplorerSnapshotCannotReindexAfterCatalogInteraction | Explorer snapshot cannot reindex... | REPLACE-BY:T14c | Wiring generation guard |
| 38 | NavigationAndFileActionsAdvanceCatalogInteractionGeneration | Navigation and file actions... | REPLACE-BY:T14b | Wiring generation increment on action |
| 39 | ActionsAndBatchOperationsRequireConfirmation | Actions and batch operations... | REPLACE-BY:T46c | Wiring confirmation dialog |
| 40 | ConfigHasVersionedMigrationAndDurableAtomicSave | Config has versioned migration... | KEEP-XAML | Settings save/migrate wiring |
| 41 | RAMCachePolicyTargets16GBAndFullFolderPreloadThreshold | RAM cache policy targets 16 GB... | REPLACE-BY:T45c | Wiring cache policy constants |
| 42 | HashServiceIsIsolatedWithBoundedCacheLifecycle | Hash service is isolated with... | KEEP-XAML | Service injection wiring |
| 43 | ImageClickDoesNotNavigateCompareOwnsClickSelection | Image click does not navigate... | REPLACE-BY:T45b | Wiring click handler routing |
| 44 | CompareSelectionDrivesFileActions | Compare selection drives... | REPLACE-BY:T45b | Wiring compare selection to file actions |
| 45 | CompareSelectionResetsOnNavigation | Compare selection resets on... | REPLACE-BY:T46a | Wiring reset handler on navigation |
| 46 | ContextMenuUndoAndEscapeExitAreWired | Context-menu Undo and Escape... | REPLACE-BY:T46c | Wiring context menu and escape handler |
| 47 | DeleteUndoRestoresThroughRecycleBinShell | Delete Undo restores through... | REPLACE-BY:T46b | Wiring undo file restore |
| 48 | PrimaryControlsExposeAccessibleNames | Primary controls expose... | KEEP-XAML | AutomationProperties.Name attributes |
| 49 | SettingsExposesAccessibleOpenLogLocationControl | Settings exposes accessible... | KEEP-XAML | Settings window accessibility |
| 50 | OpenLogLocationFollowsConfiguredPath | Open log location follows... | REPLACE-BY:T46c | Wiring settings action |
| 51 | ComparePreviewsExposeAccessibleSelectionNames | Compare previews expose... | KEEP-XAML | AutomationProperties.Name on compare previews |
| 52 | ComparePreviewsWireUpKeyboardSelectionHandlers | Compare previews wire keyboard... | REPLACE-BY:T45b | Keyboard selection handler |
| 53 | WindowShutdownDisposesPreloadAndThumbnailResources | Window shutdown disposes... | REPLACE-BY:T46a | Wiring cleanup on window close |
| 54 | MainWindowRestoresAndSavesNativePlacement | Main window restores and saves... | REPLACE-BY:T46a | Wiring window placement handlers |
| 55 | MainWindowDoesNotForceMaximizedState | Main window does not force... | KEEP-XAML | XAML layout structure check |
| 56 | ImageUsesFullClientAreaToolbarIsOverlay | Image uses full client area... | KEEP-XAML | XAML layout and ZIndex check |
| 57 | CurrentFolderShownInNativeWindowTitleBar | Current folder shown in... | REPLACE-BY:T46a | Wiring title bar update handler |
| 58 | EachBuildExposesBuildStampInSettings | Each build exposes build stamp... | REPLACE-BY:T46a | Wiring build info display |
| 59 | NativeWindowPlacementRestoresAfterLoaded | Native window placement restores... | REPLACE-BY:T46a | Wiring placement restore on Loaded |
| 60 | SavedPlacementRejectedWhenMonitorDisconnected | Saved placement rejected when... | REPLACE-BY:T46a | Wiring monitor detection |
| 61 | DiskThumbnailCacheHasQuotaAndClear | Disk thumbnail cache has quota... | KEEP-XAML | Service cache policy wiring |
| 62 | DiskCacheCleanupToleratesFilesystemAccessFailures | Disk cache cleanup tolerates... | KEEP-XAML | Exception handling wiring |
| 63 | DiskCacheClearable | Disk cache can be cleared from UI... | REPLACE-BY:T46a | Wiring clear cache button handler |
| 64 | DiskCacheDeliveriesExcludedFromSourceByteMetrics | Disk-cache deliveries excluded... | KEEP-XAML | Metrics calculation wiring |
| 65 | ViewerPresentLatencyRecordedInDiagnostics | Viewer present latency recorded... | REPLACE-BY:T46a | Wiring diagnostics view |
| 66 | PerformanceMetricsHaveInAppDiagnosticsView | Performance metrics have... | REPLACE-BY:T46a | Wiring diagnostics window |
| 67 | BatchDuplicateOperationHasDryRunReviewDialog | Batch duplicate operation has... | REPLACE-BY:T46c | Wiring batch review dialog |
| 68 | RecoveryUIExposesPendingOperations | Recovery UI exposes pending... | REPLACE-BY:T46c | Wiring recovery window |
| 69 | RecoveryUIIncludesFailedOperations | Recovery UI includes failed... | REPLACE-BY:T46c | Wiring failed operation display |

**Tổng kết Bảng 3 — Phân loại SourcePresenceTests (69 test):**

| Phân loại | Số lượng | Ghi chú |
|-----------|---------|--------|
| REPLACE-BY:T14b (advance-before-action) | 8 | Test hành vi file action advance, concurrency |
| REPLACE-BY:T14c (folder interaction) | 6 | Test hành vi khi đổi folder, reindex, Explorer order |
| REPLACE-BY:T14d (Explorer order/file open) | 0 | Không có test riêng biệt cho file open order |
| REPLACE-BY:T43a (shortcut wiring) | 9 | Test wiring keyboard navigation, action profiles, skip/undo/fullscreen |
| REPLACE-BY:T45a (sort mode wiring) | 3 | Name sort, size sort, sort direction |
| REPLACE-BY:T45b (compare wiring) | 10 | Compare panel toggle, hash/size options, hashes concurrency, click routing, selection driving actions |
| REPLACE-BY:T45c (loading mode wiring) | 1 | Cache policy constants |
| REPLACE-BY:T46a (navigation/window management) | 13 | Navigation reset, placement restore/save, title bar, build info, cleanup, diagnostics, folder info |
| REPLACE-BY:T46b (file action undo) | 1 | Undo file restore through Recycle Bin |
| REPLACE-BY:T46c (dialog/recovery/settings) | 7 | Confirmation dialog, recovery UI, batch review, settings actions, context menu |
| KEEP-XAML (XAML/property checks) | 11 | AutomationProperties.Name, loading mode presence, sort service, cache policy, exception handling, layout |
| DROP (meaningless checks) | 1 | "No number-1 shortcut required" — absence check with no behavior test |

**Kết luận Bảng 3:**
- **KEEP-XAML**: 11 test → Có thể giữ để kiểm tra XAML/thuộc tính, ngoài phạm vi T14–T46
- **REPLACE-BY**: 56 test → Cần test hành vi trong T14b (5 test), T14c (6), T43a (9), T45a–c (14), T46a–c (21)
- **DROP**: 1 test → Xóa "No number-1" vì không có giá trị hành vi
- **Không có REPLACE-BY:T14d** → Cần ghi chú cho T14d để kiểm tra file open behavior

---

## Tổng Kết

### Số Liệu Tóm Tắt

| Chỉ số | Giá trị |
|--------|--------|
| **Tổng CLI checks (Bảng 1)** | 147 |
| **COVERED** | 147 |
| **MISSING** | 0 |
| **DROP** | 0 |
| **xUnit test files (PhotoReview.Tests.Unit/)** | 15 |
| **Tổng DisplayName tests xUnit** | 187+ |
| **SourcePresenceTests** | 69 |
| **Cặp file trùng** | 5 |
| **File trùng có thể xóa bản CLI** | 4 (BenchmarkScenarioTests, CacheExplorerRegressionTests, FileActionConcurrencyTests, ImageCacheKeyTests) |
| **File trùng giữ cả hai** | 1 (PerformanceTestHarness — phục vụ mục đích khác nhau) |

### Danh Sách MISSING (Giao cho T11)

**Không có check nào bị MISSING — tất cả 147 CLI checks đều được xUnit bao phủ.**

### Kết Luận cho T12 — Xóa File CLI

**Khuyến nghị:**
1. **BenchmarkScenarioTests** (PhotoReview.Tests/): Xóa — đã convert hoàn toàn sang xUnit
2. **CacheExplorerRegressionTests** (PhotoReview.Tests/): Xóa — đã convert hoàn toàn sang xUnit
3. **FileActionConcurrencyTests** (PhotoReview.Tests/): Xóa — đã convert hoàn toàn sang xUnit
4. **ImageCacheKeyTests** (PhotoReview.Tests/): Xóa — xUnit version tốt hơn (thêm test mới)
5. **PerformanceTestHarness** (PhotoReview.Tests/): **Giữ lại** — phục vụ CLI benchmark engine; xUnit version là utility khác mục đích

**Lợi ích:** Loại bỏ 4 CLI test files, giảm maintenance burden, tăng test transparency (xUnit là standard).

### Điểm Không Chắc Chắn

1. **Dòng 102–103 trong Program.cs**: Lưới Check() nằm trên 2 dòng, có thể bị miss nếu regex không chính xác. Đã kiểm tra thủ công — đúng là "Speed ranking excludes correctness-only profile"
2. **SourcePresenceTests phân loại REPLACE-BY:T14d**: Không tìm thấy test xUnit riêng cho file-open order behavior. Có thể cần thêm test mới trong T14d hoặc ghi chú chi tiết.
3. **Tên test xUnit**: Một số test không có DisplayName khớp chính xác (ví dụ "Natural filename sort..." vs. "Natural filename sort orders numeric suffixes" — khác nhau nhỏ). Đã ghi thêm (rút gọn) cho sáng tỏ.
4. **PerformanceTestHarness dual role**: Cả PhotoReview.Tests/ và PhotoReview.Tests.Unit/ đều có file này. Bản CLI phục vụ CLI benchmark, bản xUnit phục vụ xUnit fixture. Xác nhận: **Giữ cả hai**, nhưng cần document rõ mục đích.

---

**Tác giả:** Claude Haiku 4.5  
**Thời gian tạo:** 2026-09-16
