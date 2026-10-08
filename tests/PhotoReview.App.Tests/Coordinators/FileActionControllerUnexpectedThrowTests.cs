using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// The "unexpected exception" catches of <see cref="FileActionController"/>: the concrete service turns its own failures into
/// results, so a throwing <see cref="IFileActionExecutor"/> is the only way to reach them. A thrown action must put the entry
/// back (INV-5) and report the failure in the folder it ran in; a throw AFTER the service reported success is bookkeeping
/// only (the file really moved); cancellation is never swallowed. No timing: every executor/present completes synchronously.
/// </summary>
[Collection("GlobalState")] // CapturedAppLog swaps the process-wide AppLog
public sealed class FileActionControllerUnexpectedThrowTests : IDisposable
{
    private readonly FileActionControllerMutationGapHarness _h = new();
    private readonly ThrowSink _sink = new();
    private readonly CapturedAppLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        _h.Dispose();
    }

    private sealed class ThrowSink : IFileActionSink
    {
        public List<string> Statuses { get; } = [];
        public int CatalogChanged { get; private set; }
        public Func<Task>? Present { get; set; }
        public Exception? ThrowOnSession { get; set; }

        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) { }
        public void OnCatalogChanged(string? removedPath) => CatalogChanged++;
        public Task PresentAsync(int index) => Present?.Invoke() ?? Task.CompletedTask;

        public void UpdateSessionPath(string currentPath)
        {
            if (ThrowOnSession is not null) throw ThrowOnSession;
        }

        public void NotifyNavigationStateChanged() { }
    }

    private sealed class ScriptedExecutor(Func<FileActionRequest, Task<FileActionResult>>? single = null,
        Func<CaptureGroupActionRequest, Task<CaptureGroupActionResult>>? group = null) : IFileActionExecutor
    {
        public bool IsBusy => false;
        public bool LacksRecycleBin(string path) => false;
        public Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default) => single!(request);
        public Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default) => group!(request);
    }

    private FileActionController NewController(IFileActionExecutor executor) =>
        new(_h.Catalog, _h.Clock, fileActionService: null, undoService: null, dialogService: null, preloadController: null,
            ManagedNaturalComparer.Instance,
            () => FileActionControllerMutationGapHarness.Settings(FileActionControllerMutationGapHarness.MoveAction()), _sink,
            fileSystem: _h.Fs)
        {
            FileActionsOverride = executor,
        };

    private static Task<FileActionResult> Succeeded(string source) =>
        Task.FromResult(new FileActionResult(true, FileOperationType.Move, source, Path.Combine("Sorted", Path.GetFileName(source)), 4, default, null));

    [Fact]
    public async Task ExecutorThrows_EntryGoesBackAtItsIndex_FailureIsReportedAndLogged()
    {
        var a = _h.Make("a.jpg");
        var b = _h.Make("b.jpg");
        var c = _h.Make("c.jpg");
        _h.Catalog.Reset([a, b, c]);
        var boom = new InvalidOperationException("boom");

        await NewController(new ScriptedExecutor(single: _ => throw boom)).RunActionAsync(0, null, b);

        Assert.Equal([a, b, c], _h.Catalog.Paths); // INV-5: b is back where it was
        Assert.Equal(StatusFormatter.ActionFailed("MoveToSub", UserFacingError.Describe(boom)), _sink.Statuses[^1]);
        Assert.Contains("File action Move threw", _log.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutorThrows_WhileThePresentOfTheNextPhotoFaults_StillRestoresAndKeepsTheRealErrorText()
    {
        var a = _h.Make("a.jpg");
        var b = _h.Make("b.jpg");
        _h.Catalog.Reset([a, b]);
        _sink.Present = () => Task.FromException(new IOException("present failed"));
        var boom = new InvalidOperationException("boom");

        await NewController(new ScriptedExecutor(single: _ => throw boom)).RunActionAsync(0, null, a);

        Assert.Equal([a, b], _h.Catalog.Paths);
        Assert.Equal(StatusFormatter.ActionFailed("MoveToSub", UserFacingError.Describe(boom)), _sink.Statuses[^1]); // not the present's text
        Assert.Contains("Present after failed file action threw", _log.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutorThrows_AfterTheUserOpenedAnotherFolder_TouchesNeitherCatalogNorStatusOfTheNewFolder()
    {
        var a = _h.Make("a.jpg");
        var z = _h.Make("z.jpg");
        var other = _h.Make("other.jpg");
        _h.Catalog.Reset([a, z]);
        var controller = NewController(new ScriptedExecutor(single: _ =>
        {
            _h.Clock.NextFolder();
            _h.Catalog.Reset([other]);
            _sink.Statuses.Clear();
            throw new InvalidOperationException("boom");
        }));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal([other], _h.Catalog.Paths); // a.jpg of the old folder must not be restored into the new catalog
        Assert.Empty(_sink.Statuses);
    }

    [Fact]
    public async Task GroupExecutorThrows_WholeCaptureGoesBackAsOneGroupedEntry()
    {
        var before = _h.Make("zbefore.jpg");
        var jpeg = _h.Make("pair.jpg");
        var raw = _h.Make("pair.cr2", 8);
        var after = _h.Make("aafter.jpg");
        _h.Catalog.Reset([new CatalogEntry(before), new CatalogEntry(jpeg), new CatalogEntry(raw), new CatalogEntry(after)], RawPairMode.PreferJpeg, null);
        var boom = new IOException("group boom");

        await NewController(new ScriptedExecutor(group: _ => throw boom)).RunActionAsync(0, null, jpeg);

        Assert.Equal([before, jpeg, after], _h.Catalog.Paths);
        var restored = _h.Catalog.Find(jpeg)!;
        Assert.NotNull(restored.CaptureGroup);
        Assert.Equal(raw, restored.CaptureGroup!.RawPath);
        Assert.Equal(StatusFormatter.ActionFailed("MoveToSub", UserFacingError.Describe(boom)), _sink.Statuses[^1]);
        Assert.True(_sink.CatalogChanged >= 2); // removed, then the restore announced to the UI
    }

    [Fact]
    public async Task SucceededAction_BookkeepingThrowsAndPresentFaults_BothAreLogged_StatusSaysCompleted_EntryNotRestored()
    {
        var a = _h.Make("a.jpg");
        var b = _h.Make("b.jpg");
        _h.Catalog.Reset([a, b]);
        _sink.ThrowOnSession = new InvalidOperationException("session write failed");
        _sink.Present = () => Task.FromException(new IOException("present failed"));

        await NewController(new ScriptedExecutor(single: request => Succeeded(request.Source))).RunActionAsync(0, null, a);

        var log = _log.Text();
        Assert.Contains("succeeded but its post-processing threw", log, StringComparison.Ordinal);
        Assert.Contains("Present after file action threw", log, StringComparison.Ordinal);
        Assert.Equal([b], _h.Catalog.Paths);
        Assert.Equal(StatusFormatter.ActionCompleted("MoveToSub"), _sink.Statuses[^1]);
    }

    [Fact]
    public async Task ExecutorCancelled_IsNotSwallowedAsAFailure_AndTheEntryIsNotReportedFailed()
    {
        var a = _h.Make("a.jpg");
        var b = _h.Make("b.jpg");
        _h.Catalog.Reset([a, b]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewController(new ScriptedExecutor(single: _ => throw new OperationCanceledException())).RunActionAsync(0, null, a));

        Assert.DoesNotContain(_sink.Statuses, status => status.Contains("MoveToSub", StringComparison.Ordinal));
        Assert.DoesNotContain("File action Move threw", _log.Text(), StringComparison.Ordinal);
    }
}
