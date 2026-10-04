using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// R08: after the file operation the controller awaits the next photo's presentation. The user may open another folder during
/// that await, so an old action's failure text, session path or "completed" status must never be written into the newly
/// opened folder. The executor is a seam that completes synchronously, so the controller is parked on the present task when the
/// test switches folder (no delays, no polling).
/// </summary>
public sealed class FileActionControllerFolderScopeTests : IDisposable
{
    private readonly FileActionControllerMutationGapHarness _h = new();
    private readonly TaskCompletionSource _presentGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ScopeSink _sink = new();

    public void Dispose() => _h.Dispose();

    private sealed class ScopeSink : IFileActionSink
    {
        public List<string> Statuses { get; } = [];
        public List<string> Sessions { get; } = [];
        public Task? PresentTask { get; set; }
        public Exception? ThrowOnSession { get; set; }

        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) { }
        public void OnCatalogChanged(string? removedPath) { }
        public Task PresentAsync(int index) => PresentTask ?? Task.CompletedTask;

        public void UpdateSessionPath(string currentPath)
        {
            if (ThrowOnSession is not null) throw ThrowOnSession;
            Sessions.Add(currentPath);
        }

        public void NotifyNavigationStateChanged() { }
    }

    private sealed class FakeExecutor(Func<FileActionRequest, Task<FileActionResult>> execute) : IFileActionExecutor
    {
        public bool IsBusy => false;
        public bool LacksRecycleBin(string path) => false;
        public Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default) => execute(request);
        public Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static FileActionResult Result(string source, bool succeeded, bool sourceRemoved = false) =>
        new(succeeded, FileOperationType.Move, source, Path.Combine("Sorted", Path.GetFileName(source)), 4, default,
            succeeded ? null : "simulated failure", SourceRemoved: sourceRemoved);

    /// <summary>
    /// Opens "folder A" (a.jpg, z.jpg), runs the Move action with <paramref name="execute"/>, and returns while the controller
    /// is parked on the present task. <paramref name="switchFolder"/> then opens "folder B" before the present completes.
    /// </summary>
    private async Task<(string FolderBFile, string A)> RunMoveParkedOnPresentAsync(Func<string, Task<FileActionResult>> execute, bool switchFolder)
    {
        var a = _h.Make("a.jpg");
        var z = _h.Make("z.jpg");
        var b = _h.Make("b.jpg");
        _h.Catalog.Reset([a, z]);
        _sink.PresentTask = _presentGate.Task;
        var controller = new FileActionController(
            _h.Catalog, _h.Clock, fileActionService: null, undoService: null, dialogService: null, preloadController: null,
            PhotoReview.Core.Catalog.ManagedNaturalComparer.Instance,
            () => FileActionControllerMutationGapHarness.Settings(FileActionControllerMutationGapHarness.MoveAction()), _sink,
            fileSystem: _h.Fs)
        {
            FileActionsOverride = new FakeExecutor(request => execute(request.Source)),
        };

        var run = controller.RunActionAsync(0, null, a);
        Assert.False(run.IsCompleted); // parked on the present task of the next photo
        if (switchFolder)
        {
            _h.Clock.NextFolder();
            _h.Catalog.Reset([b]);
            _sink.Statuses.Clear();
            _sink.Sessions.Clear();
        }

        _presentGate.SetResult();
        await run;
        return (b, a);
    }

    [Theory(DisplayName = "R08: a failed Move whose present completes after a folder switch writes no failure text into the new folder")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMove_PresentCompletesAfterFolderSwitch_WritesNoStatusIntoNewFolder(bool switchFolder)
    {
        var (b, a) = await RunMoveParkedOnPresentAsync(source => Task.FromResult(Result(source, succeeded: false)), switchFolder);

        if (switchFolder)
        {
            Assert.Empty(_sink.Statuses);
            Assert.Equal([b], _h.Catalog.Paths);
        }
        else
        {
            Assert.Equal([StatusFormatter.ActionFailed("MoveToSub", "simulated failure")], _sink.Statuses);
            Assert.Contains(a, _h.Catalog.Paths); // INV-5: the photo is back in the same folder's catalog
        }
    }

    [Theory(DisplayName = "R08: an unverified Move (source already gone) completing its present after a folder switch writes neither session path nor status into the new folder")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedMove_PresentCompletesAfterFolderSwitch_WritesNothingIntoNewFolder(bool switchFolder)
    {
        await RunMoveParkedOnPresentAsync(source => Task.FromResult(Result(source, succeeded: false, sourceRemoved: true)), switchFolder);

        if (switchFolder)
        {
            Assert.Empty(_sink.Statuses);
            Assert.Empty(_sink.Sessions);
        }
        else
        {
            Assert.Equal([Tr.StatusMoveUnverified("a.jpg")], _sink.Statuses);
            Assert.Single(_sink.Sessions);
        }
    }

    [Theory(DisplayName = "R08: an executor that throws, with the present completing after a folder switch, writes no failure text into the new folder")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingExecutor_PresentCompletesAfterFolderSwitch_WritesNoStatusIntoNewFolder(bool switchFolder)
    {
        await RunMoveParkedOnPresentAsync(_ => throw new InvalidOperationException("boom"), switchFolder);

        if (switchFolder)
            Assert.Empty(_sink.Statuses);
        else
            Assert.Single(_sink.Statuses); // the failure is reported in the folder it happened in
    }

    [Theory(DisplayName = "R08: a successful Move whose bookkeeping threw, with the present completing after a folder switch, writes no 'completed' status into the new folder")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SucceededMoveWithThrowingBookkeeping_PresentCompletesAfterFolderSwitch_WritesNoStatusIntoNewFolder(bool switchFolder)
    {
        _sink.ThrowOnSession = new InvalidOperationException("session write failed");

        await RunMoveParkedOnPresentAsync(source => Task.FromResult(Result(source, succeeded: true)), switchFolder);

        if (switchFolder)
            Assert.Empty(_sink.Statuses);
        else
            Assert.Equal([StatusFormatter.ActionCompleted("MoveToSub")], _sink.Statuses);
    }
}
