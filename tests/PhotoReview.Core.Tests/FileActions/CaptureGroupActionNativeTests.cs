using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "Native")]
public sealed class CaptureGroupActionNativeTests
{
    [Fact]
    public async Task GroupMoveAndUndo_CopiedRawCorpusMember_PreservesOriginalAndRestoresPair()
    {
        var corpusRaw = RawCorpus.TryGetFile("Canon - EOS 350D - RAW (3_2).CR2"); // null = corpus absent (throws instead in strict mode)
        if (corpusRaw is null) return;

        using var root = new TempRoot("raw-group-corpus-action");
        var sourceFolder = root.Dir("photos");
        var rawPath = Path.Combine(sourceFolder, "capture.cr2");
        var jpegPath = Path.Combine(sourceFolder, "capture.jpg");
        File.Copy(corpusRaw, rawPath);
        File.WriteAllBytes(jpegPath, [0xFF, 0xD8, 0xFF, 0xD9]);
        var originalCorpusLength = new FileInfo(corpusRaw).Length;

        var fileSystem = new PhysicalFileSystem();
        var clock = new NativeTestClock();
        var journal = new OperationJournal(new AppPaths(root.Combine("app-data")), fileSystem, clock);
        var recycleBin = new NonRecyclingBin();
        var actions = new FileActionService(journal, fileSystem, clock, recycleBin);
        var group = new CaptureGroup(jpegPath, rawPath);

        var moved = await actions.ExecuteGroupAsync(new CaptureGroupActionRequest(group, FileOperationType.Move, "selected"));

        Assert.True(moved.Succeeded, moved.Error);
        var movedJpeg = Path.Combine(sourceFolder, "selected", "capture.jpg");
        var movedRaw = Path.Combine(sourceFolder, "selected", "capture.cr2");
        Assert.False(File.Exists(jpegPath));
        Assert.False(File.Exists(rawPath));
        Assert.Equal(originalCorpusLength, new FileInfo(movedRaw).Length);
        Assert.True(File.Exists(corpusRaw));

        var undo = new UndoService(journal, fileSystem, recycleBin, actions);
        undo.RegisterGroup(moved);
        var restored = await undo.UndoLastAsync();

        Assert.True(restored.Succeeded, restored.ErrorMessage);
        Assert.True(File.Exists(jpegPath));
        Assert.True(File.Exists(rawPath));
        Assert.False(File.Exists(movedJpeg));
        Assert.False(File.Exists(movedRaw));
        Assert.True(File.Exists(corpusRaw));
        var committedUndo = Assert.Single(journal.ReadCommittedMoves(), entry => entry.Undo == true);
        Assert.Equal(2, committedUndo.GroupMembers!.Count);
        Assert.All(committedUndo.GroupMembers, member => Assert.True(File.Exists(member.Destination!)));
    }

    private sealed class NativeTestClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    private sealed class NonRecyclingBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
