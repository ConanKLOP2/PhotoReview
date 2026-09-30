using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class JournalGroupReconcileTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reconcile_GroupMoveCommitsOnlyWhenEveryMemberReachedItsDestination()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\selected\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        var prepared = Prepared(
            new(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp),
            new(@"C:\photos\a.cr2", @"C:\selected\a.cr2", 100, Stamp));
        journal.Append(prepared);

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Committed, outcome.State);
        Assert.Equal(prepared.GroupMembers, outcome.GroupMembers);
    }

    [Fact]
    public void Reconcile_PartialGroupMoveRemainsFailedAndVisibleAsOneOperation()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(Prepared(
            new(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp),
            new(@"C:\photos\a.cr2", @"C:\selected\a.cr2", 100, Stamp)));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
        Assert.Equal(2, Assert.Single(journal.ReadFailedOperations()).GroupMembers!.Count);
    }

    [Fact]
    public void Reconcile_PartialGroupUndoUsesReversedManifestAndStaysFailed()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\selected\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        var members = new[]
        {
            new JournalGroupMember(@"C:\selected\a.jpg", @"C:\photos\a.jpg", 10, Stamp),
            new JournalGroupMember(@"C:\selected\a.cr2", @"C:\photos\a.cr2", 100, Stamp),
        };
        journal.Append(new JournalEntry("undo", FileOperationType.Move, JournalState.Prepared,
            members[0].Source, members[0].Destination, members[0].Size, members[0].LastWriteUtc, Stamp,
            Undo: true, GroupId: "undo-group", GroupMembers: members));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
        Assert.Equal(2, Assert.Single(journal.ReadFailedOperations()).GroupMembers!.Count);
    }

    [Fact]
    public void Reconcile_GroupUndoCrashedBeforeAnyMove_StaysFailed()
    {
        // The undo manifest is already in undo direction (Source = original destination). Nothing was moved back yet:
        // both files still sit at the undo sources, so the undo never ran and must not be recorded as Committed.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\selected\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(UndoPrepared());

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, outcome.State);
        Assert.Equal(JournalErrors.PendingUnconfirmed, outcome.ErrorCode);
    }

    [Fact]
    public void Reconcile_GroupUndoCompletedButOutcomeNotJournaled_Commits()
    {
        // Every member is back at its original location (the undo destination); only the Committed append was lost.
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\photos\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(UndoPrepared());

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Committed, outcome.State);
        Assert.Empty(journal.ReadPendingOperations());
        Assert.Empty(journal.ReadFailedOperations());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Reconcile_GroupLineWithBlankMemberSource_IsQuarantinedAndLaterPendingEntriesStillReconcile(string? badSource)
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\b.jpg", new string('j', 10), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(Prepared(new JournalGroupMember(badSource!, @"C:\selected\a.jpg", 10, Stamp)) with { Id = "bad-group" });
        journal.Append(new JournalEntry("good", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\b.jpg", @"C:\selected\b.jpg", 10, Stamp, Stamp));

        var reconciled = journal.ReconcilePendingOperations(); // used to throw ArgumentException out of the loop

        var outcome = Assert.Single(reconciled);
        Assert.Equal("good", outcome.Id);
        Assert.Equal(JournalState.Committed, outcome.State);
    }

    [Fact]
    public void TryParse_GroupLineWithNullMemberOrBlankSource_IsRejected()
    {
        var good = "{\"Id\":\"g\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":\"C:\\\\a.jpg\",\"GroupMembers\":[{\"Source\":\"C:\\\\a.jpg\",\"Destination\":\"C:\\\\s\\\\a.jpg\",\"Size\":1}]}";
        var nullMember = good.Replace("[{", "[null,{", StringComparison.Ordinal);
        var blank = good.Replace("\"Source\":\"C:\\\\a.jpg\",\"Destination", "\"Source\":\" \",\"Destination", StringComparison.Ordinal);

        Assert.NotNull(JournalLineParser.TryParse(good));
        Assert.Null(JournalLineParser.TryParse(nullMember));
        Assert.Null(JournalLineParser.TryParse(blank));
    }

    [Fact]
    public void Dismiss_GroupEntryReReadFromJournal_IsDismissedNotSkipped()
    {
        var journal = NewJournal(new InMemoryFileSystem());
        journal.Append(Prepared(
            new(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp),
            new(@"C:\photos\a.cr2", @"C:\selected\a.cr2", 100, Stamp)) with { State = JournalState.Failed });
        var snapshot = Assert.Single(journal.ReadFailedOperations());

        var outcome = journal.Dismiss([snapshot]);

        Assert.Empty(outcome.Skipped);
        Assert.Equal(snapshot.Id, Assert.Single(outcome.Dismissed).Id);
        Assert.Empty(journal.ReadFailedOperations());
    }

    [Fact]
    public void Dismiss_MixedGroupAndSingleEntries_ClearsAllOfThem()
    {
        var journal = NewJournal(new InMemoryFileSystem());
        journal.Append(Prepared(
            new(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp),
            new(@"C:\photos\a.cr2", @"C:\selected\a.cr2", 100, Stamp)) with { State = JournalState.Failed });
        journal.Append(new JournalEntry("single", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\b.jpg", @"C:\selected\b.jpg", 5, Stamp, Stamp));

        var outcome = journal.Dismiss(journal.ReadFailedOperations());

        Assert.Empty(outcome.Skipped);
        Assert.Equal(2, outcome.Dismissed.Count);
        Assert.Empty(journal.ReadFailedOperations());
    }

    [Fact]
    public void JournalEntry_GroupEntriesWithEqualMembers_AreValueEqual()
    {
        var left = Prepared(new JournalGroupMember(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp));
        var right = Prepared(new JournalGroupMember(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp));
        var different = Prepared(new JournalGroupMember(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 11, Stamp));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, different);
        Assert.NotEqual(left, left with { GroupMembers = null });
    }

    private static JournalEntry UndoPrepared()
    {
        var members = new JournalGroupMember[]
        {
            new(@"C:\selected\a.jpg", @"C:\photos\a.jpg", 10, Stamp),
            new(@"C:\selected\a.cr2", @"C:\photos\a.cr2", 100, Stamp),
        };
        return new JournalEntry("undo", FileOperationType.Move, JournalState.Prepared,
            members[0].Source, members[0].Destination, members[0].Size, members[0].LastWriteUtc, Stamp,
            Undo: true, GroupId: "undo-group", GroupMembers: members);
    }

    private static OperationJournal NewJournal(InMemoryFileSystem fileSystem) =>
        new(Paths, fileSystem, new FixedClock(Stamp.AddMinutes(1)));

    private static JournalEntry Prepared(params JournalGroupMember[] members) => new(
        "group-op", FileOperationType.Move, JournalState.Prepared,
        members[0].Source, members[0].Destination, members[0].Size, members[0].LastWriteUtc, Stamp,
        GroupId: "capture-a", GroupMembers: members);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
