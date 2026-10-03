using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// JPEG + RAW + XMP capture-group actions (happy paths and undo), cancellation in the middle of a group Delete, and the
/// busy gate under overlapping calls. In-memory file system and a fake Recycle Bin only.
/// </summary>
public sealed class CaptureGroupSidecarAndGateTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string Xmp = @"C:\photos\a.xmp";

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly FakeBin _bin;
    private readonly FileActionService _service;

    public CaptureGroupSidecarAndGateTests()
    {
        _fs.AddFile(Jpeg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        _fs.AddFile(Xmp, "xmp", Stamp);
        _journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), _fs, new FixedClock());
        _bin = new FakeBin(_fs);
        _service = new FileActionService(_journal, _fs, new FixedClock(), _bin);
    }

    private static CaptureGroupActionRequest Request(FileOperationType operation, string? destination = "selected") =>
        new(new CaptureGroup(Jpeg, Raw, Xmp), operation, destination);

    [Fact]
    public async Task ExecuteGroupAsync_MoveWithXmpSidecar_MovesAllThreeAndJournalsThreeMembers()
    {
        var result = await _service.ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.True(result.Succeeded, result.Error);
        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp" })
        {
            Assert.True(_fs.FileExists(@"C:\photos\selected\" + name));
            Assert.False(_fs.FileExists(@"C:\photos\" + name));
        }
        var committed = Assert.Single(_journal.ReadCommittedMoves());
        Assert.Equal([Jpeg, Raw, Xmp], committed.GroupMembers!.Select(member => member.Source));
        Assert.Empty(_journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task UndoLastAsync_AfterGroupMoveWithXmp_RestoresAllThree()
    {
        var undo = new UndoService(_journal, _fs, _bin, _service, clock: new FixedClock());
        undo.RegisterGroup(await _service.ExecuteGroupAsync(Request(FileOperationType.Move)));

        var result = await undo.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal([Jpeg, Raw, Xmp], result.RestoredPaths);
        Assert.True(_fs.FileExists(Jpeg) && _fs.FileExists(Raw) && _fs.FileExists(Xmp));
        Assert.False(_fs.FileExists(@"C:\photos\selected\a.xmp"));
        Assert.Empty(_journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyWithXmpSidecar_CopiesAllThreeAndKeepsTheSources()
    {
        var result = await _service.ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.True(result.Succeeded, result.Error);
        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp" })
        {
            Assert.True(_fs.FileExists(@"C:\photos\selected\" + name));
            Assert.True(_fs.FileExists(@"C:\photos\" + name));
        }
        Assert.Equal(3, result.Members.Count);
        Assert.All(result.Members, member => Assert.True(member.Completed));
        Assert.Empty(_journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleCancelledAfterFirstMember_LeavesAFailedEntryWithTheTrueMemberStates()
    {
        using var cts = new CancellationTokenSource();
        _bin.OnRecycle = _ => cts.Cancel(); // the user cancels while the first member is in the bin

        var result = await _service.ExecuteGroupAsync(Request(FileOperationType.Recycle, null), cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal([Jpeg], _bin.Recycled); // nothing after the cancellation
        var byPath = result.Members.ToDictionary(member => member.Member.Source, StringComparer.OrdinalIgnoreCase);
        Assert.True(byPath[Jpeg].Completed);
        Assert.False(byPath[Raw].Completed);
        Assert.True(byPath[Raw].SourceExists);
        Assert.False(byPath[Xmp].Completed);
        var failed = Assert.Single(_journal.ReadFailedOperations()); // a Delete cannot be rolled back: Recovery keeps it
        Assert.Equal(JournalErrors.CancelledByUser, failed.ErrorCode);
        Assert.Equal(3, failed.GroupMembers!.Count);
    }

    [Fact]
    public async Task ExecuteGroupAsync_SecondCallWhileFirstRuns_IsRejectedWithoutJournalLineAndGateIsReleasedOnce()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _bin.OnRecycle = _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        };
        var first = _service.ExecuteGroupAsync(Request(FileOperationType.Recycle, null));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        var journalBefore = _fs.ReadAllText(new AppPaths(@"C:\Users\test\AppData\Local").JournalFile);

        var second = await _service.ExecuteGroupAsync(new CaptureGroupActionRequest(new CaptureGroup(Jpeg, Raw), FileOperationType.Recycle));

        Assert.True(second.Rejected);
        Assert.False(second.Succeeded);
        Assert.Equal(Tr.CoreFileActionBusy, second.Error);
        Assert.True(_service.IsBusy); // the rejected call did not release the first call's gate
        Assert.Equal(journalBefore, _fs.ReadAllText(new AppPaths(@"C:\Users\test\AppData\Local").JournalFile)); // no line written
        release.Set();
        Assert.True((await first).Succeeded);
        Assert.False(_service.IsBusy);
        Assert.True(_service.TryBegin()); // free again, exactly once
        Assert.False(_service.TryBegin());
        _service.End();
    }

    [Fact]
    public async Task UndoLastAsync_WhileAnotherActionRuns_IsRejectedAndKeepsTheUndoAction()
    {
        var undo = new UndoService(_journal, _fs, _bin, _service, clock: new FixedClock());
        undo.RegisterGroup(await _service.ExecuteGroupAsync(Request(FileOperationType.Move)));
        Assert.True(_service.TryBegin()); // another file action is running

        var rejected = await undo.UndoLastAsync();

        Assert.True(rejected.Rejected);
        Assert.False(rejected.Succeeded);
        Assert.True(_service.IsBusy);
        Assert.True(undo.HasLastAction);
        Assert.False(_fs.FileExists(Jpeg)); // nothing was moved back
        _service.End();
        var ok = await undo.UndoLastAsync();
        Assert.True(ok.Succeeded, ok.ErrorMessage);
        Assert.False(_service.IsBusy);
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    /// <summary>Fake Recycle Bin (never the real one): removes the file from the in-memory disk and records the call.</summary>
    private sealed class FakeBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public Action<string>? OnRecycle { get; set; }

        public void SendToRecycleBin(string path)
        {
            OnRecycle?.Invoke(path);
            Recycled.Add(path);
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
