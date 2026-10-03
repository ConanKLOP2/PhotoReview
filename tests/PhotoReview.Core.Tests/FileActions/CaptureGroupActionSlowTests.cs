using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "Slow")]
public sealed class CaptureGroupActionSlowTests
{
    [Fact]
    public async Task GroupMoveAndUndo_UsesRealFilesystemAndKeepsBothMembersRecoverable()
    {
        using var root = new TempRoot("raw-group-action");
        var sourceFolder = root.Dir("photos");
        var jpeg = root.File(Path.Combine("photos", "capture.jpg"), 1, 2, 3, 4);
        var raw = root.File(Path.Combine("photos", "capture.cr2"), 5, 6, 7, 8, 9);
        var fileSystem = new PhysicalFileSystem();
        var clock = new SlowTestClock();
        var journal = new OperationJournal(new AppPaths(root.Combine("app-data")), fileSystem, clock);
        var recycleBin = new NonRecyclingBin();
        var actions = new FileActionService(journal, fileSystem, clock, recycleBin);
        var group = new CaptureGroup(jpeg, raw);

        var moved = await actions.ExecuteGroupAsync(new CaptureGroupActionRequest(group, FileOperationType.Move, "selected"));

        Assert.True(moved.Succeeded, moved.Error);
        var movedJpeg = Path.Combine(sourceFolder, "selected", "capture.jpg");
        var movedRaw = Path.Combine(sourceFolder, "selected", "capture.cr2");
        Assert.False(File.Exists(jpeg));
        Assert.False(File.Exists(raw));
        Assert.True(File.Exists(movedJpeg));
        Assert.True(File.Exists(movedRaw));

        var undo = new UndoService(journal, fileSystem, recycleBin, actions);
        undo.RegisterGroup(moved);
        var restored = await undo.UndoLastAsync();

        Assert.True(restored.Succeeded, restored.ErrorMessage);
        Assert.True(File.Exists(jpeg));
        Assert.True(File.Exists(raw));
        Assert.False(File.Exists(movedJpeg));
        Assert.False(File.Exists(movedRaw));
        var committedUndo = Assert.Single(journal.ReadCommittedMoves(), entry => entry.Undo == true);
        Assert.Equal(2, committedUndo.GroupMembers!.Count);
        Assert.All(committedUndo.GroupMembers, member => Assert.True(File.Exists(member.Destination!)));
    }

    private sealed class SlowTestClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    private sealed class NonRecyclingBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
