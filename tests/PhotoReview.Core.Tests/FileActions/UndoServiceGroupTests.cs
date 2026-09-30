using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Group undo (Move and Recycle) edge cases: stale/dead entries, identity checks, partial results, per-member permanence.</summary>
[Trait("Category", "HotPath")]
public sealed class UndoServiceGroupTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string Xmp = @"C:\photos\a.xmp";
    private const string MovedJpeg = @"C:\photos\selected\a.jpg";
    private const string MovedRaw = @"C:\photos\selected\a.cr2";

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly RestoringBin _bin;
    private readonly UndoService _undo;

    public UndoServiceGroupTests()
    {
        var clock = new FixedClock();
        _journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), _fs, clock);
        _bin = new RestoringBin(_fs);
        _undo = new UndoService(_journal, _fs, _bin, new FileActionService(_journal, _fs, clock, _bin), clock: clock);
    }

    private static JournalGroupMember Member(string source, string? destination, long size, bool permanent = false) =>
        new(source, destination, size, Stamp, permanent);

    private void RegisterMove(params JournalGroupMember[] members) => _undo.RegisterGroup(Result(FileOperationType.Move, members));

    private void RegisterRecycle(params JournalGroupMember[] members) => _undo.RegisterGroup(Result(FileOperationType.Recycle, members));

    private static CaptureGroupActionResult Result(FileOperationType operation, JournalGroupMember[] members)
    {
        var entry = new JournalEntry("g", operation, JournalState.Committed, members[0].Source, members[0].Destination,
            members[0].Size, Stamp, Stamp, GroupId: "g", GroupMembers: members);
        return new(true, false, operation, "g", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null,
            PermanentlyDeleted: members.Any(member => member.Permanent));
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveAlreadyRestoredExternally_ClearsTheActionSoItDoesNotRepeat()
    {
        _fs.AddFile(Jpeg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        RegisterMove(Member(Jpeg, MovedJpeg, 4), Member(Raw, MovedRaw, 8));

        var first = await _undo.UndoLastAsync();
        var second = await _undo.UndoLastAsync();

        Assert.False(first.Succeeded);
        Assert.False(_undo.HasLastAction);
        Assert.Equal(Tr.CoreUndoNothingToUndo, second.ErrorMessage);
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleEverythingAlreadyBack_ClearsTheActionSoItDoesNotRepeat()
    {
        _fs.AddFile(Jpeg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8));

        var first = await _undo.UndoLastAsync();
        var second = await _undo.UndoLastAsync();

        Assert.False(first.Succeeded);
        Assert.False(_undo.HasLastAction);
        Assert.Equal(Tr.CoreUndoNothingToUndo, second.ErrorMessage);
        Assert.Equal(0, _bin.RestoreCalls);
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleUnrelatedFileAtOriginalPath_FailsInsteadOfReportingSuccess()
    {
        _fs.AddFile(Jpeg, "a different, newer file", Stamp.AddHours(5)); // same path, not the recycled photo
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(Tr.CoreUndoRecycleTargetExists("a.jpg"), result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, _bin.RestoreCalls); // nothing was restored on top of / next to the unrelated file
        Assert.False(_fs.FileExists(Raw));
        Assert.True(_undo.HasLastAction);
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleMemberWithMatchingIdentityAlreadyBack_IsSkippedAndRestPending()
    {
        _fs.AddFile(Jpeg, "jpeg", Stamp); // restored earlier by hand: same size and time
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8));

        var result = await _undo.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1, _bin.RestoreCalls);
        Assert.True(_fs.FileExists(Raw));
        Assert.Equal([Jpeg, Raw], result.RestoredPaths);
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveOriginalHasSameLengthButOtherTimestamp_IsNotTreatedAsRestored()
    {
        _fs.AddFile(Jpeg, "JPEG", Stamp.AddHours(3)); // same length as the moved one, different write time
        _fs.AddFile(MovedRaw, "raw data", Stamp);
        RegisterMove(Member(Jpeg, MovedJpeg, 4), Member(Raw, MovedRaw, 8));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.True(_fs.FileExists(MovedRaw)); // nothing was moved
        Assert.False(_fs.FileExists(Raw));
        Assert.DoesNotContain(_fs.Events, item => item.Kind == "move");
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveFailsMidWay_ReturnsTheAlreadyRestoredPaths()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _fs.AddFile(MovedRaw, "raw data", Stamp);
        _fs.MoveHook = (source, _) => source == MovedRaw ? new IOException("simulated undo failure") : null;
        RegisterMove(Member(Jpeg, MovedJpeg, 4), Member(Raw, MovedRaw, 8));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal([Jpeg], result.RestoredPaths);
        Assert.True(_fs.FileExists(Jpeg));
        Assert.True(_undo.HasLastAction); // still retryable: the next attempt skips the restored member
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleFailsMidWay_ReturnsTheAlreadyRestoredPaths()
    {
        _bin.FailFor = Raw;
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal([Jpeg], result.RestoredPaths);
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleWithOnePermanentMember_RestoresTheRecyclableOnesAndReportsTheOther()
    {
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8), Member(Xmp, null, 3, permanent: true));

        var result = await _undo.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal([Jpeg, Raw], result.RestoredPaths);
        Assert.Equal(2, _bin.RestoreCalls);
        Assert.False(_fs.FileExists(Xmp));
        Assert.Equal(Tr.CoreUndoGroupPartiallyRestored(2, 3, "a.xmp"), result.ErrorMessage);
        Assert.False(_undo.HasLastAction);
        Assert.Empty(_journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveRetryAfterPartialUndoWhenPartnerCameBackMeanwhile_SucceedsIdempotentlyWithAllPaths()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _fs.AddFile(MovedRaw, "raw data", Stamp);
        _fs.MoveHook = (source, _) => source == MovedRaw ? new IOException("simulated undo failure") : null;
        RegisterMove(Member(Jpeg, MovedJpeg, 4), Member(Raw, MovedRaw, 8));
        var partial = await _undo.UndoLastAsync();
        Assert.False(partial.Succeeded);
        Assert.Single(_journal.ReadFailedOperations());
        _fs.MoveHook = null;
        _fs.Move(MovedRaw, Raw); // e.g. restored through the Recovery window in the meantime

        var retry = await _undo.UndoLastAsync();

        Assert.True(retry.Succeeded, retry.ErrorMessage);
        Assert.Equal([Jpeg, Raw], retry.RestoredPaths);
        Assert.False(_undo.HasLastAction);
        Assert.Empty(_journal.ReadFailedOperations()); // the earlier Failed Recovery item is closed
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleRetryAfterPartialUndoWhenPartnerCameBackMeanwhile_SucceedsIdempotentlyWithAllPaths()
    {
        _bin.FailFor = Raw;
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8));
        var partial = await _undo.UndoLastAsync();
        Assert.False(partial.Succeeded);
        Assert.Single(_journal.ReadFailedOperations());
        _bin.FailFor = null;
        _fs.AddFile(Raw, "raw data", Stamp); // restored through the Recovery window in the meantime
        var restoreCallsBefore = _bin.RestoreCalls;

        var retry = await _undo.UndoLastAsync();

        Assert.True(retry.Succeeded, retry.ErrorMessage);
        Assert.Equal([Jpeg, Raw], retry.RestoredPaths);
        Assert.Equal(restoreCallsBefore, _bin.RestoreCalls);
        Assert.False(_undo.HasLastAction);
        Assert.Empty(_journal.ReadFailedOperations());
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveRetryThatRestoresRemainingMembers_ClosesTheEarlierFailedLine()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _fs.AddFile(MovedRaw, "raw data", Stamp);
        _fs.MoveHook = (source, _) => source == MovedRaw ? new IOException("simulated undo failure") : null;
        RegisterMove(Member(Jpeg, MovedJpeg, 4), Member(Raw, MovedRaw, 8));
        Assert.False((await _undo.UndoLastAsync()).Succeeded);
        Assert.Single(_journal.ReadFailedOperations());
        _fs.MoveHook = null;

        var retry = await _undo.UndoLastAsync();

        Assert.True(retry.Succeeded, retry.ErrorMessage);
        Assert.Empty(_journal.ReadFailedOperations()); // otherwise it looks retryable and would move fresh files
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleRetryThatRestoresRemainingMembers_ClosesTheEarlierFailedLine()
    {
        _bin.FailFor = Raw;
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8));
        Assert.False((await _undo.UndoLastAsync()).Succeeded);
        Assert.Single(_journal.ReadFailedOperations());
        _bin.FailFor = null;

        var retry = await _undo.UndoLastAsync();

        Assert.True(retry.Succeeded, retry.ErrorMessage);
        Assert.Empty(_journal.ReadFailedOperations());
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleNothingEverRestoredByThisUndo_StillReportsAlreadyHandled()
    {
        _fs.AddFile(Jpeg, "jpeg", Stamp);
        _fs.AddFile(Raw, "raw data", Stamp);
        RegisterRecycle(Member(Jpeg, null, 4), Member(Raw, null, 8));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(Tr.CoreRecoveryAlreadyHandled, result.ErrorMessage, StringComparison.Ordinal);
        Assert.False(_undo.HasLastAction);
    }

    [Fact]
    public async Task UndoLastAsync_GroupRecycleAllPermanent_SaysNothingCanBeRestoredAndClears()
    {
        RegisterRecycle(Member(Jpeg, null, 4, permanent: true), Member(Raw, null, 8, permanent: true));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreUndoPermanentlyDeleted("a.jpg"), result.ErrorMessage);
        Assert.False(_undo.HasLastAction);
        Assert.Equal(0, _bin.RestoreCalls);
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    /// <summary>Fake Recycle Bin (never the real one): a "restore" re-creates the file with the recorded identity.</summary>
    private sealed class RestoringBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public int RestoreCalls { get; private set; }
        public string? FailFor { get; set; }
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            RestoreCalls++;
            if (string.Equals(originalPath, FailFor, StringComparison.OrdinalIgnoreCase)) return false;
            fs.AddFile(originalPath, new string('x', checked((int)expectedSize)), expectedLastWriteUtc);
            return true;
        }
    }
}
