using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Tests.ViewModels;
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
/// RV-T20 / RV-T21 / RV-T22 gap tests for <see cref="FileActionController"/>: an unverified single-file Move through the
/// action-profile path, a partly failed capture undo that has to present into an empty catalog while the folder switches,
/// and the path rules of <c>RestoresOutsideFolder</c> / <c>ReloadPathAfterUndo</c>. Temp folder and fake bin only.
/// </summary>
public sealed class FileActionControllerGapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_GapCtl_" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly RecordingSink _sink = new();
    private readonly FakeBin _bin = new();
    private readonly GrowOnMoveFileSystem _fs = new(new PhysicalFileSystem());
    private UndoService? _undo;

    public FileActionControllerGapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Make(string name, int length = 4)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private FileActionController NewController()
    {
        var settings = new AppSettings
        {
            Actions =
            [
                new ReviewAction { Name = "MoveToSub", Operation = FileOperationType.Move, Destination = "Sorted" },
                new ReviewAction { Name = "DeleteIt", Operation = FileOperationType.Recycle },
            ]
        };
        var journal = new OperationJournal(new AppPaths(_root), _fs, new SystemClock());
        var fileActions = new FileActionService(journal, _fs, new SystemClock(), _bin);
        _undo = new UndoService(journal, _fs, _bin, fileActions);
        return new FileActionController(
            _catalog, _clock, fileActions, _undo, null, preloadController: null,
            ManagedNaturalComparer.Instance, () => settings, _sink, fileSystem: _fs);
    }

    // RV-T20
    [Fact]
    public async Task RunAction_SingleMoveGrowsWhileMovingAndSourceIsGone_StaysOutOfCatalogWarnsAndRegistersNoUndo()
    {
        var first = Make("a.jpg");
        var second = Make("b.jpg");
        _catalog.Reset([first, second]);
        _fs.GrowDestinationAfterMove = true;
        var controller = NewController();

        await controller.RunActionAsync(0, null, first);

        Assert.False(File.Exists(first));
        Assert.DoesNotContain(first, _catalog.Paths);
        Assert.Equal([second], _catalog.Paths);
        Assert.Equal(Tr.StatusMoveUnverified("a.jpg"), _sink.LastStatus);
        Assert.False(_undo!.HasLastAction);
        Assert.Equal(0, _undo.MoveHistoryCount);
    }

    // RV-T21 (the wasEmpty present itself is pinned by FileActionControllerGroupTests; here the folder switches during it)
    [Fact]
    public async Task UndoLastAsync_PartialCaptureUndoIntoEmptyCatalog_FolderSwitchedDuringPresent_WritesNoSessionOrStatus()
    {
        var jpeg = Make("only.jpg");
        var raw = Make("only.cr2", 8);
        _catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var controller = NewController();
        await controller.RecycleAsync(null, jpeg);
        Assert.Equal(0, _catalog.Count);
        _sink.Reset();
        _bin.FailRestoreFor = raw;
        var other = Make("other.jpg");
        _sink.OnPresent = () => { _clock.NextFolder(); _catalog.Reset([other]); };

        var result = await controller.UndoLastAsync(_root);

        Assert.False(result!.Succeeded);
        Assert.Equal([0], _sink.Presented);
        Assert.Empty(_sink.Sessions);
        Assert.Empty(_sink.Statuses);
        Assert.Equal(0, _sink.NavCount);
        Assert.Equal([other], _catalog.Paths);
    }

    [Fact]
    public async Task UndoLastAsync_PartialCaptureUndoIntoEmptyCatalog_FolderStaysCurrent_PresentsOnceAndWritesSessionAndStatus()
    {
        var jpeg = Make("only.jpg");
        var raw = Make("only.cr2", 8);
        _catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var controller = NewController();
        await controller.RecycleAsync(null, jpeg);
        _sink.Reset();
        _bin.FailRestoreFor = raw;

        var result = await controller.UndoLastAsync(_root);

        Assert.False(result!.Succeeded);
        Assert.Equal([0], _sink.Presented);
        Assert.Equal([jpeg], _sink.Sessions);
        Assert.Equal(result.ErrorMessage, Assert.Single(_sink.Statuses));
    }

    // RV-T22
    [Fact]
    public void RestoresOutsideFolder_RecycleUndo_IsAlwaysOutsideEvenForAnEmptyOrNullCurrentFolder()
    {
        var result = new UndoResult(true, FileOperationType.Recycle, Path.Combine(_root, "a.jpg"), null, null);

        Assert.True(FileActionController.RestoresOutsideFolder(result, _root));
        Assert.True(FileActionController.RestoresOutsideFolder(result, null));
        Assert.True(FileActionController.RestoresOutsideFolder(result, string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RestoresOutsideFolder_MoveUndoWithNullOrEmptyCurrentFolder_CountsAsOutside(string? currentFolder)
    {
        var result = new UndoResult(true, FileOperationType.Move, Path.Combine(_root, "a.jpg"), null, null);

        Assert.True(FileActionController.RestoresOutsideFolder(result, currentFolder));
    }

    [Fact]
    public void RestoresOutsideFolder_MoveUndoInCurrentFolderWithCaseAndTrailingSeparatorVariants_IsInside()
    {
        var result = new UndoResult(true, FileOperationType.Move, Path.Combine(_root, "a.jpg"), null, null);
        var variant = _root.ToUpperInvariant() + Path.DirectorySeparatorChar;

        Assert.False(FileActionController.RestoresOutsideFolder(result, variant));
        Assert.False(FileActionController.RestoresOutsideFolder(result, _root));
    }

    [Fact]
    public void RestoresOutsideFolder_MoveUndoRestoredPathsWithoutTheSource_JudgesByTheRestoredPaths()
    {
        var source = Path.Combine(_root, "a.jpg");
        var inside = Path.Combine(_root, "a.cr2");
        var outside = Path.Combine(_root, "elsewhere", "a.xmp");

        var allInside = new UndoResult(true, FileOperationType.Move, source, null, null, RestoredPaths: [inside.ToUpperInvariant()]);
        var oneOutside = new UndoResult(true, FileOperationType.Move, source, null, null, RestoredPaths: [inside, outside]);

        Assert.False(FileActionController.RestoresOutsideFolder(allInside, _root));
        Assert.True(FileActionController.RestoresOutsideFolder(oneOutside, _root));
    }

    [Fact]
    public void RestoresOutsideFolder_NullFailedOrCopyOrEmptySourceResult_IsFalse()
    {
        var source = Path.Combine(_root, "a.jpg");

        Assert.False(FileActionController.RestoresOutsideFolder(null, _root));
        Assert.False(FileActionController.RestoresOutsideFolder(new UndoResult(false, FileOperationType.Recycle, source, null, "x"), _root));
        Assert.False(FileActionController.RestoresOutsideFolder(new UndoResult(true, FileOperationType.Copy, source, null, null), _root));
        Assert.False(FileActionController.RestoresOutsideFolder(new UndoResult(true, FileOperationType.Recycle, string.Empty, null, null), _root));
    }

    [Fact]
    public void ReloadPathAfterUndo_SourceRestoredUnderADifferentCase_KeepsTheSourceSpelling()
    {
        var source = Path.Combine(_root, "A.jpg");
        var raw = Path.Combine(_root, "a.cr2");
        var result = new UndoResult(true, FileOperationType.Recycle, source, null, null, RestoredPaths: [raw, source.ToLowerInvariant()]);

        Assert.Equal(source, FileActionController.ReloadPathAfterUndo(result));
    }

    [Fact]
    public void ReloadPathAfterUndo_NoRestoredPathsOrOnlySidecars_FallsBackToTheSource()
    {
        var source = Path.Combine(_root, "gone.jpg");
        var sidecar = Path.Combine(_root, "kept.xmp");

        Assert.Equal(source, FileActionController.ReloadPathAfterUndo(new UndoResult(true, FileOperationType.Recycle, source, null, null)));
        Assert.Equal(source, FileActionController.ReloadPathAfterUndo(new UndoResult(true, FileOperationType.Recycle, source, null, null, RestoredPaths: [])));
        Assert.Equal(source, FileActionController.ReloadPathAfterUndo(new UndoResult(true, FileOperationType.Recycle, source, null, null, RestoredPaths: [sidecar])));
    }

    [Fact]
    public void ReloadPathAfterUndo_EmptySourceWithRestoredImage_PicksTheImage()
    {
        var raw = Path.Combine(_root, "kept.cr2");
        var result = new UndoResult(true, FileOperationType.Recycle, string.Empty, null, null, RestoredPaths: [raw]);

        Assert.Equal(raw, FileActionController.ReloadPathAfterUndo(result));
    }

    private sealed class GrowOnMoveFileSystem(IFileSystem inner) : MainViewModelFileActionTests.DelegatingFileSystem(inner)
    {
        public bool GrowDestinationAfterMove { get; set; }

        public override void Move(string source, string destination)
        {
            base.Move(source, destination);
            if (GrowDestinationAfterMove) File.AppendAllText(destination, "grew while moving");
        }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public string? FailRestoreFor { get; set; }
        public bool CanRecycle(string path) => true;
        public bool FitsInRecycleBin(string path, long fileSize) => true;
        public void SendToRecycleBin(string path) => File.Delete(path);
        public void DeletePermanently(string path) => File.Delete(path);

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            if (string.Equals(originalPath, FailRestoreFor, StringComparison.OrdinalIgnoreCase)) return false;
            File.WriteAllBytes(originalPath, new byte[expectedSize]);
            File.SetLastWriteTimeUtc(originalPath, expectedLastWriteUtc);
            return true;
        }
    }

    private sealed class RecordingSink : IFileActionSink
    {
        public List<int> Presented { get; } = [];
        public List<string> Sessions { get; } = [];
        public List<string> Statuses { get; } = [];
        public int NavCount { get; private set; }
        public Action? OnPresent { get; set; }

        public void Reset() { Presented.Clear(); Sessions.Clear(); Statuses.Clear(); NavCount = 0; }
        public string? LastStatus => Statuses.Count > 0 ? Statuses[^1] : null;
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) { }
        public void OnCatalogChanged(string? removedPath) { }
        public void EvictCachedPaths(IReadOnlyList<string> paths) { }

        public Task PresentAsync(int index)
        {
            Presented.Add(index);
            OnPresent?.Invoke();
            return Task.CompletedTask;
        }

        public void UpdateSessionPath(string currentPath) => Sessions.Add(currentPath);
        public void NotifyNavigationStateChanged() => NavCount++;
    }
}
