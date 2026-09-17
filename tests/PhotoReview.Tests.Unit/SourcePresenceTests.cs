using System.IO;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// These checks assert the PRESENCE OF SOURCE TEXT in PhotoReview.App, not runtime behavior.
/// Each one covers glue
/// that needs a live WPF Window (STA + Application context), a real HWND, the user's real
/// LocalAppData, or the Windows registry, so it cannot be driven headlessly.
/// </summary>
public sealed class SourcePresenceTests
{
    private static string MainWindow => ProjectSources.MainWindow;
    private static string MainWindowXaml => ProjectSources.MainWindowXaml;
    private static string AppSettingsSource => ProjectSources.AppSettingsSource;
    private static string SettingsWindow => ProjectSources.SettingsWindow;
    private static string SettingsWindowXaml => ProjectSources.SettingsWindowXaml;
    private static string ImageSortServiceSource => ProjectSources.ImageSortService;

    // ---- MainWindow file-action glue ----
    // The advance-before-action ordering lives in MainWindow's async event handlers and touches
    // _files/_index/StatusText directly. InterleavedFileActionSequenceTests asserts the same
    // ordering contract behaviorally against a catalog model; these only pin the wiring.

    [Fact(DisplayName = "Interleaved actions advance viewer before filesystem operation and exactly once (source presence, not behavior)")]
    public void InterleavedActionsAdvanceViewerBeforeFilesystemOperation() =>
        Assert.True(MainWindow.Contains("AdvanceBeforeFileActionAsync(sourcePath, removeSource: true)")
            && MainWindow.Contains("AdvanceBeforeFileActionAsync(sourcePath, removeSource: operation == \"Move\")")
            && MainWindow.Contains("Do not call ShowImageAsync after action"));

    [Fact(DisplayName = "Interleaved actions reject duplicate concurrent file actions (source presence; FileActionConcurrencyTests covers the behavior)")]
    public void InterleavedActionsRejectDuplicateConcurrentFileActions() =>
        Assert.Contains("Interlocked.Exchange(ref _fileActionInProgress, 1)", MainWindow, StringComparison.Ordinal);

    [Fact(DisplayName = "MainWindow reads LoadingMode (source presence, not behavior)")]
    public void MainWindowReadsLoadingMode() =>
        Assert.Contains("LoadingMode", MainWindow, StringComparison.Ordinal);

    [Fact(DisplayName = "MainWindow loading modes have thumbnail/preview contract (source presence, not behavior)")]
    public void MainWindowLoadingModesHaveThumbnailPreviewContract() =>
        Assert.True(MainWindow.Contains("Thumbnail", StringComparison.Ordinal)
            && MainWindow.Contains("Preview", StringComparison.Ordinal));

    // ---- Keyboard and shortcut wiring ----
    // The default mappings are asserted for real; what stays a grep is the MainWindow KeyDown
    // handler wiring, which only runs against a live WPF Window and routed input events.

