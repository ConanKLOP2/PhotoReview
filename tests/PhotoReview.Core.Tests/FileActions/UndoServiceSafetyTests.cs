using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Single-undo safety: a Recycle Bin that throws, the busy guard of Ctrl+Z, and a move whose verification throws
/// after the file already returned. Fakes only; the real Recycle Bin is never touched.</summary>
public sealed class UndoServiceSafetyTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
    private const string Source = @"C:\photos\a.jpg";
    private const string Dest = @"C:\photos\selected\a.jpg";

    private sealed class FixedClock : IClock { public DateTime UtcNow => Stamp; }

    private sealed class ThrowingBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public bool Throw { get; set; } = true;
        public bool RestoreThenThrow { get; set; }
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            if (RestoreThenThrow) fs.AddFile(originalPath, new string('x', checked((int)expectedSize)), expectedLastWriteUtc);
            if (Throw) throw new InvalidOperationException("bin exploded");
            fs.AddFile(originalPath, new string('x', checked((int)expectedSize)), expectedLastWriteUtc);
            return true;
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly ThrowingBin _bin;

    public UndoServiceSafetyTests()
    {
        _journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), _fs, new FixedClock());
        _bin = new ThrowingBin(_fs);
    }

    private UndoService NewUndo(Func<string, string, Task>? moveOverride = null) =>
        new(_journal, _fs, _bin, moveOverride: moveOverride, clock: new FixedClock());

    private static FileActionResult Recycled() => new(true, FileOperationType.Recycle, Source, null, 4, Stamp, null);

    [Fact(DisplayName = "Undo of a Delete whose bin throws fails truthfully, does not throw and stays retryable")]
    public async Task UndoLast_Recycle_BinThrows_FailsTruthfullyAndStaysRetryable()
    {
        var undo = NewUndo();
        undo.Register(Recycled());

        var result = await undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.Rejected);
        Assert.Contains("bin exploded", result.ErrorMessage, StringComparison.Ordinal);
        Assert.NotNull(undo.LastUndoAction);
        Assert.False(undo.IsBusy);

        _bin.Throw = false;
        var retry = await undo.UndoLastAsync();
        Assert.True(retry.Succeeded);
        Assert.Null(undo.LastUndoAction);
    }

    [Fact(DisplayName = "Undo of a Delete whose bin restored the file and then threw is a success (the file is the proof)")]
    public async Task UndoLast_Recycle_BinRestoresThenThrows_Succeeds()
    {
        _bin.RestoreThenThrow = true;
        var undo = NewUndo();
        undo.Register(Recycled());

        var result = await undo.UndoLastAsync();

        Assert.True(result.Succeeded);
        Assert.Null(undo.LastUndoAction);
    }

    [Fact(DisplayName = "Ctrl+Z while the file-action gate is held is rejected before any undo state is read")]
    public async Task UndoLast_WhileBusy_IsRejectedAndKeepsState()
    {
        var undo = NewUndo();
        undo.Register(Recycled());
        Assert.True(undo.TryBegin());
        try
        {
            var result = await undo.UndoLastAsync();
            Assert.True(result.Rejected);
            Assert.False(result.Succeeded);
            Assert.NotNull(undo.LastUndoAction);
        }
        finally { undo.End(); }
    }

    [Fact(DisplayName = "Ctrl+Z while the gate is held reports busy even where the callee would not (permanent delete, nothing to undo)")]
    public async Task UndoLast_WhileBusy_PermanentOrEmpty_IsRejectedNotMutated()
    {
        var undo = NewUndo();
        Assert.True(undo.TryBegin());
        try
        {
            Assert.True((await undo.UndoLastAsync()).Rejected);
            undo.Register(new FileActionResult(true, FileOperationType.Recycle, Source, null, 4, Stamp, null, PermanentlyDeleted: true));
            Assert.True((await undo.UndoLastAsync()).Rejected);
            Assert.NotNull(undo.LastUndoAction);
        }
        finally { undo.End(); }
    }

    [Fact(DisplayName = "A move undo whose verification throws after the file is back drops the dead entry in one step")]
    public async Task UndoMove_VerifyThrowsAfterFileReturned_DropsEntry()
    {
        _fs.AddFile(Dest, "abcd", Stamp);
        var undo = NewUndo((from, to) =>
        {
            _fs.Move(from, to);
            // The file came back but with another size: VerifyMoved throws after the mutation.
            _fs.AddFile(to, "abcdefgh", Stamp);
            return Task.CompletedTask;
        });
        undo.Register(new FileActionResult(true, FileOperationType.Move, Source, Dest, 4, Stamp, null));

        var first = await undo.UndoLastAsync();

        Assert.False(first.Succeeded);
        Assert.Empty(undo.MoveHistory);
        Assert.Null(undo.LastUndoAction);
        var second = await undo.UndoMoveAsync();
        Assert.Equal(Tr.CoreUndoNoMoveToUndo, second.ErrorMessage);
    }
}
