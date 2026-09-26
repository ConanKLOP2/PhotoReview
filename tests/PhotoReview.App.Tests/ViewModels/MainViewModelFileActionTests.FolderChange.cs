using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// APP-03 (Q-R25, option B): a file action that finishes after the user switched folder is still registered for Undo.
/// Controller-level races (barriers inside the I/O, catalog/session/status assertions) live in
/// FileActionControllerLateCompletionTests; these tests run the whole view-model.
/// </summary>
public sealed partial class MainViewModelFileActionTests
{
    [Fact(DisplayName = "APP-03 (replaces the old 'not offered for Undo' pin): a Move that finishes after a folder switch is undoable, the hint is shown, and Ctrl+Z restores it")]
    public async Task MoveCompletingAfterFolderSwitch_IsUndoable_AndUndoRestoresIt()
    {
        var folder1 = Path.Combine(_tempDir, "app03_a");
        var folder2 = Path.Combine(_tempDir, "app03_b");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        var source = CreateImageFile(folder1, "1.jpg");
        var x = CreateImageFile(folder2, "x.jpg");
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, undo) = CreateViewModel(new BlockingMoveFileSystem(_fileSystem, moveGate.Task));
        await vm.OpenFolderAsync(folder1);

        var action = vm.RunActionAsync(0); // Move to "Sorted"
        await vm.OpenFolderAsync(folder2);
        moveGate.SetResult();
        await action;

