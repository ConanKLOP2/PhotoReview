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
