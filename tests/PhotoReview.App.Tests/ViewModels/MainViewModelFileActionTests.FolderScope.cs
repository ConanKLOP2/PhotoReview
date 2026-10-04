using System.IO;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// R02 / R09: a file command queued (or an undo reopen started) in one folder must never act on a photo of another folder.
/// The folder-load identity is captured when the command is submitted; the folder generation of the clock cannot serve as
/// that identity because every file action advances it.
/// </summary>
public sealed partial class MainViewModelFileActionTests
{
    [Fact(DisplayName = "R02: a Move queued in folder A, whose turn comes after folder B was opened, does not move B's photo")]
    public async Task QueuedMove_FolderChangesBeforeItsTurn_DoesNotMoveNewFolderPhoto()
    {
        var folderA = Path.Combine(_tempDir, "queue_move_a");
        var folderB = Path.Combine(_tempDir, "queue_move_b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        var a1 = CreateImageFile(folderA, "1.jpg");
        var a2 = CreateImageFile(folderA, "2.jpg");
        var b = CreateImageFile(folderB, "x.jpg");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, undo) = CreateViewModel(new BlockingMoveFileSystem(_fileSystem, release.Task));
        await vm.OpenFolderAsync(folderA);

        var first = vm.RunActionAsync(0);
        Assert.True(vm.IsFileActionInProgress);
        var queued = vm.RunActionAsync(0);
        Assert.False(queued.IsCompleted);
        try
        {
            await vm.OpenFolderAsync(folderB);
            release.SetResult();
            await Task.WhenAll(first, queued);
        }
        finally { release.TrySetResult(); }

        Assert.True(File.Exists(b), "An action queued in folder A moved folder B's photo.");
        Assert.False(File.Exists(Path.Combine(folderB, "Sorted", "x.jpg")));
        Assert.Equal([b], vm.Catalog.Paths);
        // Only the command that had started in A ran; the stale queued one was rejected, not re-targeted.
        Assert.False(File.Exists(a1));
        Assert.True(File.Exists(a2));
        Assert.Single(undo.MoveHistory);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "R02: a Recycle queued in folder A, whose turn comes after folder B was opened, does not recycle B's photo")]
    public async Task QueuedRecycle_FolderChangesBeforeItsTurn_DoesNotRecycleNewFolderPhoto()
    {
        var folderA = Path.Combine(_tempDir, "queue_recycle_a");
        var folderB = Path.Combine(_tempDir, "queue_recycle_b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        var a1 = CreateImageFile(folderA, "1.jpg");
        var a2 = CreateImageFile(folderA, "2.jpg");
        var b = CreateImageFile(folderB, "x.jpg");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recycleBin.SendGate = release.Task;
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folderA);

        var first = vm.RecycleAsync();
        Assert.True(vm.IsFileActionInProgress);
        var queued = vm.RecycleAsync();
        Assert.False(queued.IsCompleted);
        try
        {
            await vm.OpenFolderAsync(folderB);
            release.SetResult();
            await Task.WhenAll(first, queued);
        }
        finally { release.TrySetResult(); }

        Assert.True(File.Exists(b), "A Recycle queued in folder A recycled folder B's photo.");
        Assert.Equal([a1], _recycleBin.RecycledPaths);
        Assert.True(File.Exists(a2));
        Assert.Equal([b], vm.Catalog.Paths);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "R02: commands queued behind each other in the SAME folder still all run, each on the live selection (control)")]
    public async Task QueuedMoves_SameFolder_AllRunOnLiveSelection()
    {
        var folder = Path.Combine(_tempDir, "queue_same");
        Directory.CreateDirectory(folder);
        var p1 = CreateImageFile(folder, "1.jpg");
        var p2 = CreateImageFile(folder, "2.jpg");
        var p3 = CreateImageFile(folder, "3.jpg");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, undo) = CreateViewModel(new BlockingMoveFileSystem(_fileSystem, release.Task));
        await vm.OpenFolderAsync(folder);

        var first = vm.RunActionAsync(0);
        var second = vm.RunActionAsync(0);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.False(File.Exists(p1));
        Assert.False(File.Exists(p2));
        Assert.True(File.Exists(p3));
        Assert.Equal([p3], vm.Catalog.Paths);
        Assert.Equal(2, undo.MoveHistory.Count);
    }

    [Fact(DisplayName = "R09: a Recycle undo whose restored folder is reopened after the user opened another folder meanwhile leaves the user's folder shown")]
    public async Task UndoRecycle_UserOpensOtherFolderDuringUndo_DoesNotReopenUndoFolder()
    {
        var folderA = Path.Combine(_tempDir, "undo_scope_a");
        var folderC = Path.Combine(_tempDir, "undo_scope_c");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderC);
        var a1 = CreateImageFile(folderA, "1.jpg");
        CreateImageFile(folderA, "2.jpg");
        var c = CreateImageFile(folderC, "x.jpg");
        var (vm, _, _) = CreateViewModel();
        await vm.OpenFolderAsync(folderA);
        await vm.RecycleAsync();
        Assert.False(File.Exists(a1));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recycleBin.RestoreGate = release.Task;

        var undoTask = vm.UndoAsync();
        Assert.True(vm.IsFileActionInProgress);
        try
        {
            await vm.OpenFolderAsync(folderC);
            release.SetResult();
            await undoTask;
        }
        finally { release.TrySetResult(); }

        Assert.True(File.Exists(a1)); // the undo itself is real on disk
        Assert.Equal(folderC, vm.Session?.Folder);
        Assert.Equal([c], vm.Catalog.Paths);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact(DisplayName = "R09: undoing a Move made in a previous folder, with the user opening yet another folder meanwhile, leaves the user's folder shown")]
    public async Task UndoMoveFromPreviousFolder_UserOpensOtherFolderDuringUndo_DoesNotReopenUndoFolder()
    {
        var folderA = Path.Combine(_tempDir, "undo_scope_move_a");
        var folderB = Path.Combine(_tempDir, "undo_scope_move_b");
        var folderC = Path.Combine(_tempDir, "undo_scope_move_c");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        Directory.CreateDirectory(folderC);
        var a1 = CreateImageFile(folderA, "1.jpg");
        CreateImageFile(folderA, "2.jpg");
        CreateImageFile(folderB, "y.jpg");
        var c = CreateImageFile(folderC, "x.jpg");
        var fs = new SwitchableBlockingMoveFileSystem(_fileSystem);
        var (vm, _, _) = CreateViewModel(fs);
        await vm.OpenFolderAsync(folderA);
        await vm.RunActionAsync(0); // Move A\1.jpg to A\Sorted
        await vm.OpenFolderAsync(folderB);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fs.Block = release.Task;

        var undoTask = vm.UndoAsync();
        Assert.True(vm.IsFileActionInProgress);
        try
        {
            await vm.OpenFolderAsync(folderC);
            release.SetResult();
            await undoTask;
        }
        finally { release.TrySetResult(); }

        Assert.True(File.Exists(a1));
        Assert.Equal(folderC, vm.Session?.Folder);
        Assert.Equal([c], vm.Catalog.Paths);
        Assert.False(vm.IsFileActionInProgress);
    }
}
