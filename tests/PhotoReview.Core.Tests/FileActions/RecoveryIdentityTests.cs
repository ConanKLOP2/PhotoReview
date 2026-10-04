using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// R15/R17/R18 (review 2026-10-04): every Undo / Recovery / reconcile step that decides "this path holds the file" must prove
/// identity (size + write time of the journaled file), not mere existence, and must re-prove it after the Prepared line and
/// right before the mutation. A replacement that appears in that window must never be moved, committed or counted as restored.
/// Fakes only: no real Recycle Bin and no real file system.
/// </summary>
public sealed class RecoveryIdentityTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OtherStamp = Stamp.AddHours(3);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string MovedJpeg = @"C:\photos\selected\a.jpg";
    private const string MovedRaw = @"C:\photos\selected\a.cr2";

    private readonly InMemoryFileSystem _fs = new();
    private readonly OperationJournal _journal;
    private readonly PlantingBin _bin;

    public RecoveryIdentityTests()
    {
        _journal = new OperationJournal(Paths, _fs, new FixedClock());
        _bin = new PlantingBin(_fs);
    }

    private UndoService NewUndo() => new(_journal, _fs, _bin, new FileActionService(_journal, _fs, new FixedClock(), _bin), clock: new FixedClock());

    private RecoveryRetryService NewRetry() => new(_journal, _fs, new FixedClock(), _bin);

    /// <summary>Runs <paramref name="plant"/> once, at the first journal append after this call (the Prepared line of the action under test).</summary>
    private void PlantAtNextJournalAppend(Action plant)
    {
        var armed = true;
        _fs.OpenAppendHook = path =>
        {
            if (armed && string.Equals(path, Paths.JournalFile, StringComparison.OrdinalIgnoreCase))
            {
                armed = false;
                plant();
            }
            return null;
        };
    }

    private static JournalEntry UndoRecycleGroup(JournalState state, params JournalGroupMember[] members) => new(
        "undo-recycle", FileOperationType.Recycle, state, members[0].Source, null, members[0].Size, members[0].LastWriteUtc, Stamp,
        Undo: true, GroupId: "g", GroupMembers: members);

    // ---- R15: reconcile of a prepared group Recycle undo ----

    [Theory]
    [InlineData(11, false)] // a different file (size) sits at the original path
    [InlineData(10, true)] // same size, other write time
    public void Reconcile_GroupRecycleUndoWithForeignFileAtOneMember_StaysFailedWhileAnotherMemberIsStillInTheBin(int foreignSize, bool otherStamp)
    {
        // The JPEG was restored. The RAW is still in the Recycle Bin, but a foreign / replacement file now sits at its original
        // path (every member path exists, so only identity can tell the undo did not complete).
        _fs.AddFile(Jpeg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', foreignSize), otherStamp ? OtherStamp : Stamp);
        _journal.Append(UndoRecycleGroup(JournalState.Prepared, new(Jpeg, null, 10, Stamp), new(Raw, null, 10, Stamp)));

        var outcome = Assert.Single(_journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
        Assert.Single(_journal.ReadFailedOperations());
    }

    [Fact]
    public void Reconcile_GroupRecycleUndoWithEveryMemberBackWithItsIdentity_Commits()
    {
        _fs.AddFile(Jpeg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        _journal.Append(UndoRecycleGroup(JournalState.Prepared, new(Jpeg, null, 10, Stamp), new(Raw, null, 10, Stamp)));

        var outcome = Assert.Single(_journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Committed, outcome.State);
    }

    // ---- R15 race extension: group Recycle undo retried from Recovery ----

    [Fact]
    public async Task RetryGroupRecycleUndo_ForeignFileAppearsAtPendingMemberAfterThePreCheck_FailsInsteadOfCommitting()
    {
        var failed = UndoRecycleGroup(JournalState.Failed, new(Jpeg, null, 10, Stamp), new(Raw, null, 10, Stamp));
        _journal.Append(failed);
        PlantAtNextJournalAppend(() => _fs.AddFile(Jpeg, new string('f', 3), OtherStamp));

        var result = await NewRetry().RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.NotEqual(JournalState.Committed, result.Entry?.State);
        Assert.Equal(3, _fs.GetFileStat(Jpeg)!.Length); // the foreign file is untouched
    }

    [Fact]
    public async Task RetryGroupRecycleUndo_BinReportsRestoreButAForeignFileSitsAtTheMember_Fails()
    {
        var failed = UndoRecycleGroup(JournalState.Failed, new JournalGroupMember(Jpeg, null, 10, Stamp));
        _journal.Append(failed);
        _bin.PlantInsteadOfRestore = (path, _, stamp) => _fs.AddFile(path, new string('f', 3), stamp);

        var result = await NewRetry().RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.NotEqual(JournalState.Committed, result.Entry?.State);
    }

    // ---- R17: single Recovery retry ----

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task RetryMoveOrCopy_SourceReplacedAfterThePreCheck_IsRefusedAndNothingIsMutated(FileOperationType type)
    {
        const string destination = @"D:\sorted\a.jpg";
        _fs.AddFile(Jpeg, new string('j', 5), Stamp);
        var failed = new JournalEntry("retry-op", type, JournalState.Failed, Jpeg, destination, 5, Stamp, Stamp, "Previous error");
        _journal.Append(failed);
        PlantAtNextJournalAppend(() => _fs.AddFile(Jpeg, new string('r', 9), OtherStamp)); // a different photo takes over the path

        var result = await NewRetry().RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(destination)); // the replacement was neither moved nor copied under the old operation
        Assert.Equal(9, _fs.GetFileStat(Jpeg)!.Length);
        Assert.DoesNotContain(_journal.ReadCommittedMoves(), entry => entry.Id == "retry-op");
    }

    // ---- R18: Undo ----

    [Fact]
    public async Task UndoMove_DestinationReplacedAfterThePreflight_IsRefusedAndTheReplacementIsNotRestored()
    {
        _fs.AddFile(MovedJpeg, new string('j', 4), Stamp);
        var undo = NewUndo();
        undo.Register(new FileActionResult(true, FileOperationType.Move, Jpeg, MovedJpeg, 4, Stamp, null));
        PlantAtNextJournalAppend(() => _fs.AddFile(MovedJpeg, new string('z', 9), OtherStamp));

        var result = await undo.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(Jpeg));
        Assert.Equal(9, _fs.GetFileStat(MovedJpeg)!.Length);
        Assert.DoesNotContain(_journal.ReadCommittedMoves(), entry => entry.Undo == true);
    }

    [Fact]
    public async Task UndoGroupMove_MemberReplacedAfterThePreflight_NoReplacementIsRestoredOrCommitted()
    {
        _fs.AddFile(MovedJpeg, "jpeg", Stamp);
        _fs.AddFile(MovedRaw, "raw data", Stamp);
        var undo = NewUndo();
        var members = new JournalGroupMember[] { new(Jpeg, MovedJpeg, 4, Stamp), new(Raw, MovedRaw, 8, Stamp) };
        var entry = new JournalEntry("g", FileOperationType.Move, JournalState.Committed, Jpeg, MovedJpeg, 4, Stamp, Stamp,
            GroupId: "g", GroupMembers: members);
        undo.RegisterGroup(new CaptureGroupActionResult(true, false, FileOperationType.Move, "g", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null));
        PlantAtNextJournalAppend(() => _fs.AddFile(MovedRaw, new string('z', 8), OtherStamp)); // same size, other write time

        var result = await undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.False(_fs.FileExists(Raw)); // the replacement never reached the original path
        Assert.Equal(OtherStamp, _fs.GetFileStat(MovedRaw)!.LastWriteUtc);
        Assert.DoesNotContain(_journal.ReadCommittedMoves(), item => item.Undo == true);
    }

    [Theory]
    [InlineData(false)] // nothing at the original path although the bin claimed success
    [InlineData(true)] // a file with the right write time but the wrong size
    public async Task UndoRecycle_BinReportsRestoreWithoutTheRecycledFileAtThePath_IsAFailure(bool plantWrongSize)
    {
        var undo = NewUndo();
        undo.Register(new FileActionResult(true, FileOperationType.Recycle, Jpeg, null, 10, Stamp, null));
        _bin.PlantInsteadOfRestore = plantWrongSize ? (path, _, stamp) => _fs.AddFile(path, new string('f', 3), stamp) : (_, _, _) => { };

        var result = await undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(undo.LastUndoAction); // still undoable / not forgotten
    }

    [Fact]
    public async Task UndoGroupRecycle_BinReportsRestoreButAWrongSizeFileSitsAtTheMember_IsAFailureWithNothingReportedRestored()
    {
        var undo = NewUndo();
        var members = new JournalGroupMember[] { new(Jpeg, null, 10, Stamp) };
        var entry = new JournalEntry("g", FileOperationType.Recycle, JournalState.Committed, Jpeg, null, 10, Stamp, Stamp,
            GroupId: "g", GroupMembers: members);
        undo.RegisterGroup(new CaptureGroupActionResult(true, false, FileOperationType.Recycle, "g", entry,
            members.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray(), null));
        _bin.PlantInsteadOfRestore = (path, _, stamp) => _fs.AddFile(path, new string('f', 3), stamp);

        var result = await undo.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Null(result.RestoredPaths);
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp.AddMinutes(5);
    }

    /// <summary>Fake bin: a "restore" re-creates the recorded file unless <see cref="PlantInsteadOfRestore"/> says what to leave on disk.</summary>
    private sealed class PlantingBin(InMemoryFileSystem fs) : IRecycleBin
    {
        public Action<string, long, DateTime>? PlantInsteadOfRestore { get; set; }

        public void SendToRecycleBin(string path) => throw new NotSupportedException();

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            if (PlantInsteadOfRestore is { } plant)
            {
                plant(originalPath, expectedSize, expectedLastWriteUtc);
                return true;
            }
            fs.AddFile(originalPath, new string('x', checked((int)expectedSize)), expectedLastWriteUtc);
            return true;
        }
    }
}
