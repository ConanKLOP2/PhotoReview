using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// D-02 / D-04 (same family as R08): the controller is parked on a gate (the next photo's present, or the file I/O) while the
/// test opens another folder. D-02: Move-to's own "Moved to X" status must not land in the new folder (the wrapper used to read
/// the folder identity AFTER the awaits, and the core returned true for a folder no longer current). D-04: an unverified single
/// Move (source removed, destination not verified) is announced late when the user already left the folder.
/// </summary>
public sealed class FileActionControllerFolderIdentityTests : IDisposable
{
    private readonly FileActionControllerMutationGapHarness _h = new();
    private readonly TaskCompletionSource _presentGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IdentitySink _sink = new();

    public void Dispose() => _h.Dispose();

    private sealed class IdentitySink : IFileActionSink
    {
        public List<string> Statuses { get; } = [];
        public List<string> LateStatuses { get; } = [];
        public Task? PresentTask { get; set; }
        public Exception? ThrowOnSession { get; set; }

        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) => LateStatuses.Add(status);
        public void OnCatalogChanged(string? removedPath) { }
        public Task PresentAsync(int index) => PresentTask ?? Task.CompletedTask;

        public void UpdateSessionPath(string currentPath)
        {
            if (ThrowOnSession is not null) throw ThrowOnSession;
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

    private FileActionController NewController(Func<FileActionRequest, Task<FileActionResult>> execute, bool withPicker) => new(
        _h.Catalog, _h.Clock, fileActionService: null, undoService: null, dialogService: null, preloadController: null,
        PhotoReview.Core.Catalog.ManagedNaturalComparer.Instance,
        () => FileActionControllerMutationGapHarness.Settings(FileActionControllerMutationGapHarness.MoveAction()), _sink,
        withPicker ? new FileActionControllerMutationGapHarness.FixedPicker(Path.Combine(_h.Root, "dest")) : null, _h.Fs)
    {
        FileActionsOverride = new FakeExecutor(execute),
    };

    private (string A, string B) StartInFolderA()
    {
        var a = _h.Make("a.jpg");
        var z = _h.Make("z.jpg");
        var b = _h.Make("b.jpg");
        Directory.CreateDirectory(Path.Combine(_h.Root, "dest"));
        _h.Catalog.Reset([a, z]);
        _sink.PresentTask = _presentGate.Task;
        return (a, b);
    }

    private void SwitchToFolderB(string b)
    {
        _h.Clock.NextFolder();
        _h.Catalog.Reset([b]);
        _sink.Statuses.Clear();
        _sink.LateStatuses.Clear();
    }

    [Theory(DisplayName = "D-02: Move-to whose present completes after a folder switch writes no 'Moved to' status into the new folder")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MoveTo_PresentCompletesAfterFolderSwitch_WritesNoMovedStatusIntoNewFolder(bool switchFolder, bool bookkeepingThrows)
    {
        var (a, b) = StartInFolderA();
        if (bookkeepingThrows) _sink.ThrowOnSession = new InvalidOperationException("session write failed");
        var controller = NewController(request => Task.FromResult(Result(request.Source, succeeded: true)), withPicker: true);

        var run = controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: true, () => (null, a));
        Assert.False(run.IsCompleted); // parked on the present task of the next photo
        if (switchFolder) SwitchToFolderB(b);
        _presentGate.SetResult();
        await run;

        var moved = Tr.StatusMovedToFolder("a.jpg", Path.Combine(_h.Root, "dest"));
        if (switchFolder)
            Assert.DoesNotContain(moved, _sink.Statuses);
        else
            Assert.Equal(moved, _sink.Statuses[^1]);
    }

    [Theory(DisplayName = "D-04: an unverified single Move (source removed) after a folder switch is announced through the late-status channel")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedMove_FolderSwitchedDuringIo_AnnouncedLate(bool sourceRemoved)
    {
        var (a, b) = StartInFolderA();
        var ioGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = NewController(async request =>
        {
            await ioGate.Task;
            return Result(request.Source, succeeded: false, sourceRemoved: sourceRemoved);
        }, withPicker: false);

        var run = controller.RunActionAsync(0, null, a);
        Assert.False(run.IsCompleted); // parked inside the file I/O
        SwitchToFolderB(b);
        ioGate.SetResult();
        _presentGate.SetResult();
        await run;

        if (sourceRemoved)
            Assert.Equal([Tr.StatusMoveUnverified("a.jpg")], _sink.LateStatuses);
        else
            Assert.Empty(_sink.LateStatuses); // a plain failure changed nothing on disk: nothing to announce
        Assert.Empty(_sink.Statuses);
        Assert.Equal([b], _h.Catalog.Paths);
    }
}
