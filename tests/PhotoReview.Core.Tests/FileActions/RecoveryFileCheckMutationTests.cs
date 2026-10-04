using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gaps of <see cref="RecoveryFileCheck"/>: the per-member verdict of an undo-Delete (Recycle) group and
/// <see cref="RecoveryCheckResult.IsGroup"/>.
/// </summary>
public sealed class RecoveryFileCheckMutationTests
{
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private const string Jpg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";

    private readonly InMemoryFileSystem _fs = new();

    private static JournalGroupMember Member(string path, long size = 10) => new(path, null, size, Stamp);

    private static JournalEntry UndoRecycleGroup(params JournalGroupMember[] members) => new(
        "undo-delete", FileOperationType.Recycle, JournalState.Failed, members[0].Source, null, members[0].Size, Stamp, Stamp,
        Error: "x", Undo: true, GroupId: "capture", GroupMembers: members);

    private RecoveryCheckResult Check(JournalEntry entry) => new RecoveryFileCheck(_fs).Check(entry);

    [Fact]
    public void Check_UndoRecycleGroupMemberBackAtItsOriginalPath_MemberIsAlreadyDone()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp); // restored from the bin already
        // RAW still in the bin (missing on disk).

        var result = Check(UndoRecycleGroup(Member(Jpg), Member(Raw)));

        Assert.Equal(RecoveryVerdict.AlreadyDone, result.GroupMembers![0].Verdict);
        Assert.Equal(RecoveryVerdict.RecycleUnverifiable, result.GroupMembers![1].Verdict);
        Assert.Equal(RecoveryVerdict.CanRetry, result.Verdict); // the missing member can be restored by a retry
    }

    [Fact]
    public void Check_UndoRecycleGroupMemberWithDifferentContentAtItsPath_MemberIsNotAlreadyDone()
    {
        _fs.AddFile(Jpg, new string('j', 11), Stamp); // somebody else's file now sits where the restore must go

        var result = Check(UndoRecycleGroup(Member(Jpg), Member(Raw)));

        Assert.NotEqual(RecoveryVerdict.AlreadyDone, result.GroupMembers![0].Verdict);
        Assert.NotEqual(RecoveryVerdict.CanRetry, result.Verdict);
    }

    [Fact]
    public void Check_UndoRecycleGroupAllMembersBack_EveryMemberIsAlreadyDone()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp);
        _fs.AddFile(Raw, new string('r', 10), Stamp);

        var result = Check(UndoRecycleGroup(Member(Jpg), Member(Raw)));

        Assert.All(result.GroupMembers!, member => Assert.Equal(RecoveryVerdict.AlreadyDone, member.Verdict));
        // W2-FA-02: every file is back on disk, so the undo is done: not "probably in the Recycle Bin, cannot verify".
        Assert.Equal(RecoveryVerdict.AlreadyDone, result.Verdict);
    }

    [Fact]
    public void Check_NonUndoRecycleGroupAllMembersMissing_StaysRecycleUnverifiable()
    {
        var entry = UndoRecycleGroup(Member(Jpg), Member(Raw)) with { Undo = null, State = JournalState.Committed };

        var result = Check(entry);

        Assert.Equal(RecoveryVerdict.RecycleUnverifiable, result.Verdict);
    }

    private static JournalEntry MoveUndoGroup(params JournalGroupMember[] members) => new(
        "undo-move", FileOperationType.Move, JournalState.Failed, members[0].Source, members[0].Destination, members[0].Size, Stamp, Stamp,
        Error: "x", Undo: true, GroupId: "capture", GroupMembers: members);

    [Fact]
    public void Check_UndoMoveGroupWithARetryableAndAConflictingMember_IsConflictNotCanRetry()
    {
        _fs.AddFile(Jpg, new string('j', 10), Stamp); // S exists, D missing -> CanRetry
        _fs.AddFile(Raw, new string('r', 10), Stamp);
        _fs.AddFile(@"C:\out.cr2", new string('r', 10), Stamp); // S and D exist on a Move -> Conflict

        var result = Check(MoveUndoGroup(
            new JournalGroupMember(Jpg, @"C:\out.jpg", 10, Stamp),
            new JournalGroupMember(Raw, @"C:\out.cr2", 10, Stamp)));

        Assert.Equal(RecoveryVerdict.CanRetry, result.GroupMembers![0].Verdict);
        Assert.Equal(RecoveryVerdict.Conflict, result.GroupMembers![1].Verdict);
        Assert.Equal(RecoveryVerdict.Conflict, result.Verdict);
    }

    [Fact]
    public void Check_DeleteGroupWithUnreadableAndMissingMembers_IsUnknownNotCanRetry()
    {
        _fs.StatHook = p => p == Jpg ? new UnauthorizedAccessException("denied") : null;
        var entry = UndoRecycleGroup(Member(Jpg), Member(Raw)) with { Undo = null };

        var result = Check(entry);

        Assert.Equal(RecoveryVerdict.Unknown, result.GroupMembers![0].Verdict);
        Assert.Equal(RecoveryVerdict.RecycleUnverifiable, result.GroupMembers![1].Verdict);
        Assert.Equal(RecoveryVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public void Check_UndoRecycleGroupWithUnreadableAndChangedMembers_IsUnknownNotCanRetry()
    {
        _fs.AddFile(Raw, new string('r', 11), Stamp); // a different file sits at the restore path -> Unknown
        _fs.StatHook = p => p == Jpg ? new UnauthorizedAccessException("denied") : null; // -> Unknown

        var result = Check(UndoRecycleGroup(Member(Jpg), Member(Raw)));

        Assert.Equal(RecoveryVerdict.Unknown, result.GroupMembers![0].Verdict);
        Assert.Equal(RecoveryVerdict.Unknown, result.GroupMembers![1].Verdict);
        Assert.Equal(RecoveryVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public void IsGroup_WithoutMembers_IsFalse()
    {
        Assert.False(ResultWithMembers(null).IsGroup);
    }

    [Fact]
    public void IsGroup_WithAnEmptyMemberList_IsFalse()
    {
        Assert.False(ResultWithMembers([]).IsGroup);
    }

    [Fact]
    public void IsGroup_WithMembers_IsTrue()
    {
        var entry = UndoRecycleGroup(Member(Jpg));
        var member = new RecoveryGroupMemberCheck(entry.GroupMembers![0], ResultWithMembers(null));

        Assert.True(ResultWithMembers([member]).IsGroup);
    }

    private static RecoveryCheckResult ResultWithMembers(IReadOnlyList<RecoveryGroupMemberCheck>? members)
    {
        var entry = new JournalEntry("op", FileOperationType.Move, JournalState.Failed, Jpg, @"C:\out\a.jpg", 10, Stamp, Stamp);
        var path = new RecoveryPathCheck(Jpg, RecoveryPathStatus.Exists, 10, Stamp, true);
        return new RecoveryCheckResult(entry, path, null, RecoveryVerdict.Unknown, members);
    }
}