        var moved = Path.Combine(folder1, "Sorted", "1.jpg");
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(moved));
        Assert.Equal(1, undo.MoveHistoryCount);
        // Folder 2 idle: the hint is visible. Its catalog, session and gate are untouched.
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusLateMoveUndoable("1.jpg", Path.Combine(folder1, "Sorted")), vm.StatusText);
        Assert.Equal([x], vm.Catalog.Paths);
        Assert.Equal(x, vm.Session?.CurrentPath);
        Assert.False(vm.IsFileActionInProgress);

        await vm.UndoAsync();

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(moved));
        Assert.Equal(folder1, vm.Session?.Folder); // R7-2: folder 1 is reopened at the restored file
        Assert.Equal(source, vm.Catalog.Current?.Path);
    }

    [Fact(DisplayName = "APP-03: the late-completion hint never replaces the new folder's own status text (here: no supported images)")]
    public async Task LateCompletionHint_DoesNotOverwriteNewFolderStatus()
    {
        var folder1 = Path.Combine(_tempDir, "app03_h1");
        var emptyFolder = Path.Combine(_tempDir, "app03_h2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(emptyFolder);
        CreateImageFile(folder1, "1.jpg");
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, undo) = CreateViewModel(new BlockingMoveFileSystem(_fileSystem, moveGate.Task));
        await vm.OpenFolderAsync(folder1);

        var action = vm.RunActionAsync(0);
        await vm.OpenFolderAsync(emptyFolder);
        var statusBefore = vm.StatusText;
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusNoSupportedImages, statusBefore);
        moveGate.SetResult();
        await action;

        Assert.Equal(1, undo.MoveHistoryCount); // still undoable, only the hint is dropped
        Assert.Equal(statusBefore, vm.StatusText);
    }

    [Fact(DisplayName = "APP-03: a Recycle finishing after a folder switch is undoable; undo restores the file and does not write its path into the new folder's session")]
    public async Task RecycleCompletingAfterFolderSwitch_IsUndoable_AndDoesNotCorruptNewSession()
    {
        var folder1 = Path.Combine(_tempDir, "app03_r1");
        var folder2 = Path.Combine(_tempDir, "app03_r2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        var source = CreateImageFile(folder1, "1.jpg");
        var x = CreateImageFile(folder2, "x.jpg");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recycleBin.SendGate = gate.Task;
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder1);

        var recycle = vm.RecycleAsync();
        await vm.OpenFolderAsync(folder2);
        gate.SetResult();
        await recycle;

        Assert.Equal([source], _recycleBin.RecycledPaths);
        Assert.Equal([x], vm.Catalog.Paths);
        Assert.Equal(PhotoReview.Core.Localization.Tr.StatusLateRecycleUndoable("1.jpg"), vm.StatusText);

        await vm.UndoAsync();

        Assert.True(File.Exists(source));
        Assert.Equal(1, _recycleBin.RestoreCalls);
        Assert.Equal(folder1, vm.Session?.Folder);
        // Folder 2's persisted session still points at its own file, not at the restored file of folder 1.
        Assert.Equal(x, _sessionStore.Load(folder2).CurrentPath);
    }

    [Fact(DisplayName = "APP-03: a Copy finishing after a folder switch copies the file, has nothing to undo, and leaves the new folder untouched")]
    public async Task CopyCompletingAfterFolderSwitch_HasNoUndo_AndLeavesNewFolderUntouched()
    {
        var folder1 = Path.Combine(_tempDir, "app03_c1");
        var folder2 = Path.Combine(_tempDir, "app03_c2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        var source = CreateImageFile(folder1, "1.jpg");
        var x = CreateImageFile(folder2, "x.jpg");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, undo) = CreateViewModel(new BlockingCopyFileSystem(_fileSystem, gate.Task));
        await vm.OpenFolderAsync(folder1);

        var action = vm.RunActionAsync(1); // Copy to "Backup"
        await vm.OpenFolderAsync(folder2);
        gate.SetResult();
        await action;

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(folder1, "Backup", "1.jpg")));
        Assert.Equal(0, undo.MoveHistoryCount);
        Assert.False(undo.HasLastAction);
        Assert.Equal([x], vm.Catalog.Paths);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "APP-03: an Undo registered in a previous folder still restores the file and reopens that folder")]
    public async Task UndoRegisteredInPreviousFolder_ReopensThatFolder()
    {
        var folder1 = Path.Combine(_tempDir, "app03_c");
        var folder2 = Path.Combine(_tempDir, "app03_d");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        var source = CreateImageFile(folder1, "1.jpg");
        CreateImageFile(folder1, "2.jpg");
        CreateImageFile(folder2, "x.jpg");
        var (vm, _, undo) = CreateViewModel();
        await vm.OpenFolderAsync(folder1);
        await vm.RunActionAsync(0);
        Assert.Equal(1, undo.MoveHistoryCount);
        await vm.OpenFolderAsync(folder2);

        await vm.UndoAsync();

        Assert.True(File.Exists(source));
        Assert.Equal(folder1, vm.Session?.Folder);
        Assert.Equal(source, vm.Catalog.Current?.Path);
    }

    [Fact(DisplayName = "Undo with no history and no folder open reports nothing to undo and stays idle")]
    public async Task UndoWithoutAnyFolder_ReportsNothingToUndo()
    {
        var (vm, _, _) = CreateViewModel();

        await vm.UndoAsync();

        Assert.Equal(PhotoReview.Core.Localization.Tr.CoreUndoNothingToUndo, vm.StatusText);
        Assert.False(vm.IsFileActionInProgress);
        Assert.Equal(0, vm.TotalFiles);
    }

    [Fact(DisplayName = "File actions on an empty catalog, and with an out-of-range action index, are no-ops")]
    public async Task ActionsWithoutImagesOrWithBadIndex_AreNoOps()
    {
        var (vm, _, undo) = CreateViewModel();
        await vm.RunActionAsync(0);
        await vm.RecycleAsync();
        await vm.MoveToFolderAsync();
        await vm.CopyToFolderAsync();

        var folder = Path.Combine(_tempDir, "app03_e");
        Directory.CreateDirectory(folder);
        var only = CreateImageFile(folder, "only.jpg");
        await vm.OpenFolderAsync(folder);
        await vm.RunActionAsync(-1);
        await vm.RunActionAsync(999);
        await vm.RunActionAsync(int.MinValue);
        await vm.RunActionAsync(int.MaxValue);

        Assert.True(File.Exists(only));
        Assert.Equal(1, vm.TotalFiles);
        Assert.Equal(0, undo.MoveHistoryCount);
        Assert.False(vm.IsFileActionInProgress);
    }
}
