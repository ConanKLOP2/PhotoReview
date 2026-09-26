using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Tests.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// APP-03 (Q-R25, option B): a file action that finishes AFTER the user changed folder is still registered for Undo, but
/// must never touch the new folder's catalog, session path, current image or status line. The "folder switch" is
/// simulated deterministically by bumping the generation clock (and swapping the catalog) from inside the file-system /
/// recycle-bin call, so the I/O is in flight exactly when the folder changes (no delays, no polling).
/// </summary>
public sealed class FileActionControllerLateCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_LateUndo_" + Guid.NewGuid().ToString("N"));
    private readonly string _folderA;
    private readonly string _folderB;
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly HookFileSystem _fs = new(new PhysicalFileSystem());
    private readonly FakeBin _bin = new();
    private readonly RecordingSink _sink = new();
    private readonly UndoService _undo;
    private readonly FileActionController _controller;

    public FileActionControllerLateCompletionTests()
    {
        _folderA = Path.Combine(_root, "A");
        _folderB = Path.Combine(_root, "B");
        Directory.CreateDirectory(_folderA);
        Directory.CreateDirectory(_folderB);
        var settings = new AppSettings
        {
            Actions =
            [
                new ReviewAction { Name = "MoveToSub", Operation = FileOperationType.Move, Destination = "Sorted" },
                new ReviewAction { Name = "CopyToBackup", Operation = FileOperationType.Copy, Destination = "Backup" },
            ]
        };
        (_controller, _undo) = NewController(settings, dialog: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (FileActionController Controller, UndoService Undo) NewController(AppSettings settings, IDialogService? dialog)
    {
        var journal = new OperationJournal(new AppPaths(_root), _fs, new SystemClock());
        var fileActions = new FileActionService(journal, _fs, new SystemClock(), _bin);
        var undo = new UndoService(journal, _fs, _bin, fileActions);
        var controller = new FileActionController(
            _catalog, _clock, fileActions, undo, dialog, preloadController: null,
            ManagedNaturalComparer.Instance, () => settings, _sink, fileSystem: _fs);
        return (controller, undo);
    }

    private static string Make(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    /// <summary>Folder A is open with a.jpg and z.jpg.</summary>
    private (string First, string Second) OpenAWithTwoFiles()
    {
        var a = Make(_folderA, "a.jpg");
        var z = Make(_folderA, "z.jpg");
        _catalog.Reset([a, z]);
        return (a, z);
    }

    /// <summary>The next file-system / recycle-bin call "opens folder B": clock bump, B's catalog, and a clean call log.</summary>
    private void SwitchToBDuringIo(string[] bFiles)
    {
        void Switch()
        {
            _clock.NextFolder();
            _catalog.Reset(bFiles);
            _sink.Calls.Clear(); // only what the controller does AFTER the switch matters
        }

        _fs.OnMutation = Switch;
        _bin.OnMutation = Switch;
    }

    private void ClearHooks()
    {
        _fs.OnMutation = null;
        _bin.OnMutation = null;
    }

    private static bool IsCall(string call, string kind) => call.StartsWith(kind + ":", StringComparison.Ordinal);

    [Fact(DisplayName = "APP-03: a Move finishing after a folder switch is registered for Undo and leaves the new folder untouched")]
    public async Task Move_FinishingAfterFolderSwitch_IsRegistered_AndLeavesNewFolderUntouched()
    {
        var (moved, _) = OpenAWithTwoFiles();
        var bFile = Make(_folderB, "x.jpg");
        SwitchToBDuringIo([bFile]);

        await _controller.RunActionAsync(0, null, moved);

        Assert.True(File.Exists(Path.Combine(_folderA, "Sorted", "a.jpg")));
        Assert.Equal(1, _undo.MoveHistoryCount);
        Assert.True(_undo.HasLastAction);
        Assert.Equal([bFile], _catalog.Paths);
        // Only the late hint and the navigation notification: no session path, no catalog event, no present, no plain status.
        Assert.Equal(
            [$"Late:{Tr.StatusLateMoveUndoable("a.jpg", Path.Combine(_folderA, "Sorted"))}", "Nav"],
            _sink.Calls);
    }

    [Fact(DisplayName = "APP-03: the late Move can be undone while another folder is open: file restored to its original path, catalog of the current folder untouched")]
    public async Task LateMove_Undo_WhileOtherFolderOpen_RestoresFile_WithoutChangingCatalog()
    {
        var (moved, _) = OpenAWithTwoFiles();
        var bFile = Make(_folderB, "x.jpg");
        SwitchToBDuringIo([bFile]);
        await _controller.RunActionAsync(0, null, moved);
        ClearHooks();
        _sink.Calls.Clear();
        var versionBefore = _catalog.StructuralVersion;

        var result = await _controller.UndoLastAsync(_folderB);

        Assert.True(result!.Succeeded);
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(Path.Combine(_folderA, "Sorted", "a.jpg")));
        Assert.Equal([bFile], _catalog.Paths); // no entry gained or lost
        Assert.Equal(versionBefore, _catalog.StructuralVersion);
        Assert.DoesNotContain(_sink.Calls, c => IsCall(c, "Catalog") || IsCall(c, "Session") || IsCall(c, "Present"));
        Assert.True(FileActionController.RestoresOutsideFolder(result, _folderB)); // the caller reopens folder A at the file (R7-2)
    }

    [Fact(DisplayName = "APP-03: undoing a Move whose original folder IS the current one still re-inserts it in natural order (unchanged behavior)")]
    public async Task Undo_WhenOriginalFolderIsCurrent_InsertsIntoCatalog()
    {
        var (moved, kept) = OpenAWithTwoFiles();
        await _controller.RunActionAsync(0, null, moved);
        Assert.Equal([kept], _catalog.Paths);

        var result = await _controller.UndoLastAsync(_folderA);

        Assert.True(result!.Succeeded);
        Assert.Equal([moved, kept], _catalog.Paths);
        Assert.Contains($"Session:{moved}", _sink.Calls);
        Assert.False(FileActionController.RestoresOutsideFolder(result, _folderA));
    }

    [Fact(DisplayName = "APP-03: switch to B and back to A while the Move runs (two generations): the completion is late, undoable, and the current catalog is not mutated")]
    public async Task Move_FinishingAfterSwitchAndReturn_IsStillLate()
    {
        var (moved, kept) = OpenAWithTwoFiles();
        _fs.OnMutation = () => { _clock.NextFolder(); _clock.NextFolder(); };

        await _controller.RunActionAsync(0, null, moved);

        Assert.Equal(1, _undo.MoveHistoryCount);
        Assert.Equal([kept], _catalog.Paths); // the optimistic removal made before the I/O is the only change
        Assert.DoesNotContain(_sink.Calls, c => IsCall(c, "Session"));
        Assert.Contains(_sink.Calls, c => IsCall(c, "Late"));
    }

    [Fact(DisplayName = "APP-03: a Recycle finishing after a folder switch is undoable; its undo does not write the restored path into the new folder's session")]
    public async Task Recycle_FinishingAfterFolderSwitch_IsUndoable_AndUndoDoesNotTouchNewFolderSession()
    {
        var (moved, _) = OpenAWithTwoFiles();
        var bFile = Make(_folderB, "x.jpg");
        SwitchToBDuringIo([bFile]);

        await _controller.RecycleAsync(null, moved);

        Assert.Equal([moved], _bin.Recycled);
        Assert.True(_undo.HasLastAction);
        Assert.Equal([bFile], _catalog.Paths);
        Assert.Equal([$"Late:{Tr.StatusLateRecycleUndoable("a.jpg")}", "Nav"], _sink.Calls);

        ClearHooks();
        _sink.Calls.Clear();
        var result = await _controller.UndoLastAsync(_folderB);

        Assert.True(result!.Succeeded);
        Assert.True(File.Exists(moved));
        Assert.Equal([bFile], _catalog.Paths);
        // The restored path belongs to folder A: writing it as B's session CurrentPath would corrupt B's resume state.
        Assert.DoesNotContain(_sink.Calls, c => IsCall(c, "Session"));
    }

    [Fact(DisplayName = "APP-03: Recycle undo in the folder it was made in still updates the session path (unchanged behavior)")]
    public async Task RecycleUndo_InSameFolder_UpdatesSessionPath()
    {
        var (moved, _) = OpenAWithTwoFiles();
        await _controller.RecycleAsync(null, moved);
        _sink.Calls.Clear();

        await _controller.UndoLastAsync(_folderA);

        Assert.Contains($"Session:{moved}", _sink.Calls);
    }

    [Fact(DisplayName = "APP-03: a permanent delete finishing after a folder switch says it cannot be undone, and Undo restores nothing")]
    public async Task PermanentDelete_FinishingAfterFolderSwitch_ReportsNotUndoable()
    {
        var (moved, _) = OpenAWithTwoFiles();
        var bFile = Make(_folderB, "x.jpg");
        _bin.NoBin = true; // fake: a drive without a Recycle Bin; nothing real is touched
        var (controller, _) = NewController(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true }, new ConfirmingDialog());
        SwitchToBDuringIo([bFile]);

        await controller.RecycleAsync(null, moved);

        Assert.Equal([moved], _bin.Deleted);
        Assert.Equal([$"Late:{Tr.StatusLateDeletedPermanently("a.jpg")}", "Nav"], _sink.Calls);
        Assert.Equal([bFile], _catalog.Paths);
        ClearHooks();
        var result = await controller.UndoLastAsync(_folderB);
        Assert.False(result!.Succeeded);
        Assert.False(File.Exists(moved));
        Assert.Equal(0, _bin.RestoreCalls);
    }

    [Fact(DisplayName = "APP-03: a Copy finishing after a folder switch has no undo (Copy is never undoable), stays silent and leaves the new folder untouched")]
    public async Task Copy_FinishingAfterFolderSwitch_HasNoUndo_AndLeavesNewFolderUntouched()
    {
        var (source, _) = OpenAWithTwoFiles();
        var bFile = Make(_folderB, "x.jpg");
        SwitchToBDuringIo([bFile]);

        await _controller.RunActionAsync(1, null, source);

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(_folderA, "Backup", "a.jpg")));
        Assert.False(_undo.HasLastAction);
        Assert.Equal(0, _undo.MoveHistoryCount);
        Assert.Equal([bFile], _catalog.Paths);
        Assert.Equal(["Nav"], _sink.Calls);
    }

    [Fact(DisplayName = "APP-03: a FAILED Move finishing after a folder switch is not registered and is not restored into the new catalog")]
    public async Task FailedMove_AfterFolderSwitch_NoUndo_NoRestoreIntoNewCatalog()
    {
        var (moved, _) = OpenAWithTwoFiles();
        var bFile = Make(_folderB, "x.jpg");
        SwitchToBDuringIo([bFile]);
        var switchThenFail = _fs.OnMutation!;
        _fs.OnMutation = () => { switchThenFail(); throw new IOException("disk error"); };

        await _controller.RunActionAsync(0, null, moved);

        Assert.True(File.Exists(moved));
        Assert.False(_undo.HasLastAction);
        Assert.Equal([bFile], _catalog.Paths);
        Assert.Equal(["Nav"], _sink.Calls);
    }

    [Fact(DisplayName = "APP-03: a folder switch DURING an undo leaves the new catalog untouched (existing stale guard on the undo side)")]
    public async Task FolderSwitchDuringUndo_DoesNotTouchNewCatalog()
    {
        var (moved, _) = OpenAWithTwoFiles();
        await _controller.RunActionAsync(0, null, moved);
        var bFile = Make(_folderB, "x.jpg");
        SwitchToBDuringIo([bFile]);

        var result = await _controller.UndoLastAsync(_folderA);

        Assert.True(result!.Succeeded);
        Assert.True(File.Exists(moved));
        Assert.Equal([bFile], _catalog.Paths);
        Assert.DoesNotContain(_sink.Calls, c => IsCall(c, "Session"));
    }

    [Fact(DisplayName = "APP-03: two late Moves stack: Ctrl+Z undoes the latest first")]
    public async Task TwoLateMoves_UndoInLifoOrder()
    {
        var (a, z) = OpenAWithTwoFiles();
        var bFile = Make(_folderB, "x.jpg");
        SwitchToBDuringIo([bFile]);
        await _controller.RunActionAsync(0, null, a);
        _catalog.Reset([z, bFile]);
        await _controller.RunActionAsync(0, null, z);
        Assert.Equal(2, _undo.MoveHistoryCount);
        ClearHooks();

        var first = await _controller.UndoLastAsync(_folderB);

        Assert.True(first!.Succeeded);
        Assert.Equal(z, first.Source);
        Assert.True(File.Exists(z));
        Assert.False(File.Exists(a));
        Assert.Equal(1, _undo.MoveHistoryCount);
    }

    private sealed class HookFileSystem(IFileSystem inner) : MainViewModelFileActionTests.DelegatingFileSystem(inner)
    {
        /// <summary>Runs inside Move/Copy, i.e. while the action's I/O is in flight.</summary>
        public Action? OnMutation { get; set; }

        public override void Move(string source, string destination)
        {
            OnMutation?.Invoke();
            base.Move(source, destination);
        }

        public override void Copy(string source, string destination)
        {
            OnMutation?.Invoke();
            base.Copy(source, destination);
        }
    }

    /// <summary>In-memory stand-in: never touches the real Recycle Bin (AGENTS.md).</summary>
    private sealed class FakeBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool NoBin { get; set; }
        public int RestoreCalls { get; private set; }
        public Action? OnMutation { get; set; }

        public bool CanRecycle(string path) => !NoBin;

        public void SendToRecycleBin(string path)
        {
            if (NoBin) throw new IOException("no bin");
            OnMutation?.Invoke();
            Recycled.Add(path);
            File.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            OnMutation?.Invoke();
            Deleted.Add(path);
            File.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            RestoreCalls++;
            File.WriteAllBytes(originalPath, [1, 2, 3, 4]);
            return true;
        }
    }

    private sealed class ConfirmingDialog : IDialogService
    {
        public bool ShowConfirmation(string title, string message) => true;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private sealed class RecordingSink : IFileActionSink
    {
        public List<string> Calls { get; } = [];
        public void SetStatusText(string status) => Calls.Add("Status:" + status);
        public void ShowLateActionStatus(string status) => Calls.Add("Late:" + status);
        public void OnCatalogChanged(string? removedPath) => Calls.Add("Catalog:" + removedPath);

        public Task PresentAsync(int index)
        {
            Calls.Add("Present:" + index);
            return Task.CompletedTask;
        }

        public void UpdateSessionPath(string currentPath) => Calls.Add("Session:" + currentPath);
        public void NotifyNavigationStateChanged() => Calls.Add("Nav");
    }
}
