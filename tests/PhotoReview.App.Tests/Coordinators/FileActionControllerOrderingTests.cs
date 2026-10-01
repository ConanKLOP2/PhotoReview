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
    private readonly OperationJournal _journal;
    private readonly FileActionService _fileActions;
    private readonly PhysicalFileSystem _fs = new();

    public FileActionControllerOrderingTests()
    {
        Directory.CreateDirectory(_root);
        var fs = _fs;
        var bin = new NoBin();
        _journal = new OperationJournal(new AppPaths(_root), fs, new SystemClock());
        var fileActions = new FileActionService(_journal, fs, new SystemClock(), bin, (source, destination) =>
        {
            if (source.EndsWith("pair.cr2", StringComparison.OrdinalIgnoreCase)) throw new IOException("simulated RAW move failure");
            File.Move(source, destination);
            return Task.CompletedTask;
        });
        _fileActions = fileActions;
        _undo = new UndoService(_journal, fs, bin, fileActions);
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

    // RV-A09: the fire-and-forget present of the next photo writes "Ready" when it finishes; the final "Moved to" must come after it.
    // Deterministic: the test body runs on a manually pumped SynchronizationContext, so every continuation of the controller
    // runs exactly when the test says (no timing, no delays).
    [Fact(DisplayName = "Move to folder: the next photo's present still running does not overwrite the final \"Moved to\" status")]
    public void MoveToFolder_NextPresentStillRunning_FinalStatusIsMovedTo()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        var dest = Path.Combine(_root, "Dest");
        Directory.CreateDirectory(dest);
        _catalog.Reset([a, b]);
        var settings = new AppSettings { MoveCopyReuseLastFolder = true, LastMoveToFolder = dest };
        var controller = new FileActionController(
            _catalog, _clock, _fileActions, _undo, dialogService: null, _preload,
            ManagedNaturalComparer.Instance, () => settings, _sink, fileSystem: _fs);
        var movedTo = Tr.StatusMovedToFolder("a.jpg", dest);
        using var coreFinished = new ManualResetEventSlim(false);
        _sink.OnNavigationStateChanged = coreFinished.Set; // the core run's finally: the file operation is done
        _sink.HoldPresenter();

        var previous = SynchronizationContext.Current;
        var pump = new PumpedSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var run = controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: false, () => (null, a));

            // Let the file operation finish, then run every continuation that is ready. Unfixed code writes "Moved to" (and
            // completes) here, while the presenter is still held; fixed code is now waiting for the presenter.
            pump.RunUntil(() => coreFinished.IsSet, TimeSpan.FromSeconds(30));
            pump.RunQueued();

            _sink.ReleasePresenter(); // the presenter finishes and writes its own status
            pump.RunUntil(() => run.IsCompleted && _sink.LastPresent!.IsCompleted, TimeSpan.FromSeconds(30));
            run.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(movedTo, _sink.LastStatus);
        Assert.True(File.Exists(Path.Combine(dest, "a.jpg")));
    }

    /// <summary>Queues every Post/Send; the test thread runs them explicitly (single-threaded, ordered).</summary>
    private sealed class PumpedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Runs everything queued right now (and what that queues in turn) until the queue is empty.</summary>
        public void RunQueued()
        {
            while (_queue.TryTake(out var item)) item.Callback(item.State);
        }

        /// <summary>Runs queued work, blocking for the next item (no polling), until the condition holds.</summary>
        public void RunUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(remaining > TimeSpan.Zero, "Timed out waiting for the controller.");
                if (_queue.TryTake(out var item, remaining)) item.Callback(item.State);
            }
        }
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

    [Fact]
    public async Task GroupMovePartialFailure_RollsBackAndRestoresTheWholeGroupWithoutARecoveryEntry()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        _catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        Assert.NotNull(_catalog.Find(jpeg)?.CaptureGroup);

        await _controller.RunActionAsync(0, null, jpeg);

        // The RAW move failed after the JPEG had moved: the JPEG was put back, so the pair is whole on disk and in the catalog.
        Assert.True(File.Exists(jpeg));
        Assert.True(File.Exists(raw));
        Assert.Single(_catalog.Paths);
        Assert.Equal(jpeg, _catalog.PathAt(0));
        Assert.NotNull(_catalog.Find(jpeg)?.CaptureGroup);
        // Fully rolled back: the disk is unchanged, so nothing is left to retry (terminal Dismissed record, no Recovery item).
        Assert.Empty(_journal.ReadFailedOperations());
        Assert.Empty(_journal.ReadPendingOperations());
        Assert.Equal(0, _catalog.IndexOf(jpeg));
        Assert.Equal(0, _catalog.IndexOf(raw));
    }

    private sealed class GatedSink : IFileActionSink
    {
        private TaskCompletionSource? _gate;
        public string? LastStatus { get; private set; }
        public void SetStatusText(string status) => LastStatus = status;
        public void ShowLateActionStatus(string status) { }
        public void OnCatalogChanged(string? removedPath) { }
        public void UpdateSessionPath(string currentPath) { }
        public Action? OnNavigationStateChanged { get; set; }
        public void NotifyNavigationStateChanged() => OnNavigationStateChanged?.Invoke();

        public void HoldPresenter() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleasePresenter() => _gate!.SetResult();

        /// <summary>The most recent present started (the fire-and-forget one of an action), to wait for its end.</summary>
        public Task? LastPresent { get; private set; }

        public Task PresentAsync(int index) => LastPresent = PresentCoreAsync(index);

        private async Task PresentCoreAsync(int index)
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