    [Fact(DisplayName = "Keyboard navigation uses arrow keys (defaults asserted; handler wiring is source presence)")]
    public void KeyboardNavigationUsesArrowKeys()
    {
        var shortcuts = ShortcutMappings.Default();
        Assert.True(shortcuts.Next == "Right" && shortcuts.Previous == "Left"
            && MainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Next)")
            && MainWindow.Contains("Matches(e.Key, _settings.Shortcuts.Previous)"));
    }

    [Fact(DisplayName = "Enter/action profiles drive configurable operations (defaults asserted; handler wiring is source presence)")]
    public void EnterActionProfilesDriveConfigurableOperations() =>
        Assert.True(ShortcutMappings.Default().MoveToFolder2 == "Enter"
            && MainWindow.Contains("ReviewAction") && MainWindow.Contains("ExecuteActionAsync(action)"));

    [Fact(DisplayName = "Delete maps to Recycle Bin (default asserted; handler wiring is source presence)")]
    public void DeleteMapsToRecycleBin() =>
        Assert.True(ShortcutMappings.Default().SendToRecycleBin == "Delete"
            && MainWindow.Contains("ClassifyCurrentAsync(3)"));

    // File actions must capture paths and advance the viewer before any blocking
    // filesystem/Shell call. The action completion must not advance a second time.

    [Fact(DisplayName = "File actions advance viewer before filesystem operation (source presence, not behavior)")]
    public void FileActionsAdvanceViewerBeforeFilesystemOperation() =>
        Assert.Contains("AdvanceBeforeFileActionAsync", MainWindow, StringComparison.Ordinal);

    [Fact(DisplayName = "File actions snapshot source and next paths (source presence, not behavior)")]
    public void FileActionsSnapshotSourceAndNextPaths() =>
        Assert.True(MainWindow.Contains("sourcePath", StringComparison.Ordinal)
            && MainWindow.Contains("nextPath", StringComparison.Ordinal));

    [Fact(DisplayName = "Filesystem action is detached from UI thread (source presence, not behavior)")]
    public void FilesystemActionIsDetachedFromUiThread() =>
        Assert.True(MainWindow.Contains("FileActionResult", StringComparison.Ordinal)
            || MainWindow.Contains("Task.Run", StringComparison.Ordinal));

    [Fact(DisplayName = "File action completion does not advance twice (source presence, not behavior)")]
    public void FileActionCompletionDoesNotAdvanceTwice() =>
        Assert.True(MainWindow.Contains("Do not call ShowImageAsync after action")
            || MainWindow.Contains("advance exactly once", StringComparison.OrdinalIgnoreCase));

    [Fact(DisplayName = "No number-1 shortcut required")]
    public void NoNumberOneShortcutRequired() =>
        Assert.True(!MainWindow.Contains("Key.D1") && !MainWindow.Contains("Key.NumPad1"));

    [Fact(DisplayName = "Space skip shortcut exists (source presence, not behavior)")]
    public void SpaceSkipShortcutExists() =>
        Assert.True(AppSettingsSource.Contains("Skip") && MainWindow.Contains("Shortcuts.Skip"));

    [Fact(DisplayName = "Skip, undo, and fullscreen shortcuts are configurable (source presence, not behavior)")]
    public void SkipUndoFullscreenShortcutsAreConfigurable() =>
        Assert.True(AppSettingsSource.Contains("Skip") && AppSettingsSource.Contains("Undo")
            && AppSettingsSource.Contains("Fullscreen") && MainWindow.Contains("Shortcuts.Skip")
            && MainWindow.Contains("Shortcuts.Undo") && MainWindow.Contains("Shortcuts.Fullscreen"));

    [Fact(DisplayName = "Shortcut conflicts are validated across global and action bindings (source presence; the validator itself is asserted behaviorally below)")]
    public void ShortcutConflictsAreValidatedAcrossScopes() =>
        Assert.True(AppSettingsSource.Contains("ValidateShortcuts") && SettingsWindow.Contains("ValidateShortcuts"));

    [Fact(DisplayName = "Configured shortcuts navigate sibling folders (source presence; SiblingFolderService is asserted behaviorally below)")]
    public void ConfiguredShortcutsNavigateSiblingFolders() =>
        Assert.True(MainWindow.Contains("Shortcuts.NextFolder") && MainWindow.Contains("Shortcuts.PreviousFolder")
            && MainWindow.Contains("NavigateSiblingFolderAsync"));

    [Fact(DisplayName = "Home navigates to first image (source presence, not behavior)")]
    public void HomeNavigatesToFirstImage() =>
        Assert.True(MainWindow.Contains("Shortcuts.FirstImage") && MainWindow.Contains("ShowImageAsync(0)"));

    [Fact(DisplayName = "Configurable navigation and zoom shortcuts are wired at runtime (source presence, not behavior)")]
    public void ConfigurableNavigationAndZoomShortcutsAreWired() =>
        Assert.True(MainWindow.Contains("_settings.Shortcuts.NextFolder")
            && MainWindow.Contains("_settings.Shortcuts.FirstImage")
            && MainWindow.Contains("_settings.Shortcuts.ZoomIn"));

    // ---- Drag-drop, compare panel and Settings dialog wiring ----
    // These assert XAML attributes and event-handler names on Window subclasses. Constructing
    // them needs an STA thread plus an Application context; the underlying services are each
    // asserted behaviorally elsewhere in this suite.

    [Fact(DisplayName = "Drag-drop events are wired on the main window (source presence; DragDropInputService is asserted behaviorally above)")]
    public void DragDropEventsAreWiredOnMainWindow() =>
        Assert.True(MainWindowXaml.Contains("AllowDrop=\"True\"")
            && MainWindow.Contains("Window_PreviewDragOver") && MainWindow.Contains("Window_Drop"));

    [Fact(DisplayName = "Compare shortcut toggles compare panel (source presence, not behavior)")]
    public void CompareShortcutTogglesComparePanel() =>
        Assert.True(MainWindow.Contains("_settings.Shortcuts.Compare") && MainWindow.Contains("ComparePanel.Visibility"));

    [Fact(DisplayName = "Compare hash is optional (source presence, not behavior)")]
    public void CompareHashIsOptional() =>
        Assert.True(AppSettingsSource.Contains("CompareHashEnabled") && MainWindow.Contains("CompareHashEnabled")
            && SettingsWindow.Contains("CompareHashCheck"));

    [Fact(DisplayName = "Compare size is optional (source presence, not behavior)")]
    public void CompareSizeIsOptional() =>
        Assert.True(AppSettingsSource.Contains("CompareSizeEnabled") && MainWindow.Contains("CompareSizeEnabled")
            && SettingsWindow.Contains("CompareSizeCheck"));

    [Fact(DisplayName = "Compare hashes are requested concurrently (source presence; FileHashService concurrency is asserted behaviorally below)")]
    public void CompareHashesAreRequestedConcurrently() =>
        Assert.Contains("Task.WhenAll(GetHashAsync(pair.Value.Left), GetHashAsync(pair.Value.Right))",
            MainWindow, StringComparison.Ordinal);

    [Fact(DisplayName = "Settings defaults reset compare options (source presence, not behavior)")]
    public void SettingsDefaultsResetCompareOptions() =>
        Assert.True(SettingsWindow.Contains("CompareHashEnabled = true")
            && SettingsWindow.Contains("CompareSizeEnabled = true"));

    [Fact(DisplayName = "Recovery retry is exposed with confirmation in UI (source presence, not behavior)")]
    public void RecoveryRetryIsExposedWithConfirmation() =>
        Assert.True(ProjectSources.RecoveryWindowXaml.Contains("Retry Move/Copy")
            && ProjectSources.RecoveryWindow.Contains("Retry_Click"));

    [Fact(DisplayName = "Recovery retry button is accessible (source presence, not behavior)")]
    public void RecoveryRetryButtonIsAccessible() =>
        Assert.Contains("AutomationProperties.Name=\"Thử lại Move hoặc Copy đã lỗi\"",
            ProjectSources.RecoveryWindowXaml, StringComparison.Ordinal);

    [Fact(DisplayName = "Name sort uses Windows Explorer logical ordering (source presence; natural ordering is asserted behaviorally below)")]
    public void NameSortUsesExplorerLogicalOrdering() =>
        Assert.True(ImageSortServiceSource.Contains("StrCmpLogicalW")
            && ImageSortServiceSource.Contains("ExplorerComparer"));

    [Fact(DisplayName = "Size sort is configurable (source presence; size ordering is asserted behaviorally below)")]
    public void SizeSortIsConfigurable() =>
        Assert.True(AppSettingsSource.Contains("Size")
            && ImageSortServiceSource.Contains("OrderByDescending(GetFileSize)")
            && SettingsWindow.Contains("Size"));

    [Fact(DisplayName = "Size sort direction is configurable (source presence; both directions are asserted behaviorally below)")]
    public void SizeSortDirectionIsConfigurable() =>
        Assert.True(ImageSortServiceSource.Contains("SizeAscending") && SettingsWindow.Contains("SizeAscending"));

    [Fact(DisplayName = "Image sorting is isolated in a testable service (source presence, not behavior)")]
    public void ImageSortingIsIsolatedInTestableService() =>
        Assert.True(File.Exists(ProjectSources.AppPath("ImageSortService.cs"))
            && MainWindow.Contains("ImageSortService.Sort"));

    // ---- MainWindow folder-open and catalog-generation internals ----
    // Folder open, native reindex and the catalog interaction generation are private async
    // MainWindow state machines over _files/_index/StatusText.

    [Fact(DisplayName = "Explorer order applies below and above 100 files and rejects stale folder results (source presence, not behavior)")]
    public void ExplorerOrderAppliesRegardlessOfCountAndRejectsStaleResults() =>
        Assert.True(!MainWindow.Contains("files.Count < 100")
            && (MainWindow.Contains("TryGetSnapshotAsync") || MainWindow.Contains("TryGetSnapshotProgressiveAsync"))
            && MainWindow.Contains("loadGeneration != _folderGeneration"));

    [Fact(DisplayName = "Direct file open waits for complete Explorer snapshot and folder size before first preload (source presence, not behavior)")]
    public void DirectFileOpenWaitsForSnapshotAndFolderSize() =>
        Assert.True(MainWindow.Contains("explorerSnapshot = await explorerTask")
            && MainWindow.Contains("_totalSourceBytes = await totalBytesTask"));

    [Fact(DisplayName = "Folder open replaces untouched fallback with the first native Explorer item (source presence, not behavior)")]
    public void FolderOpenReplacesUntouchedFallback() =>
        Assert.True(MainWindow.Contains("mayReplaceInitialFallback") && MainWindow.Contains("await ShowImageAsync(0)"));

    [Fact(DisplayName = "Native reindex refreshes counter and preload without duplicate render (source presence, not behavior)")]
    public void NativeReindexRefreshesCounterAndPreload() =>
        Assert.True(MainWindow.Contains("currentSet.SetEquals(scannedFiles)")
            && MainWindow.Contains("StatusText.Text = $\"{_index + 1}/{_files.Count}\"")
            && MainWindow.Contains("PreloadAroundAsync(_index, _generation)"));

    [Fact(DisplayName = "Explorer snapshot cannot reindex after user catalog interaction (source presence, not behavior)")]
    public void ExplorerSnapshotCannotReindexAfterCatalogInteraction() =>
        Assert.True(MainWindow.Contains("_catalogInteractionGeneration")
            && MainWindow.Contains("Explorer native order ignored after catalog interaction"));

    [Fact(DisplayName = "Navigation and file actions advance catalog interaction generation (source presence, not behavior)")]
    public void NavigationAndFileActionsAdvanceCatalogInteractionGeneration() =>
        Assert.Contains("Interlocked.Increment(ref _catalogInteractionGeneration)", MainWindow, StringComparison.Ordinal);

    [Fact(DisplayName = "Actions and batch operations require confirmation (source presence; ShowDialog needs a real WPF Window)")]
    public void ActionsAndBatchOperationsRequireConfirmation() =>
        Assert.True(MainWindow.Contains("action.Confirm") && MainWindow.Contains("BatchReviewWindow")
            && MainWindow.Contains("ShowDialog()"));

    // AppSettings.Save writes to the user's real LocalAppData config path (it does not honour
    // PHOTOREVIEW_DATA_ROOT), so the durable-save path stays a source-presence check.
    [Fact(DisplayName = "Config has versioned migration and durable atomic save (source presence, not behavior)")]
    public void ConfigHasVersionedMigrationAndDurableAtomicSave() =>
        Assert.True(AppSettingsSource.Contains("Migrate") && AppSettingsSource.Contains("Flush(flushToDisk: true)"));

    [Fact(DisplayName = "RAM cache policy targets 16 GB and full-folder preload threshold (source presence: AppConstants is internal to the app assembly)")]
    public void RamCachePolicyTargetsSixteenGbAndPreloadThreshold() =>
        Assert.True(MainWindow.Contains("AppConstants.ImageCacheCapacityBytes")
            && MainWindow.Contains("FullFolderRamThresholdBytes"));

    [Fact(DisplayName = "Hash service is isolated with bounded cache lifecycle (source presence; FileHashService is asserted behaviorally below)")]
    public void HashServiceIsIsolatedWithBoundedCacheLifecycle() =>
        Assert.True(File.Exists(ProjectSources.AppPath("FileHashService.cs"))
            && MainWindow.Contains("_hashService.Clear()"));

    [Fact(DisplayName = "Image click does not navigate; compare owns click selection (source absence, not behavior)")]
    public void ImageClickDoesNotNavigate() =>
        Assert.True(!MainWindow.Contains("Image_LeftClick") && !MainWindow.Contains("Image_RightClick")
            && !MainWindowXaml.Contains("Image_LeftClick") && !MainWindowXaml.Contains("Image_RightClick"));

    [Fact(DisplayName = "Compare selection drives file actions (source presence, not behavior)")]
    public void CompareSelectionDrivesFileActions() =>
        Assert.True(MainWindow.Contains("_compareSelectedPath ?? _files[_index]")
            && MainWindow.Contains("_files.Remove(source)"));

    [Fact(DisplayName = "Compare selection resets on navigation (source presence, not behavior)")]
    public void CompareSelectionResetsOnNavigation() =>
        Assert.True(MainWindow.Contains("_compareSelectedPath = null;")
            && MainWindow.Contains("var token = Interlocked.Increment(ref _generation);"));

    [Fact(DisplayName = "Context-menu Undo and Escape exit are wired (source presence, not behavior)")]
    public void ContextMenuUndoAndEscapeExitAreWired() =>
        Assert.True(MainWindowXaml.Contains("UndoLastAction_Click") && MainWindow.Contains("e.Key == Key.Escape")
            && MainWindow.Contains("Close();"));

    [Fact(DisplayName = "Delete Undo restores through Recycle Bin Shell (source presence: restoring needs the real Recycle Bin)")]
    public void DeleteUndoRestoresThroughRecycleBinShell() =>
        Assert.True(File.Exists(ProjectSources.AppPath("RecycleBinRestoreService.cs"))
            && MainWindow.Contains("RecycleBinRestoreService.TryRestore"));

    [Fact(DisplayName = "Primary controls expose accessible names (source presence, not behavior)")]
    public void PrimaryControlsExposeAccessibleNames() =>
        Assert.True(MainWindowXaml.Contains("AutomationProperties.Name=\"Mở thư mục ảnh\"")
            && MainWindowXaml.Contains("AutomationProperties.Name=\"Mở cài đặt\""));

    [Fact(DisplayName = "Settings exposes an accessible Open log location control (source presence, not behavior)")]
    public void SettingsExposesAccessibleOpenLogLocationControl() =>
        Assert.True(SettingsWindow.Contains("OpenLogLocation_Click")
            && SettingsWindowXaml.Contains("AutomationProperties.Name=\"Mở vị trí file log\""));

    [Fact(DisplayName = "Open log location follows the configured AppLog path (source presence, not behavior)")]
    public void OpenLogLocationFollowsConfiguredAppLogPath() =>
        Assert.True(SettingsWindow.Contains("Path.GetDirectoryName(AppLog.FilePath)")
            && SettingsWindow.Contains("explorer.exe") && SettingsWindow.Contains("AppLog.FilePath"));

    [Fact(DisplayName = "Compare previews expose accessible selection names (source presence, not behavior)")]
    public void ComparePreviewsExposeAccessibleSelectionNames() =>
        Assert.True(MainWindowXaml.Contains("Preview ảnh bên trái, nhấn để chọn")
            && MainWindowXaml.Contains("Preview ảnh bên phải, nhấn để chọn"));

    [Fact(DisplayName = "Compare previews wire up keyboard selection handlers (source presence, not behavior)")]
    public void ComparePreviewsWireUpKeyboardSelectionHandlers() =>
        Assert.True(MainWindowXaml.Contains("Focusable=\"True\"") && MainWindow.Contains("CompareLeft_KeyDown")
            && MainWindow.Contains("CompareRight_KeyDown"));

    // Window shutdown itself is WPF glue (Closed handler on the Window); the scheduler's own
    // disposal is covered behaviorally by PreloadSchedulerTests.
    [Fact(DisplayName = "Window shutdown disposes preload and thumbnail resources (source presence, not behavior)")]
    public void WindowShutdownDisposesPreloadAndThumbnailResources() =>
        Assert.True(MainWindowXaml.Contains("Closed=\"Window_Closed\"")
            && MainWindow.Contains("_thumbnailCache.Dispose()")
            && MainWindow.Contains("_preloadScheduler.Dispose()"));

    // ---- Window placement, chrome and accessibility ----
    // WindowPlacementService operates on a real HWND via GetWindowPlacement/SetWindowPlacement
    // and reads the attached monitor set, so it cannot be exercised headlessly.

    [Fact(DisplayName = "Main window restores and saves native placement instead of always using the startup default (source presence, not behavior)")]
    public void MainWindowRestoresAndSavesNativePlacement() =>
        Assert.True(MainWindowXaml.Contains("Loaded=\"Window_Loaded\"")
            && MainWindowXaml.Contains("Closing=\"Window_Closing\""));

    [Fact(DisplayName = "Main window does not force maximized state in XAML (source absence, not behavior)")]
    public void MainWindowDoesNotForceMaximizedState() =>
        Assert.DoesNotContain("WindowState=\"Maximized\"", MainWindowXaml, StringComparison.Ordinal);

    [Fact(DisplayName = "Image uses the full client area while toolbar remains a compact overlay (source presence, not behavior)")]
    public void ImageUsesFullClientAreaWithCompactToolbarOverlay() =>
        Assert.True(!MainWindowXaml.Contains("<Grid.RowDefinitions><RowDefinition Height=\"Auto\"/><RowDefinition Height=\"*\"/><RowDefinition Height=\"Auto\"/></Grid.RowDefinitions>")
            && MainWindowXaml.Contains("Panel.ZIndex=\"100\" Background=\"#B0181818\""));

    [Fact(DisplayName = "Current folder is shown in the native window title bar (source presence, not behavior)")]
    public void CurrentFolderIsShownInNativeTitleBar() =>
        Assert.True(MainWindow.Contains("Title = $\"Photo Review — {folder}")
            && MainWindowXaml.Contains("x:Name=\"FolderText\" Visibility=\"Collapsed\""));

    [Fact(DisplayName = "Each build exposes a unique informational build stamp in Settings (source presence, not behavior)")]
    public void EachBuildExposesUniqueInformationalBuildStamp() =>
        Assert.True(ProjectSources.AppCsproj.Contains("BuildStamp")
            && SettingsWindow.Contains("AssemblyInformationalVersionAttribute"));

    [Fact(DisplayName = "Native window placement restores after Loaded and persists monitor, bounds, and maximized state (source presence: needs a real Window handle)")]
    public void NativeWindowPlacementRestoresAndPersists() =>
        Assert.True(!MainWindow.Contains("Window_SourceInitialized")
            && MainWindow.Contains("WindowPlacementService.Restore(this)")
            && MainWindow.Contains("WindowPlacementService.Save(this)")
            && ProjectSources.WindowPlacementService.Contains("GetWindowPlacement")
            && ProjectSources.WindowPlacementService.Contains("SetWindowPlacement"));

    [Fact(DisplayName = "Saved placement is rejected when its monitor is no longer connected (source presence: needs real multi-monitor hardware)")]
    public void SavedPlacementIsRejectedWhenMonitorIsGone() =>
        Assert.True(ProjectSources.WindowPlacementService.Contains("Screen.AllScreens")
            && ProjectSources.WindowPlacementService.Contains("WorkingArea"));

    // ---- Disk thumbnail cache quota ----
    // ThumbnailCache prunes the shared on-disk cache under the user's real LocalAppData, so
    // driving the quota path here would mutate the developer's own cache directory.

    [Fact(DisplayName = "Disk thumbnail cache has quota and clear operation (source presence, not behavior)")]
    public void DiskThumbnailCacheHasQuotaAndClearOperation() =>
        Assert.True(ProjectSources.ThumbnailCache.Contains("DefaultMaxDiskBytes")
            && ProjectSources.ThumbnailCache.Contains("PruneDiskCache")
            && ProjectSources.ThumbnailCache.Contains("ClearDisk"));

    [Fact(DisplayName = "Disk cache cleanup tolerates filesystem access failures (source presence, not behavior)")]
    public void DiskCacheCleanupToleratesFilesystemAccessFailures() =>
        Assert.True(ProjectSources.ThumbnailCache.Contains("catch (UnauthorizedAccessException ex)")
            && ProjectSources.ThumbnailCache.Contains("catch (IOException ex)"));

    [Fact(DisplayName = "Disk cache can be cleared from UI without changing source images (source presence, not behavior)")]
    public void DiskCacheCanBeClearedFromUi() =>
        Assert.True(MainWindowXaml.Contains("ClearCache_Click") && MainWindow.Contains("_thumbnailCache.ClearDisk()"));

    // The disk-cache branch can only be entered by planting a file in the user's real
    // LocalAppData cache directory, so it stays a grep -- but against the focused service.
    [Fact(DisplayName = "Disk-cache deliveries are excluded from source byte metrics (source presence: priming the real disk cache is out of scope)")]
    public void DiskCacheDeliveriesAreExcludedFromSourceByteMetrics() =>
        Assert.True(ProjectSources.PreviewImageServiceSource.Contains("RecordDiskCacheHit")
            && ProjectSources.PreviewImageServiceSource.Contains("if (sourceRead)"));

    // RecordPresented times a WPF render pass, which needs a live viewer; only its wiring is checked.
    [Fact(DisplayName = "Viewer present latency is recorded and surfaced in diagnostics (source presence, not behavior)")]
    public void ViewerPresentLatencyIsRecordedAndSurfaced() =>
        Assert.True(File.Exists(ProjectSources.AppPath("ReviewMetrics.cs"))
            && MainWindow.Contains("RecordPresented")
            && ProjectSources.DiagnosticsWindowXaml.Contains("Source file reads"));

    [Fact(DisplayName = "Performance metrics have an in-app diagnostics view (source presence, not behavior)")]
    public void PerformanceMetricsHaveInAppDiagnosticsView() =>
        Assert.True(MainWindow.Contains("DiagnosticsWindow")
            && File.Exists(ProjectSources.AppPath("DiagnosticsWindow.xaml")));

    [Fact(DisplayName = "Batch duplicate operation has dry-run review dialog (source presence; ShowDialog needs a real WPF Window)")]
    public void BatchDuplicateOperationHasDryRunReviewDialog() =>
        Assert.True(MainWindow.Contains("BatchReviewWindow") && MainWindow.Contains("review.ShowDialog()"));

    [Fact(DisplayName = "Recovery UI exposes pending operations without replay (source presence; journal reconciliation is asserted behaviorally below)")]
    public void RecoveryUiExposesPendingOperationsWithoutReplay() =>
        Assert.True(MainWindow.Contains("RecoveryWindow") && MainWindow.Contains("ReadPendingOperations"));

    [Fact(DisplayName = "Recovery UI includes failed journal operations (source presence, not behavior)")]
    public void RecoveryUiIncludesFailedJournalOperations() =>
        Assert.True(MainWindow.Contains("ReadFailedOperations")
            && ProjectSources.OperationJournalSource.Contains("ReadFailedOperations", StringComparison.Ordinal));

    [Fact(DisplayName = "Batch operations journal success and failures (source presence; OperationJournal is asserted behaviorally below)")]
    public void BatchOperationsJournalSuccessAndFailures() =>
        Assert.True(MainWindow.Contains("FileOperationType.Recycle, JournalState.Prepared") && MainWindow.Contains("FileOperationType.Recycle, JournalState.Committed")
            && MainWindow.Contains("FileOperationType.Recycle, JournalState.Failed"));

    [Fact(DisplayName = "File association command is registered (source presence: registering needs the real Windows registry)")]
    public void FileAssociationCommandIsRegistered()
    {
        var association = Path.Combine(ProjectSources.ProjectRoot, "outputs", "install-photo-review-association.ps1");
        var associationText = File.ReadAllText(association);
        Assert.True(associationText.Contains("$progId\\shell\\open\\command") && associationText.Contains("\"%1\""));
    }
}
