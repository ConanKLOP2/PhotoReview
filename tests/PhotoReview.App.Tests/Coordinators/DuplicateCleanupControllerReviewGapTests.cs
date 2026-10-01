using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// RV-T23: the batch-review dialog returns true, but the catalog lost some (or all) of the files while it was open
/// (the dialog runs a nested dispatcher loop). Only the files still in the catalog may be recycled.
/// </summary>
public sealed class DuplicateCleanupControllerReviewGapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_DupReview_" + Guid.NewGuid().ToString("N"));

    public DuplicateCleanupControllerReviewGapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly string[] BatchNames = ["a.jpg", "a (1).jpg", "a (2).jpg"];

    private (DuplicateCleanupController Controller, ReviewCatalog Catalog, RecordingSink Sink, CountingBin Bin, ReviewingDialog Dialog, string[] Files) NewBatch()
    {
        var folder = Path.Combine(_root, "album");
        Directory.CreateDirectory(folder);
        var files = BatchNames.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, new byte[2048]);
        var catalog = new ReviewCatalog();
        catalog.Reset(files);
        var fs = new PhysicalFileSystem();
        var bin = new CountingBin();
        var fileActions = new FileActionService(new OperationJournal(new AppPaths(_root), fs, new SystemClock()), fs, new SystemClock(), bin);
        var sink = new RecordingSink();
        var dialog = new ReviewingDialog();
        var controller = new DuplicateCleanupController(
            new GenerationClock(), catalog, fileActions, new FileHashService(new SourceBytesCache(1024 * 1024)),
            fs, dialog, new InlineUiScheduler(),
            preloadController: null, thumbnailCache: null, previewService: null, sink);
        return (controller, catalog, sink, bin, dialog, files);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_ReviewConfirmedButOneFileLeftTheCatalog_RecyclesOnlyTheRemainingOne()
    {
        var (controller, catalog, sink, bin, dialog, files) = NewBatch();
        dialog.OnReview = paths =>
        {
            Assert.Equal([files[1], files[2]], paths); // both numbered copies were offered
            catalog.Remove(files[1]);
        };

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal([files[2]], bin.Recycled);
        Assert.True(File.Exists(files[1]));
        Assert.False(File.Exists(files[2]));
        Assert.Equal(StatusFormatter.BatchDone(1, 0), sink.Statuses[^1]);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_ReviewConfirmedButEveryFileLeftTheCatalog_RecyclesNothingAndReportsBatchCanceled()
    {
        var (controller, catalog, sink, bin, dialog, files) = NewBatch();
        dialog.OnReview = _ =>
        {
            catalog.Remove(files[1]);
            catalog.Remove(files[2]);
        };

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Empty(bin.Recycled);
        Assert.True(File.Exists(files[1]) && File.Exists(files[2]));
        Assert.Equal(StatusFormatter.BatchCanceled(), sink.Statuses[^1]);
        Assert.Equal(0, sink.Reloads);
    }

    private sealed class CountingBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];

        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            File.Delete(path); // never the real Recycle Bin
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class RecordingSink : IDuplicateCleanupSink
    {
        public List<string> Statuses { get; } = [];
        public int Reloads { get; private set; }
        public void SetStatusText(string status) => Statuses.Add(status);

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

    private sealed class ReviewingDialog : IDialogService
    {
        public Action<IReadOnlyList<string>>? OnReview { get; set; }

        public bool ShowBatchReview(IReadOnlyList<string> paths)
        {
            OnReview?.Invoke(paths);
            return true;
        }

        public bool ShowConfirmation(string title, string message) => true;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }
}
