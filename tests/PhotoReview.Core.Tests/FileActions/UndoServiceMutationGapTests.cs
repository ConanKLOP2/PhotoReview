using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gap fillers (Stryker, 2026-10-03) for <see cref="UndoService"/>: which results become the undoable last
/// action, the busy gate without a <see cref="FileActionService"/>, the journal fallback and move override of a single undo,
/// and what a group undo reports / remembers when it cannot (fully) restore. Fakes only; the real Recycle Bin is never touched.
/// </summary>
public sealed class UndoServiceMutationGapTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterStamp = Stamp.AddHours(3);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string MovedJpeg = @"C:\photos\selected\a.jpg";
    private const string MovedRaw = @"C:\photos\selected\a.cr2";

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    /// <summary>Fake Recycle Bin: a "restore" re-creates the file with the recorded identity, unless told to fail.</summary>
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

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly RestoringBin _bin;
    private readonly FileActionService _service;
    private readonly UndoService _undo;

    public UndoServiceMutationGapTests()
    {
        var clock = new FixedClock(Stamp);
        _journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), _fs, clock);
        _bin = new RestoringBin(_fs);
        _service = new FileActionService(_journal, _fs, clock, _bin);
        // The undo's own clock differs from every recorded stamp: a journal line it writes is recognisable by its timestamp.
        _undo = new UndoService(_journal, _fs, _bin, _service, clock: new FixedClock(LaterStamp));
    }

    private static JournalGroupMember Member(string source, string? destination, long size, bool permanent = false) =>
        new(source, destination, size, Stamp, permanent);

    private static CaptureGroupActionResult GroupResult(FileOperationType operation, JournalGroupMember[] members,
        string? entrySource = null, string? entryDestination = null)
    {
        var entry = new JournalEntry("g", operation, JournalState.Committed, entrySource ?? members[0].Source,
            entryDestination ?? members[0].Destination, members[0].Size, Stamp, Stamp, GroupId: "g", GroupMembers: members);
        return new(true, false, operation, "g", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null);
    }

    private static FileActionResult Moved(bool succeeded = true, bool rejected = false, string? destination = MovedJpeg,
        FileOperationType operation = FileOperationType.Move) =>
        new(succeeded, operation, Jpeg, destination, 4, Stamp, succeeded ? null : "failed", Rejected: rejected);

    // ---- busy gate without a FileActionService ----

    [Fact]
    public void TryBegin_WithoutFileActionService_GateFollowsBeginAndEnd()
    {
        var undo = new UndoService(_journal, _fs, _bin);

        Assert.False(undo.IsBusy);
        Assert.True(undo.TryBegin());
        Assert.True(undo.IsBusy);
        Assert.False(undo.TryBegin()); // already taken
        undo.End();
        Assert.False(undo.IsBusy);
        Assert.True(undo.TryBegin()); // released by End
    }

    // ---- Register: what becomes the last undoable action ----

    [Fact]
    public void Register_FailedMoveWithDestination_IsNotUndoable()
    {
        _undo.Register(Moved(succeeded: false));

        Assert.Empty(_undo.MoveHistory);
        Assert.False(_undo.LastUndoAction is not null);
    }

    [Fact]
    public void Register_RejectedMoveReportedAsSucceeded_IsNotUndoable()
    {
        _undo.Register(Moved(rejected: true));

        Assert.Empty(_undo.MoveHistory);
        Assert.False(_undo.LastUndoAction is not null);
    }

    [Fact]
    public void Register_FailedRecycle_IsNotUndoable()
    {
        _undo.Register(Moved(succeeded: false, destination: null, operation: FileOperationType.Recycle));

        Assert.False(_undo.LastUndoAction is not null);
    }

    [Fact]
    public void Register_SucceededCopyWithDestination_IsNotUndoable()
    {
        _undo.Register(Moved(operation: FileOperationType.Copy));

        Assert.Empty(_undo.MoveHistory);
        Assert.False(_undo.LastUndoAction is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Register_SucceededMoveWithoutDestinationPath_IsNotUndoable(string? destination)
    {
        _undo.Register(Moved(destination: destination));

        Assert.Empty(_undo.MoveHistory);
        Assert.False(_undo.LastUndoAction is not null);
    }

    [Fact]
    public void Register_SucceededMove_BecomesTheUndoableLastAction()
    {
        _undo.Register(Moved());

        Assert.Single(_undo.MoveHistory);
        Assert.True(_undo.MoveHistory.Count > 0);
        Assert.True(_undo.LastUndoAction is not null);
    }

    // ---- single Move undo ----

    [Fact]
    public async Task UndoMoveAsync_AfterSuccess_ForgetsTheMoveAsLastAction()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _undo.Register(Moved());

        var result = await _undo.UndoMoveAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(_undo.LastUndoAction is not null); // the undone Move is no longer something Ctrl+Z could undo again
        Assert.True(_fs.FileExists(Jpeg));
    }

    [Fact]
    public async Task UndoMoveAsync_CalledDirectlyWhileARecycleIsTheLastAction_KeepsTheRecycleUndoable()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _undo.Register(Moved());
        _undo.Register(new FileActionResult(true, FileOperationType.Recycle, @"C:\photos\b.jpg", null, 4, Stamp, null)); // the latest action

        var result = await _undo.UndoMoveAsync(); // undoes the earlier Move only

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(_undo.LastUndoAction is not null);
    }
    [Fact]
    public async Task UndoMoveAsync_UsesTheMoveOverrideForTheReverseMove()
    {
        var calls = new List<(string Source, string Destination)>();
        var undo = new UndoService(_journal, _fs, _bin, _service, (source, destination) =>
        {
            calls.Add((source, destination));
            _fs.Move(source, destination);
            return Task.CompletedTask;
        }, new FixedClock(LaterStamp));
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        undo.Register(Moved());

        var result = await undo.UndoMoveAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal([(MovedJpeg, Jpeg)], calls);
        Assert.True(_fs.FileExists(Jpeg));
    }

    [Fact]
    public async Task UndoMoveAsync_UsesTheInjectedClockForItsJournalLines()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _undo.Register(Moved());
        _fs.MoveHook = (source, _) => source == MovedJpeg ? new IOException("simulated") : null;

        var result = await _undo.UndoMoveAsync();

        Assert.False(result.Succeeded);
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(LaterStamp, failed.TimestampUtc);
    }

    [Fact]
    public async Task UndoMoveAsync_HistoryFilledDirectlyAndMoveJournaled_UsesTheJournalFingerprint()
    {
        _fs.AddFile(Jpeg, "jpeg", Stamp);
        var moved = await _service.ExecuteAsync(new FileActionRequest(Jpeg, FileOperationType.Move, "selected"));
        Assert.True(moved.Succeeded, moved.Error);
        var undo = new UndoService(_journal, _fs, _bin, _service, clock: new FixedClock(LaterStamp)); // nothing Registered
        undo.MoveHistory.Push((Jpeg, moved.DestinationPath!));

        var result = await undo.UndoMoveAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(_fs.FileExists(Jpeg));
        Assert.False(_fs.FileExists(moved.DestinationPath!));
    }

    [Fact]
    public async Task UndoMoveAsync_HistoryFilledDirectlyWithoutAJournalLine_FailsAsFingerprintMissingAndKeepsTheEntry()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _undo.MoveHistory.Push((Jpeg, MovedJpeg));

        var result = await _undo.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreUndoFailed(Tr.CoreUndoFingerprintMissing), result.ErrorMessage);
        Assert.Single(_undo.MoveHistory); // pushed back: nothing was proven, nothing was dropped
        Assert.True(_fs.FileExists(MovedJpeg));
        Assert.False(_fs.FileExists(Jpeg));
    }

    // ---- single Recycle undo ----

    [Fact]
    public async Task UndoLastAsync_RecycleWhileAnotherOperationRuns_IsRejectedAndRestoresNothing()
    {
        _undo.Register(Moved(destination: null, operation: FileOperationType.Recycle));
        Assert.True(_service.TryBegin()); // a file action is in flight

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.Rejected);
        Assert.Equal(Tr.CoreUndoBusy, result.ErrorMessage);
        Assert.Equal(0, _bin.RestoreCalls);
        Assert.True(_undo.LastUndoAction is not null); // still undoable once the gate is free
        _service.End();
    }

    // ---- group Move undo ----

    [Fact]
    public async Task UndoLastAsync_GroupMoveWhenTheMovedFileIsGone_FailsAndDoesNotCountItAsRestored()
    {
        _undo.RegisterGroup(GroupResult(FileOperationType.Move, [Member(Jpeg, MovedJpeg, 4)]));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreUndoFailed(Tr.CoreUndoDestinationChangedAfterMove), result.ErrorMessage);
        Assert.Null(result.RestoredPaths);
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveWhenADifferentFileSitsAtTheOriginalPath_FailsAndLeavesThatFileAlone()
    {
        _fs.AddFile(Jpeg, "someone else's file", Stamp); // not the moved file: another length
        _undo.RegisterGroup(GroupResult(FileOperationType.Move, [Member(Jpeg, MovedJpeg, 4)]));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreUndoFailed(Tr.CoreUndoDestinationChangedAfterMove), result.ErrorMessage);
        Assert.Null(result.RestoredPaths);
        Assert.Equal(19, _fs.GetFileStat(Jpeg)!.Length);
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveWhoseSourceIsKeptByTheMove_FailsVerificationInsteadOfClaimingSuccess()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _fs.MoveLeavesSource = true; // the reverse move copies but cannot remove the file at the move destination
        _undo.RegisterGroup(GroupResult(FileOperationType.Move, [Member(Jpeg, MovedJpeg, 4)]));

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(Tr.CoreUndoFailed(new JournalCodedException(JournalErrors.VerifySizeChanged).Message), result.ErrorMessage);
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveFailedWithoutRestoringAnything_DoesNotRememberAPartialUndo()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _fs.AddFile(MovedRaw, "raw edited elsewhere", Stamp); // not the moved RAW: the pre-scan fails before anything moves
        _undo.RegisterGroup(GroupResult(FileOperationType.Move, [Member(Jpeg, MovedJpeg, 4), Member(Raw, MovedRaw, 8)]));
        var first = await _undo.UndoLastAsync();
        Assert.False(first.Succeeded);
        Assert.False(_fs.FileExists(Jpeg)); // nothing was restored by this undo

        // The user then puts everything back by hand.
        _fs.AddFile(MovedRaw, "raw data", Stamp);
        _fs.Move(MovedJpeg, Jpeg);
        _fs.Move(MovedRaw, Raw);
        var second = await _undo.UndoLastAsync();

        // Nothing was restored by the earlier attempt, so this is "already handled", not a completed partial undo.
        Assert.False(second.Succeeded);
        Assert.Equal(Tr.CoreUndoFailed(Tr.CoreRecoveryAlreadyHandled), second.ErrorMessage);
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveFailsTwiceAfterPartialRestore_RetryClosesBothFailedUndoLines()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _fs.AddFile(MovedRaw, "raw data", Stamp);
        _undo.RegisterGroup(GroupResult(FileOperationType.Move, [Member(Jpeg, MovedJpeg, 4), Member(Raw, MovedRaw, 8)]));
        _fs.MoveHook = (source, _) => source == MovedRaw ? new IOException("raw busy") : null;

        var first = await _undo.UndoLastAsync();  // JPEG restored, RAW fails: first Failed line
        var second = await _undo.UndoLastAsync(); // JPEG already back, RAW fails again: second Failed line
        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(2, _journal.ReadFailedOperations().Count);

        _fs.MoveHook = null;
        var third = await _undo.UndoLastAsync();

        Assert.True(third.Succeeded, third.ErrorMessage);
        Assert.Empty(_journal.ReadFailedOperations()); // both earlier Failed lines were closed, not just the latest
    }

    [Fact]
    public async Task UndoLastAsync_GroupMoveSuccess_DropsTheFingerprintOfEveryMemberDestination()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _undo.Register(Moved()); // a single Move registered earlier left a fingerprint for the same destination
        Assert.Equal(1, _undo.MoveFingerprintCount);
        _undo.RegisterGroup(GroupResult(FileOperationType.Move, [Member(Jpeg, MovedJpeg, 4)]));

        var result = await _undo.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(0, _undo.MoveFingerprintCount);
        Assert.False(_undo.LastUndoAction is not null);
    }

    [Fact]
    public async Task UndoLastAsync_GroupMove_ReportsTheJournalEntrySourceAndDestinationWhenPresent()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        const string entrySource = @"C:\photos\entry.jpg";
        const string entryDestination = @"C:\photos\selected\entry.jpg";
        _undo.RegisterGroup(GroupResult(FileOperationType.Move, [Member(Jpeg, MovedJpeg, 4)], entrySource, entryDestination));

        var result = await _undo.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(entrySource, result.Source);
        Assert.Equal(entryDestination, result.Destination);
    }

    // ---- group Recycle undo ----

    [Fact]
    public async Task UndoLastAsync_GroupRecycleOfNormalMembers_RestoresAllWithoutAnyNote()
    {
        _undo.RegisterGroup(GroupResult(FileOperationType.Recycle, [Member(Jpeg, null, 4), Member(Raw, null, 8)]));

        var result = await _undo.UndoLastAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Null(result.ErrorMessage); // no "partially restored" warning when nothing was permanent
        Assert.Equal([Jpeg, Raw], result.RestoredPaths);
    }

    [Fact]
    public async Task UndoLastAsync_FailedGroupDeleteWhereNothingCanBeRestored_ReportsNoRestoredPathsAndLeavesTheFailedLineAlone()
    {
        var jpeg = Member(Jpeg, null, 4);
        var raw = Member(Raw, null, 8);
        var failedLine = new JournalEntry("delete-group", FileOperationType.Recycle, JournalState.Failed, Jpeg, null, 4, Stamp, Stamp,
            Error: "boom", GroupId: "g", GroupMembers: [jpeg, raw]);
        _journal.Append(failedLine);
        _undo.RegisterGroup(new CaptureGroupActionResult(false, false, FileOperationType.Recycle, "g", failedLine,
            [new CaptureGroupMemberResult(jpeg, true, false), new CaptureGroupMemberResult(raw, false, false, "boom", SourceExists: true)], "boom"));
        _bin.FailFor = Jpeg; // the one recycled member cannot be restored

        var result = await _undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Null(result.RestoredPaths); // nothing came back: null, not an empty list
        // Nothing was restored, so the Failed delete line is not rewritten (a rewrite would carry the undo's later stamp).
        var delete = Assert.Single(_journal.ReadFailedOperations(), entry => entry.Id == "delete-group");
        Assert.Equal(Stamp, delete.TimestampUtc);
        Assert.Equal([jpeg, raw], delete.GroupMembers);
    }
}