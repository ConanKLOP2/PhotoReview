using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// FileActionController ordering guarantees: a fast failure is not overwritten by the fire-and-forget presenter, a Copy
/// leaves the in-flight present and preload alone, and Undo of a Move restores the review-order position.
/// The presenter is gated with a TaskCompletionSource so every ordering is deterministic (no delays).
/// </summary>
public sealed class FileActionControllerOrderingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_Ordering_" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly GatedSink _sink = new();
    private readonly CountingPreload _preload = new();
    private readonly UndoService _undo;
    private readonly FileActionController _controller;

    public FileActionControllerOrderingTests()
    {
        Directory.CreateDirectory(_root);
        var fs = new PhysicalFileSystem();
        var bin = new NoBin();
        var journal = new OperationJournal(new AppPaths(_root), fs, new SystemClock());
        var fileActions = new FileActionService(journal, fs, new SystemClock(), bin);
        _undo = new UndoService(journal, fs, bin, fileActions);
        var settings = new AppSettings
        {
            Actions =
            [
                new ReviewAction { Name = "MoveToSub", Operation = FileOperationType.Move, Destination = "Sorted" },
                new ReviewAction { Name = "CopyToBackup", Operation = FileOperationType.Copy, Destination = "Backup" },
            ]
        };
        _controller = new FileActionController(
            _catalog, _clock, fileActions, _undo, dialogService: null, _preload,
            ManagedNaturalComparer.Instance, () => settings, _sink, fileSystem: fs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Make(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    [Fact(DisplayName = "A Move that fails before its first await keeps its failure message after the presenter finishes")]
    public async Task FastFailedMove_FailureStatus_IsNotOverwrittenByPresenter()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Make(Path.Combine("Sorted", "a.jpg")); // destination exists: the service fails synchronously
        _catalog.Reset([a, b]);
        _sink.HoldPresenter();

        var run = _controller.RunActionAsync(0, null, a);
        // With the presenter still running, the failure must not be published yet (it would be overwritten).
        Assert.False(run.IsCompleted);
        _sink.ReleasePresenter();
        await run;

        Assert.Equal(StatusFormatter.ActionFailed("MoveToSub", Tr.CoreFileActionDestinationExists(Path.Combine(_root, "Sorted", "a.jpg"))), _sink.LastStatus);
        Assert.Equal([a, b], _catalog.Paths); // INV-5: the photo is back
    }

    [Fact(DisplayName = "A Copy does not invalidate the in-flight present or cancel preload")]
    public async Task Copy_DoesNotStopReadsOrCancelPreload()
    {
        var a = Make("a.jpg");
        _catalog.Reset([a, Make("b.jpg")]);
        var navigation = _clock.CurrentNavigation;
        var folder = _clock.CurrentFolder;

        await _controller.RunActionAsync(1, null, a);

        Assert.True(File.Exists(Path.Combine(_root, "Backup", "a.jpg")));
        Assert.Equal(0, _preload.CancelCalls);
        Assert.Equal(navigation, _clock.CurrentNavigation);
        Assert.Equal(folder, _clock.CurrentFolder);
    }

    [Fact(DisplayName = "A Move still invalidates reads and cancels preload")]
    public async Task Move_StillStopsReadsAndCancelsPreload()
    {
        var a = Make("a.jpg");
        _catalog.Reset([a, Make("b.jpg")]);
        var navigation = _clock.CurrentNavigation;

        await _controller.RunActionAsync(0, null, a);

        Assert.Equal(1, _preload.CancelCalls);
        Assert.NotEqual(navigation, _clock.CurrentNavigation);
    }

    [Fact(DisplayName = "Undo of a Move puts the photo back after its former predecessor even when the catalog is not in name order")]
    public async Task UndoMove_RestoresReviewOrderPosition_NotNameOrder()
    {
        var c = Make("c.jpg");
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        _catalog.Reset([c, a, b]); // e.g. Z-A / size / Explorer order
        await _controller.RunActionAsync(0, null, a);
        Assert.Equal([c, b], _catalog.Paths);

        var result = await _controller.UndoLastAsync(_root);

        Assert.True(result!.Succeeded);
        Assert.Equal([c, a, b], _catalog.Paths); // name order would have produced a, c, b
    }

    [Fact(DisplayName = "Undo of a Move of the first photo restores it to the first slot")]
    public async Task UndoMove_OfFirstPhoto_RestoresAtFront()
    {
        var z = Make("z.jpg");
        var a = Make("a.jpg");
        _catalog.Reset([z, a]);
        await _controller.RunActionAsync(0, null, z);

        await _controller.UndoLastAsync(_root);

        Assert.Equal([z, a], _catalog.Paths);
    }

    private sealed class GatedSink : IFileActionSink
    {
        private TaskCompletionSource? _gate;
        public string? LastStatus { get; private set; }
        public void SetStatusText(string status) => LastStatus = status;
        public void ShowLateActionStatus(string status) { }
        public void OnCatalogChanged(string? removedPath) { }
        public void UpdateSessionPath(string currentPath) { }
        public void NotifyNavigationStateChanged() { }

        public void HoldPresenter() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleasePresenter() => _gate!.SetResult();

        public async Task PresentAsync(int index)
        {
            if (_gate is not null) await _gate.Task;
            LastStatus = "presented:" + index; // like ImagePresenter.UpdateStatus after the decode
        }
    }

    private sealed class CountingPreload : IPreloadController
    {
        public int CancelCalls { get; private set; }
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() => CancelCalls++;
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    /// <summary>Never touches the real Recycle Bin (AGENTS.md); these tests use Move and Copy only.</summary>
    private sealed class NoBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new IOException("not used");
        public void DeletePermanently(string path) => throw new IOException("not used");
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
