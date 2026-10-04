using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Stryker round 1 (App): the result reporting of the duplicate batch cleanup -- counts when every recycle fails (also
/// after a folder switch), no reload when nothing was recycled, the error dialog only for real failures. Fake bin only.
/// </summary>
public sealed class DuplicateCleanupControllerMutationGapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_DupMutGap_" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly RecordingSink _sink = new();
    private readonly FailingBin _bin = new();
    private readonly RecordingDialog _dialog = new();

    public DuplicateCleanupControllerMutationGapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly string[] BatchNames = ["a.jpg", "a (1).jpg", "a (2).jpg"];

    private DuplicateCleanupController NewBatch(bool withDialog, bool withService = true)
    {
        var folder = Path.Combine(_root, "album");
        Directory.CreateDirectory(folder);
        var files = BatchNames.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, new byte[2048]);
        var catalog = new ReviewCatalog();
        catalog.Reset(files);
        var fs = new PhysicalFileSystem();
        var fileActions = withService
            ? new FileActionService(new OperationJournal(new AppPaths(_root), fs, new SystemClock()), fs, new SystemClock(), _bin)
            : null;
        return new DuplicateCleanupController(
            _clock, catalog, fileActions, new FileHashService(new SourceBytesCache(1024 * 1024)),
            fs, withDialog ? _dialog : null, new InlineUiScheduler(),
            preloadController: null, thumbnailCache: null, previewService: null, _sink);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_NoFileActionService_ReturnsWithoutAnyStatus()
    {
        var controller = NewBatch(withDialog: false, withService: false);

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Empty(_sink.Statuses);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_EveryRecycleFailsAfterTheFolderChanged_ReportsTheFailureCountLate()
    {
        _bin.OnRecycle = () => { _clock.NextFolder(); throw new IOException("locked"); };
        var controller = NewBatch(withDialog: false);

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal([Tr.StatusLateBatchRecycled(0, 2)], _sink.LateStatuses);
        Assert.Equal(0, _sink.Reloads);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_EveryRecycleFails_DoesNotReloadTheFolderAndReportsTheFailures()
    {
        _bin.OnRecycle = () => throw new IOException("locked");
        var controller = NewBatch(withDialog: false);

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(0, _sink.Reloads);
        Assert.Equal(StatusFormatter.BatchDone(0, 2), _sink.Statuses[^1]);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_AllRecyclesSucceedWithADialog_NeverShowsTheErrorDialog()
    {
        var controller = NewBatch(withDialog: true);

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Empty(_dialog.Errors);
        Assert.Equal(StatusFormatter.BatchDone(2, 0), _sink.Statuses[^1]);
        Assert.Equal(1, _sink.Reloads);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_SomeRecyclesFailWithADialog_ShowsOneErrorDialogListingEachFailure()
    {
        _bin.OnRecycle = () => throw new IOException("locked");
        var controller = NewBatch(withDialog: true);

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        var error = Assert.Single(_dialog.Errors);
        Assert.Equal(Tr.DialogBatchErrorsTitle, error.Title);
        Assert.Contains("a (1).jpg", error.Message, StringComparison.Ordinal);
        Assert.Contains("a (2).jpg", error.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingExecutor(Exception toThrow) : IFileActionExecutor
    {
        public bool IsBusy => false;

        public bool LacksRecycleBin(string path) => false;

        public Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default)
            => throw toThrow;

        public Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default)
            => throw toThrow;
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_AnUnexpectedThrowForOneFile_DoesNotAbortTheBatchAndIsReported()
    {
        var controller = NewBatch(withDialog: true);
        controller.FileActionsOverride = new ThrowingExecutor(new InvalidOperationException("boom"));

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(StatusFormatter.BatchDone(0, 2), _sink.Statuses[^1]);
        var error = Assert.Single(_dialog.Errors);
        Assert.Contains("a (1).jpg", error.Message, StringComparison.Ordinal);
        Assert.Contains("a (2).jpg", error.Message, StringComparison.Ordinal); // the second file was still attempted
        Assert.Equal(0, _sink.Reloads);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_ACancellationFromTheService_IsNotSwallowedAsAFileFailure()
    {
        var controller = NewBatch(withDialog: false);
        controller.FileActionsOverride = new ThrowingExecutor(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RemoveDuplicatesAsync(removeNumbered: true));
        Assert.DoesNotContain(StatusFormatter.BatchDone(0, 2), _sink.Statuses);
    }

    [Fact]
    public async Task ClearCacheAsync_AnUnexpectedDiskFailure_IsReportedAsFailedNotAsCleared()
    {
        var controller = NewBatch(withDialog: false);
        controller.ClearDiskCaches = () => throw new InvalidOperationException("disk exploded");

        await controller.ClearCacheAsync();

        var status = Assert.Single(_sink.Statuses);
        Assert.Equal(StatusFormatter.ActionFailed(Tr.DialogClearCacheTitle, UserFacingError.Describe(new InvalidOperationException("disk exploded"))), status);
    }

    [Fact]
    public async Task ClearCacheAsync_ACancelledDiskClear_PropagatesInsteadOfBeingReported()
    {
        var controller = NewBatch(withDialog: false);
        controller.ClearDiskCaches = () => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(controller.ClearCacheAsync);
        Assert.Empty(_sink.Statuses);
    }

    private sealed class FailingBin : IRecycleBin
    {
        public Action? OnRecycle { get; set; }

        public void SendToRecycleBin(string path)
        {
            OnRecycle?.Invoke();
            File.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class RecordingSink : IDuplicateCleanupSink
    {
        public List<string> Statuses { get; } = [];
        public List<string> LateStatuses { get; } = [];
        public int Reloads { get; private set; }
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) => LateStatuses.Add(status);

        public Task OpenFolderAsync(string folder, string? initialPath = null)
        {
            Reloads++;
            return Task.CompletedTask;
        }
    }

    private sealed class InlineUiScheduler : IUiScheduler
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingDialog : IDialogService
    {
        public List<(string Title, string Message)> Errors { get; } = [];

        public bool ShowBatchReview(IReadOnlyList<string> paths) => true;
        public bool ShowConfirmation(string title, string message) => true;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) => Errors.Add((title, message));
        public string? PickFolder(string? initialFolder = null) => null;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }
}
