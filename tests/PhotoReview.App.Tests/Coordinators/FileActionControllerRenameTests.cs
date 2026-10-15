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

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Shared fixture of the FEAT-RENAME PR2 controller tests: the real <see cref="FileActionService"/> / <see cref="UndoService"/> /
/// <see cref="OperationJournal"/> over a temp folder (a fake Recycle Bin that never touches the real one), a recording sink and a
/// recording preload controller that append to ONE ordered log, and move-override hooks that snapshot the clock and the catalog
/// at the moment of the rename / undo I/O.
/// </summary>
internal sealed class RenameHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "PhotoReview_RenameCtl_" + Guid.NewGuid().ToString("N"));
    public GenerationClock Clock { get; } = new();
    public ReviewCatalog Catalog { get; } = new();
    public List<string> Log { get; } = [];
    public RenameSink Sink { get; }
    public RenamePreload Preload { get; }
    public FakeRenameBin Bin { get; } = new();
    public IFileSystem Fs { get; } = new PhysicalFileSystem();
    public OperationJournal Journal { get; }
    public FileActionService Service { get; }
    public UndoService Undo { get; }
    public FileActionController Controller { get; }
    public AppPaths Paths { get; }

    /// <summary>What the clock and the catalog looked like when the rename I/O ran (null: it did not run).</summary>
    public IoSnapshot? RenameIo { get; private set; }

    /// <summary>What they looked like when the undo's file move ran.</summary>
    public IoSnapshot? UndoIo { get; private set; }

    /// <summary>Awaited at the rename I/O after the snapshot (null = no hold); gets the 1-based call number.</summary>
    public Func<int, Task>? RenameHook { get; set; }

    /// <summary>Awaited at the undo I/O after the snapshot.</summary>
    public Func<int, Task>? UndoHook { get; set; }

    private int _renameCalls;
    private int _undoCalls;

    public sealed record IoSnapshot(long Navigation, long Folder, string[] CatalogPaths, int LogCountBefore);

    public RenameHarness()
    {
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Sink = new RenameSink(Log, Catalog);
        Preload = new RenamePreload(Log);
        Journal = new OperationJournal(Paths, Fs, new SystemClock());
        Service = new FileActionService(Journal, Fs, new SystemClock(), Bin, async (source, destination) =>
        {
            RenameIo = Snapshot("IO");
            if (RenameHook is { } hook) await hook(++_renameCalls);
            else _renameCalls++;
            Fs.Move(source, destination);
        });
        Undo = new UndoService(Journal, Fs, Bin, Service, async (source, destination) =>
        {
            UndoIo = Snapshot("UndoIO");
            if (UndoHook is { } hook) await hook(++_undoCalls);
            else _undoCalls++;
            Fs.Move(source, destination);
        });
        var settings = new AppSettings
        {
            Actions = [new ReviewAction { Name = "MoveToSub", Operation = FileOperationType.Move, Destination = "Sorted" }],
        };
        Controller = new FileActionController(
            Catalog, Clock, Service, Undo, dialogService: null, Preload,
            ManagedNaturalComparer.Instance, () => settings, Sink, fileSystem: Fs);
    }

    private IoSnapshot Snapshot(string label)
    {
        var snapshot = new IoSnapshot(Clock.CurrentNavigation, Clock.CurrentFolder, [.. Catalog.Paths], Log.Count);
        Log.Add(label);
        return snapshot;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public string Make(string name, int length = 4)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    public string P(string name) => Path.Combine(Root, name);

    /// <summary>Plain listing a.jpg, b.jpg, c.jpg with stat metadata that a re-listing would NOT reproduce; current = <paramref name="current"/>.</summary>
    public (string A, string B, string C) LoadThree(int current = 1)
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        var c = Make("c.jpg");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Catalog.Reset([
            new CatalogEntry(a).WithMetadata(1111, stamp, 10, 20),
            new CatalogEntry(b).WithMetadata(2222, stamp.AddDays(1), 30, 40),
            new CatalogEntry(c).WithMetadata(3333, stamp.AddDays(2), 50, 60)], RawPairMode.Separate);
        Catalog.SetCurrent(current);
        return (a, b, c);
    }

    /// <summary>Catalog of [before.jpg, shot.jpg+shot.cr2[+shot.xmp] as one entry, after.jpg] (PreferJpeg).</summary>
    public (string Before, string Jpeg, string Raw, string? Xmp, string After) LoadCapture(bool withXmp = false)
    {
        var before = Make("zbefore.jpg");
        var jpeg = Make("shot.jpg");
        var raw = Make("shot.cr2", 8);
        var xmp = withXmp ? Make("shot.xmp", 3) : null;
        var after = Make("aafter.jpg");
        Catalog.Reset(
            [new CatalogEntry(before), new CatalogEntry(jpeg), new CatalogEntry(raw), new CatalogEntry(after)],
            RawPairMode.PreferJpeg, xmp is null ? null : [xmp]);
        Catalog.SetCurrent(Catalog.IndexOf(jpeg));
        return (before, jpeg, raw, xmp, after);
    }

    public int JournalLineCount() => File.Exists(Paths.JournalFile) ? File.ReadAllLines(Paths.JournalFile).Length : 0;

    public string JournalText() => File.Exists(Paths.JournalFile) ? File.ReadAllText(Paths.JournalFile) : string.Empty;

    internal sealed class RenameSink(List<string> log, ReviewCatalog catalog) : IFileActionSink
    {
        /// <summary>What <see cref="OnFilesRenamed"/> answers: true = the shown image was kept.</summary>
        public bool Keep { get; set; } = true;
        public List<IReadOnlyDictionary<string, string>> Renamed { get; } = [];
        public List<string[]> CatalogAtRenamed { get; } = [];
        public List<int> Presented { get; } = [];
        public List<string> Statuses { get; } = [];
        public List<string> LateStatuses { get; } = [];
        public List<string?> CatalogChanged { get; } = [];
        public List<string> Sessions { get; } = [];
        public string? LastStatus => Statuses.Count > 0 ? Statuses[^1] : null;

        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) => LateStatuses.Add(status);

        public void OnCatalogChanged(string? removedPath)
        {
            log.Add("CatalogChanged");
            CatalogChanged.Add(removedPath);
        }

        public bool OnFilesRenamed(IReadOnlyDictionary<string, string> renames)
        {
            log.Add("OnFilesRenamed");
            Renamed.Add(new Dictionary<string, string>(renames, StringComparer.OrdinalIgnoreCase));
            CatalogAtRenamed.Add([.. catalog.Paths]);
            return Keep;
        }

        public Task PresentAsync(int index)
        {
            log.Add("Present:" + index);
            Presented.Add(index);
            return Task.CompletedTask;
        }

        public void UpdateSessionPath(string currentPath) => Sessions.Add(currentPath);
        public void NotifyNavigationStateChanged() { }
    }

    internal sealed class RenamePreload(List<string> log) : IPreloadController
    {
        public int Cancels { get; private set; }
        public List<int> Around { get; } = [];

        public Task PreloadAroundAsync(int center)
        {
            log.Add("Preload:" + center);
            Around.Add(center);
            return Task.CompletedTask;
        }

        public bool TryConsumePreloadedKey(PhotoReview.Imaging.ImageCacheKey key) => false;

        public void Cancel()
        {
            Cancels++;
            log.Add("Cancel");
        }

        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    internal sealed class FakeRenameBin : IRecycleBin
    {
        public bool CanRecycle(string path) => true;
        public bool FitsInRecycleBin(string path, long fileSize) => true;
        public void SendToRecycleBin(string path) => throw new NotSupportedException("rename tests never recycle");
        public void DeletePermanently(string path) => throw new NotSupportedException("rename tests never delete");
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}

/// <summary>
/// FEAT-RENAME PR2: <see cref="FileActionController.RenameAsync"/> -- ordering of effects, in-place catalog re-key, the no-op /
/// invalid / failed outcomes, capture groups and the late (folder switched) completion. Undo is in
/// <see cref="FileActionControllerRenameUndoTests"/>.
/// </summary>
[Collection("GlobalState")] // the unexpected-throw test captures the process-wide AppLog
public sealed class FileActionControllerRenameTests : IDisposable
{
    private readonly RenameHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task RenameAsync_SingleMidList_KeepsIndexMetadataAndRekeysCatalogInPlace()
    {
        var (a, b, c) = _h.LoadThree(current: 1);
        var before = _h.Catalog.EntriesSnapshot()[1];
        var stampB = before.LastWriteUtc;

        var result = await _h.Controller.RenameAsync(null, b, "renamed");

        var renamed = _h.P("renamed.jpg");
        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.Equal(renamed, result.NewPath);
        Assert.Equal([a, renamed, c], _h.Catalog.Paths);
        Assert.Equal(1, _h.Catalog.CurrentIndex);
        Assert.Equal(3, _h.Catalog.Count);
        var entry = _h.Catalog.EntriesSnapshot()[1];
        Assert.Equal(2222, entry.Length); // not re-listed: the metadata the listing had is still there
        Assert.Equal(stampB, entry.LastWriteUtc);
        Assert.Equal(30, entry.Width);
        Assert.Equal(40, entry.Height);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(b));
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Equal(new Dictionary<string, string> { [b] = renamed }, map);
        Assert.Equal(Tr.StatusRenamed("b.jpg", "renamed.jpg"), _h.Sink.LastStatus);
    }

    [Fact]
    public async Task RenameAsync_SinkKeepsShownImage_NeverPresents()
    {
        var (_, b, _) = _h.LoadThree();
        _h.Sink.Keep = true;

        await _h.Controller.RenameAsync(null, b, "x");

        Assert.Empty(_h.Sink.Presented);
        Assert.Equal([1], _h.Preload.Around);
    }

    [Fact]
    public async Task RenameAsync_SinkCannotKeepShownImage_PresentsTheCurrentIndexOnce()
    {
        var (_, b, _) = _h.LoadThree(current: 1);
        _h.Sink.Keep = false;

        await _h.Controller.RenameAsync(null, b, "x");

        Assert.Equal([1], _h.Sink.Presented);
        Assert.Empty(_h.Preload.Around); // the present warms the neighbours itself
    }

    [Fact]
    public async Task RenameAsync_EffectsRunInTheDocumentedOrder_ClockAndPreloadStoppedBeforeTheIo()
    {
        var (_, b, _) = _h.LoadThree(current: 1);
        var navigationBefore = _h.Clock.CurrentNavigation;
        var folderBefore = _h.Clock.CurrentFolder;

        await _h.Controller.RenameAsync(null, b, "x");

        Assert.Equal(["Cancel", "IO", "OnFilesRenamed", "Preload:1"], _h.Log);
        var io = _h.RenameIo!;
        Assert.True(io.Navigation > navigationBefore, "StopForAction must have bumped the navigation generation before the I/O");
        Assert.True(io.Folder > folderBefore);
        Assert.Contains(b, io.CatalogPaths); // the catalog still lists the OLD name while the file moves
        Assert.DoesNotContain(_h.P("x.jpg"), io.CatalogPaths);
        var atRenamed = Assert.Single(_h.Sink.CatalogAtRenamed);
        Assert.Contains(_h.P("x.jpg"), atRenamed); // re-keyed BEFORE the sink is told
        Assert.DoesNotContain(b, atRenamed);
    }

    [Fact]
    public async Task RenameAsync_SameName_IsNoChangeAndTouchesNothing()
    {
        var (a, b, c) = _h.LoadThree();
        var navigation = _h.Clock.CurrentNavigation;
        var folder = _h.Clock.CurrentFolder;
        var lines = _h.JournalLineCount();

        var result = await _h.Controller.RenameAsync(null, b, "b");

        Assert.Equal(FileRenameStatus.NoChange, result.Status);
        Assert.Equal(b, result.NewPath);
        Assert.Equal(Tr.StatusRenameUnchanged("b.jpg"), _h.Sink.LastStatus);
        Assert.Equal(navigation, _h.Clock.CurrentNavigation);
        Assert.Equal(folder, _h.Clock.CurrentFolder);
        Assert.Equal(0, _h.Preload.Cancels);
        Assert.Empty(_h.Log);
        Assert.Equal(lines, _h.JournalLineCount());
        Assert.Equal([a, b, c], _h.Catalog.Paths);
    }

    [Theory]
    [InlineData("", RenameNameProblem.Empty)]
    [InlineData("a?b", RenameNameProblem.InvalidCharacter)]
    [InlineData("CON", RenameNameProblem.ReservedDeviceName)]
    [InlineData("name.", RenameNameProblem.TrailingDotOrSpace)]
    public async Task RenameAsync_InvalidName_IsRefusedWithItsProblemAndNothingTouched(string typed, RenameNameProblem problem)
    {
        var (a, b, c) = _h.LoadThree();
        var navigation = _h.Clock.CurrentNavigation;

        var result = await _h.Controller.RenameAsync(null, b, typed);

        Assert.Equal(FileRenameStatus.Invalid, result.Status);
        Assert.Equal(problem, result.Problem);
        Assert.Equal(navigation, _h.Clock.CurrentNavigation);
        Assert.Equal(0, _h.Preload.Cancels);
        Assert.Empty(_h.Log);
        Assert.Equal(0, _h.JournalLineCount());
        Assert.Equal([a, b, c], _h.Catalog.Paths);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task RenameAsync_InvalidCharacter_ReportsTheOffendingCharacter()
    {
        var (_, b, _) = _h.LoadThree();

        var result = await _h.Controller.RenameAsync(null, b, "a?b");

        Assert.Equal('?', result.InvalidCharacter);
    }

    [Fact]
    public async Task RenameAsync_TargetTakenByAnotherFile_FailsKeepsEverythingAndDismissesTheJournalLine()
    {
        var (a, b, c) = _h.LoadThree(current: 1);

        var result = await _h.Controller.RenameAsync(null, b, "c");

        Assert.Equal(FileRenameStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal([a, b, c], _h.Catalog.Paths);
        Assert.Equal(1, _h.Catalog.CurrentIndex);
        Assert.True(File.Exists(b) && File.Exists(c));
        // The failed attempt re-armed what StopForAction stopped: an EMPTY map, nothing presented.
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Empty(map);
        Assert.Empty(_h.Sink.Presented);
        Assert.Equal(Tr.StatusRenameFailed("b.jpg", result.Error), _h.Sink.LastStatus);
        Assert.Empty(_h.Journal.ReadPendingAndFailedOperations()); // not a Recovery "Failed" item
    }

    [Fact]
    public async Task RenameAsync_TargetTaken_IsRefusedBeforeAnythingIsJournaled()
    {
        var (_, b, _) = _h.LoadThree();
        _h.Make("taken.jpg");

        await _h.Controller.RenameAsync(null, b, "taken");

        Assert.Equal(0, _h.JournalLineCount());
        Assert.DoesNotContain("IO", _h.Log); // the file was never touched
    }

    [Fact]
    public async Task RenameAsync_IoFailsAfterPreparedWithTheSourceUntouched_JournalLineIsDismissedNotAFailedRecoveryItem()
    {
        var (a, b, c) = _h.LoadThree();
        _h.RenameHook = _ => throw new IOException("share hiccup");

        var result = await _h.Controller.RenameAsync(null, b, "x");

        Assert.Equal(FileRenameStatus.Failed, result.Status);
        var text = _h.JournalText();
        Assert.Contains("Dismissed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Failed\"", text, StringComparison.Ordinal);
        Assert.Empty(_h.Journal.ReadPendingAndFailedOperations());
        Assert.Equal([a, b, c], _h.Catalog.Paths);
        Assert.Null(_h.Undo.LastUndoAction);
        Assert.Empty(Assert.Single(_h.Sink.Renamed));
    }

    [Fact]
    public async Task RenameAsync_ExecutorThrows_FailsKeepsCatalogAndReArmsWithoutEscaping()
    {
        var (a, b, c) = _h.LoadThree(current: 1);
        using var captured = new CapturedAppLog();
        var boom = new InvalidOperationException("boom");
        _h.Controller.FileActionsOverride = new ThrowingRenameExecutor(boom);

        var result = await _h.Controller.RenameAsync(null, b, "x");

        Assert.Equal(FileRenameStatus.Failed, result.Status);
        Assert.Equal(UserFacingError.Describe(boom), result.Error);
        Assert.Equal([a, b, c], _h.Catalog.Paths);
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Empty(map);
        Assert.Equal(Tr.StatusRenameFailed("b.jpg", UserFacingError.Describe(boom)), _h.Sink.LastStatus);
        Assert.Contains("Rename threw", captured.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameAsync_ExecutorThrowsAndSinkCannotKeep_PresentsAgain()
    {
        var (_, b, _) = _h.LoadThree(current: 1);
        using var captured = new CapturedAppLog();
        _h.Sink.Keep = false;
        _h.Controller.FileActionsOverride = new ThrowingRenameExecutor(new IOException("disk"));

        await _h.Controller.RenameAsync(null, b, "x");

        Assert.Equal([1], _h.Sink.Presented);
    }

    [Fact]
    public async Task RenameAsync_GateBusy_ReturnsBusyAndChangesNothing()
    {
        var (a, b, c) = _h.LoadThree();
        Assert.True(_h.Service.TryBegin());
        var navigation = _h.Clock.CurrentNavigation;

        var result = await _h.Controller.RenameAsync(null, b, "x");

        Assert.Equal(FileRenameStatus.Busy, result.Status);
        Assert.Equal([a, b, c], _h.Catalog.Paths);
        Assert.Equal(navigation, _h.Clock.CurrentNavigation);
        Assert.Empty(_h.Log);
        Assert.True(File.Exists(b));
        _h.Service.End();
    }

    [Fact]
    public async Task RenameAsync_EmptyCatalogOrUnknownPath_IsNothingToRename()
    {
        var empty = await _h.Controller.RenameAsync(null, _h.P("nope.jpg"), "x");
        _ = _h.LoadThree();
        var unknown = await _h.Controller.RenameAsync(null, _h.P("unlisted.jpg"), "x");
        var noPath = await _h.Controller.RenameAsync(null, null, "x");

        Assert.Equal(FileRenameStatus.NothingToRename, empty.Status);
        Assert.Equal(FileRenameStatus.NothingToRename, unknown.Status);
        Assert.Equal(FileRenameStatus.NothingToRename, noPath.Status);
        Assert.Empty(_h.Log);
    }

    [Fact]
    public async Task RenameAsync_CompareSelection_RenamesTheSelectedFileNotTheCurrent()
    {
        var (a, b, c) = _h.LoadThree(current: 1);

        var result = await _h.Controller.RenameAsync(compareSelectedPath: c, currentPath: b, "x");

        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.Equal([a, b, _h.P("x.jpg")], _h.Catalog.Paths);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task RenameAsync_FolderSwitchedDuringTheIo_RenameStaysUndoableAndTheNewFoldersStateIsUntouched()
    {
        var (a, b, c) = _h.LoadThree();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.RenameHook = async _ =>
        {
            entered.SetResult();
            await release.Task;
        };

        var running = _h.Controller.RenameAsync(null, b, "x");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _h.Clock.NextFolder(); // another folder was opened while the file was moving
        release.SetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.True(File.Exists(_h.P("x.jpg")));
        Assert.False(File.Exists(b));
        Assert.NotNull(_h.Undo.LastUndoAction); // registered: Ctrl+Z can still undo it
        Assert.Equal(Tr.StatusRenamed("b.jpg", "x.jpg"), Assert.Single(_h.Sink.LateStatuses));
        Assert.Empty(_h.Sink.Statuses);
        Assert.Empty(_h.Sink.Renamed); // the new folder's catalog / session / presenter are not touched
        Assert.Empty(_h.Sink.Presented);
        Assert.Equal([a, b, c], _h.Catalog.Paths);
    }

    [Fact]
    public async Task RenameAsync_CaseOnly_RenamesTheEntryAndTheFileSpelling()
    {
        var (a, b, c) = _h.LoadThree(current: 1);

        var result = await _h.Controller.RenameAsync(null, b, "B");

        var upper = _h.P("B.jpg");
        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.Equal([a, upper, c], _h.Catalog.Paths);
        Assert.Contains("B.jpg", Directory.EnumerateFiles(_h.Root).Select(Path.GetFileName));
        Assert.DoesNotContain("b.jpg", Directory.EnumerateFiles(_h.Root).Select(Path.GetFileName));
        Assert.Equal(new Dictionary<string, string> { [b] = upper }, Assert.Single(_h.Sink.Renamed));
        Assert.NotNull(_h.Undo.LastUndoAction);
    }

    [Fact]
    public async Task RenameAsync_CaptureGroup_RespellsEveryMemberAndRegistersTheGroupForUndo()
    {
        var (before, jpeg, raw, xmp, after) = _h.LoadCapture(withXmp: true);
        var index = _h.Catalog.CurrentIndex;

        var result = await _h.Controller.RenameAsync(null, jpeg, "pic");

        var newJpeg = _h.P("pic.jpg");
        var newRaw = _h.P("pic.cr2");
        var newXmp = _h.P("pic.xmp");
        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.Equal(newJpeg, result.NewPath);
        Assert.Equal([before, newJpeg, after], _h.Catalog.Paths);
        Assert.Equal(index, _h.Catalog.CurrentIndex);
        var group = _h.Catalog.EntriesSnapshot()[1].CaptureGroup;
        Assert.NotNull(group);
        Assert.Equal(newJpeg, group!.JpegPath);
        Assert.Equal(newRaw, group.RawPath);
        Assert.Equal(newXmp, group.XmpPath);
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Equal(new Dictionary<string, string> { [jpeg] = newJpeg, [raw] = newRaw, [xmp!] = newXmp }, map);
        Assert.True(File.Exists(newJpeg) && File.Exists(newRaw) && File.Exists(newXmp));
        Assert.False(File.Exists(jpeg) || File.Exists(raw) || File.Exists(xmp!));
        Assert.NotNull(_h.Undo.LastUndoAction);
        Assert.Empty(_h.Sink.Presented);
    }

    [Fact]
    public async Task RenameAsync_CaptureGroupFailsAtTheSecondMember_RollsBackAndKeepsTheCatalog()
    {
        var (before, jpeg, raw, _, after) = _h.LoadCapture();
        _h.RenameHook = call => call == 2 ? throw new IOException("second member refused") : Task.CompletedTask;

        var result = await _h.Controller.RenameAsync(null, jpeg, "pic");

        Assert.Equal(FileRenameStatus.Failed, result.Status);
        Assert.Equal([before, jpeg, after], _h.Catalog.Paths);
        var group = _h.Catalog.EntriesSnapshot()[1].CaptureGroup;
        Assert.NotNull(group);
        Assert.Equal(raw, group!.RawPath);
        Assert.True(File.Exists(jpeg), "the first member must have been rolled back");
        Assert.True(File.Exists(raw));
        Assert.False(File.Exists(_h.P("pic.jpg")));
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Empty(map); // nothing stayed renamed: pure re-arm
        Assert.Null(_h.Undo.LastUndoAction);
    }

    [Fact]
    public async Task RenameAsync_CatalogAlreadyListsTheNewName_DropsTheOldEntryAndPresentsTheCurrentOne()
    {
        var a = _h.Make("a.jpg");
        var b = _h.Make("b.jpg");
        var stale = _h.P("x.jpg"); // listed by a stale scan but not on disk: the rename itself succeeds
        _h.Catalog.Reset([new CatalogEntry(a), new CatalogEntry(b), new CatalogEntry(stale)], RawPairMode.Separate);
        _h.Catalog.SetCurrent(1);

        var result = await _h.Controller.RenameAsync(null, b, "x");

        Assert.Equal(FileRenameStatus.Renamed, result.Status);
        Assert.Equal([a, stale], _h.Catalog.Paths);
        Assert.Contains(b, _h.Sink.CatalogChanged);
        Assert.NotEmpty(_h.Sink.Presented); // listing conflict: present again even though the sink would keep the image
        var map = Assert.Single(_h.Sink.Renamed);
        Assert.Empty(map);
    }

    private sealed class ThrowingRenameExecutor(Exception error) : IFileActionExecutor
    {
        public bool IsBusy => false;
        public bool LacksRecycleBin(string path) => false;
        public Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default) => throw error;
        public Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default) => throw error;
        public Task<FileActionResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken = default) => throw error;
        public Task<CaptureGroupActionResult> RenameGroupAsync(CaptureGroup group, string typedStem, CancellationToken cancellationToken = default) => throw error;
    }
}
