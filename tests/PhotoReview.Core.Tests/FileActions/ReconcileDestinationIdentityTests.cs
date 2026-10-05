using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// R01c (review 2026-10-04): startup reconcile settles a Prepared Move/Copy as Committed only when the destination is the recorded
/// file: the journaled size AND the journaled write time (copy and move keep the source's write time), allowing the 2 s rounding of a
/// FAT destination. A same-size file with another write time is a replacement and stays Failed for the user. The journal format is
/// unchanged: the source's write time is already in every entry.
/// </summary>
public sealed class ReconcileDestinationIdentityTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OtherStamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Source = @"C:\photos\a.jpg";
    private const string Destination = @"C:\selected\a.jpg";

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private static OperationJournal NewJournal(InMemoryFileSystem fileSystem) =>
        new(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, new FixedClock(Stamp.AddMinutes(1)));

    private static JournalEntry Single(FileOperationType type) =>
        new("op", type, JournalState.Prepared, Source, Destination, 10, Stamp, Stamp);

    private static JournalEntry Group(FileOperationType type) => new(
        "group-op", type, JournalState.Prepared, Source, Destination, 10, Stamp, Stamp,
        GroupId: "capture", GroupMembers: [new JournalGroupMember(Source, Destination, 10, Stamp), new JournalGroupMember(@"C:\photos\a.cr2", @"C:\selected\a.cr2", 100, Stamp)]);

    private static JournalState Settle(InMemoryFileSystem fileSystem, JournalEntry prepared)
    {
        var journal = NewJournal(fileSystem);
        journal.Append(prepared);
        return Assert.Single(journal.ReconcilePendingOperations()).State;
    }

    [Theory(DisplayName = "R01c: a single entry is Committed only for the recorded destination file")]
    [InlineData(FileOperationType.Copy, 0, true)]
    [InlineData(FileOperationType.Copy, 1, true)] // FAT rounds the write time to 2 s
    [InlineData(FileOperationType.Copy, 3600, false)] // same size, another file
    [InlineData(FileOperationType.Move, 0, true)]
    [InlineData(FileOperationType.Move, 3600, false)]
    public void Reconcile_SingleEntry_CommitsOnlyWhenTheDestinationIsTheRecordedFile(FileOperationType type, int destinationOffsetSeconds, bool committed)
    {
        var fileSystem = new InMemoryFileSystem();
        if (type == FileOperationType.Copy) fileSystem.AddFile(Source, new string('s', 10), Stamp);
        fileSystem.AddFile(Destination, new string('d', 10), Stamp.AddSeconds(destinationOffsetSeconds));

        var state = Settle(fileSystem, Single(type));

        Assert.Equal(committed ? JournalState.Committed : JournalState.Failed, state);
    }

    [Theory(DisplayName = "R01c: a group entry is Committed only when every member is the recorded destination file")]
    [InlineData(FileOperationType.Copy, 0, true)]
    [InlineData(FileOperationType.Copy, 3600, false)]
    [InlineData(FileOperationType.Move, 0, true)]
    [InlineData(FileOperationType.Move, 3600, false)]
    public void Reconcile_GroupEntry_CommitsOnlyWhenEveryDestinationIsTheRecordedFile(FileOperationType type, int rawDestinationOffsetSeconds, bool committed)
    {
        var fileSystem = new InMemoryFileSystem();
        if (type == FileOperationType.Copy)
        {
            fileSystem.AddFile(Source, new string('s', 10), Stamp);
            fileSystem.AddFile(@"C:\photos\a.cr2", new string('s', 100), Stamp);
        }
        fileSystem.AddFile(Destination, new string('d', 10), Stamp);
        fileSystem.AddFile(@"C:\selected\a.cr2", new string('d', 100), Stamp.AddSeconds(rawDestinationOffsetSeconds));

        var state = Settle(fileSystem, Group(type));

        Assert.Equal(committed ? JournalState.Committed : JournalState.Failed, state);
    }
}
