using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// APP-03 characterization. Policy is NOT decided (the audit asks the owner); these tests pin what the code does today so
/// a later policy change is a conscious edit, and document the user-visible consequence.
/// </summary>
public sealed partial class MainViewModelFileActionTests
{
    [Fact(DisplayName = "APP-03 (current behavior): a Move that finishes after a folder switch really moves the file but is not offered for Undo")]
    public async Task MoveCompletingAfterFolderSwitch_MovesTheFile_ButHasNoUndo()
    {
        var folder1 = Path.Combine(_tempDir, "app03_a");
        var folder2 = Path.Combine(_tempDir, "app03_b");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        var source = CreateImageFile(folder1, "1.jpg");
        CreateImageFile(folder2, "x.jpg");
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, undo) = CreateViewModel(new BlockingMoveFileSystem(_fileSystem, moveGate.Task));
        await vm.OpenFolderAsync(folder1);

        var action = vm.RunActionAsync(0); // Move to "Sorted"
        await vm.OpenFolderAsync(folder2);
        moveGate.SetResult();
        await action;

        // The user-visible consequence: the file WAS moved ...
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(folder1, "Sorted", "1.jpg")));
        // ... yet Undo has nothing (the completion is treated as stale).
        Assert.Equal(0, undo.MoveHistoryCount);
        await vm.UndoAsync();
        Assert.Equal(PhotoReview.Core.Localization.Tr.CoreUndoNothingToUndo, vm.StatusText);
        Assert.True(File.Exists(Path.Combine(folder1, "Sorted", "1.jpg"))); // nothing was restored
        // ... and the new folder's catalog and the gate are untouched.
        Assert.Equal([Path.Combine(folder2, "x.jpg")], vm.Catalog.Paths);
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
