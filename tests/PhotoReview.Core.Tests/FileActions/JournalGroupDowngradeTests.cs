using System.Text;
using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// ADR 0003 downgrade guard. An older build does not know GroupId/GroupMembers: it reads a group line as a move of the first
/// member and appends its outcome WITHOUT the members (the fixture below writes exactly that shape by dropping them from a
/// <c>with</c> copy, which serializes like the old record). A new build must not trust such a verdict.
/// </summary>
public sealed class JournalGroupDowngradeTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JournalGroupMember[] Members =
    [
        new(@"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp),
        new(@"C:\photos\a.cr2", @"C:\selected\a.cr2", 100, Stamp),
    ];

    private static JournalEntry GroupPrepared() => new(
        "group-op", FileOperationType.Move, JournalState.Prepared,
        Members[0].Source, Members[0].Destination, Members[0].Size, Members[0].LastWriteUtc, Stamp,
        GroupId: "capture-a", GroupMembers: Members);

    // What an older build appends after reconciling/failing/dismissing the line: same fields, no group members.
    private static JournalEntry OlderBuildOutcome(JournalEntry prepared, JournalState state) =>
        prepared with { State = state, GroupId = null, GroupMembers = null, TimestampUtc = Stamp.AddSeconds(30) };

    private static OperationJournal NewJournal(InMemoryFileSystem fileSystem) =>
        new(Paths, fileSystem, new FixedClock(Stamp.AddMinutes(1)));

    [Fact]
    public void Reconcile_GroupCommittedByOlderBuildButSecondMemberNeverMoved_SurfacesAsFailedGroup()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp); // only the first member moved
        fileSystem.AddFile(@"C:\photos\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(GroupPrepared());
        journal.Append(OlderBuildOutcome(GroupPrepared(), JournalState.Committed));

        var surfaced = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, surfaced.State);
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal("group-op", failed.Id);
        Assert.Equal(Members, failed.GroupMembers);
        Assert.Equal("capture-a", failed.GroupId);
        Assert.Empty(journal.ReadPendingOperations());
    }

    [Fact]
    public void Reconcile_GroupCommittedByOlderBuildAndEveryMemberMoved_StaysCommittedWithMembersRestored()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\selected\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(GroupPrepared());
        journal.Append(OlderBuildOutcome(GroupPrepared(), JournalState.Committed));

        Assert.Empty(journal.ReconcilePendingOperations());

        Assert.Empty(journal.ReadFailedOperations());
        var move = journal.ReadCommittedMoves()[^1]; // the repaired line is the newest; the older-build line stays as history
        Assert.Equal(Members, move.GroupMembers);
    }

    [Fact]
    public void Reconcile_GroupFailedByOlderBuild_GetsItsMembersBackSoRecoveryStillSeesTheWholeCapture()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(GroupPrepared());
        journal.Append(OlderBuildOutcome(GroupPrepared(), JournalState.Failed) with { Error = "boom" });

        Assert.Empty(journal.ReconcilePendingOperations());

        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(Members, failed.GroupMembers);
        Assert.Equal("boom", failed.Error);
    }

    [Fact]
    public void Reconcile_DowngradeRepair_IsAppliedOnce()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(GroupPrepared());
        journal.Append(OlderBuildOutcome(GroupPrepared(), JournalState.Committed));
        journal.ReconcilePendingOperations();
        var lengthAfterFirst = fileSystem.ReadAllText(Paths.JournalFile).Length;

        Assert.Empty(journal.ReconcilePendingOperations());

        Assert.Equal(lengthAfterFirst, fileSystem.ReadAllText(Paths.JournalFile).Length);
        Assert.Single(journal.ReadFailedOperations());
    }

    [Fact]
    public void Reconcile_DismissedByOlderBuild_IsLeftAlone()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddFile(@"C:\selected\a.jpg", new string('j', 10), Stamp);
        fileSystem.AddFile(@"C:\photos\a.cr2", new string('r', 100), Stamp);
        var journal = NewJournal(fileSystem);
        journal.Append(GroupPrepared());
        journal.Append(OlderBuildOutcome(GroupPrepared(), JournalState.Failed));
        journal.Append(OlderBuildOutcome(GroupPrepared(), JournalState.Dismissed));
        var before = fileSystem.ReadAllText(Paths.JournalFile);

        Assert.Empty(journal.ReconcilePendingOperations());

        Assert.Equal(before, fileSystem.ReadAllText(Paths.JournalFile));
        Assert.Empty(journal.ReadFailedOperations());
    }

    [Fact]
    public void Reconcile_SingleFileCommittedMove_IsNotTouchedByTheGuard()
    {
        var fileSystem = new InMemoryFileSystem();
        var journal = NewJournal(fileSystem);
        var single = new JournalEntry("single", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\b.jpg", @"C:\selected\b.jpg", 10, Stamp, Stamp);
        journal.Append(single);
        journal.Append(single with { State = JournalState.Committed });
        var before = fileSystem.ReadAllText(Paths.JournalFile);

        Assert.Empty(journal.ReconcilePendingOperations());

        Assert.Equal(before, fileSystem.ReadAllText(Paths.JournalFile));
    }

    [Fact]
    public void GroupLineWrittenByThisBuild_SurvivesCompactionAndTailReaderUnchanged()
    {
        // H2: a Committed group line (members present) must round-trip through the compaction plan and the tail reader.
        using var root = new TempRoot("journal-group-roundtrip");
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.JournalFile)!);
        var group = GroupPrepared();
        using (var stream = new MemoryStream())
        {
            for (var i = 0; i < 9000; i++)
            {
                var filler = new JournalEntry($"f{i:D6}", FileOperationType.Move, JournalState.Prepared,
                    $@"C:\photos\{new string('x', 80)}\f{i}.jpg", $@"C:\sel\{new string('x', 80)}\f{i}.jpg", 1, Stamp, Stamp.AddSeconds(i));
                Write(stream, filler);
                Write(stream, filler with { State = JournalState.Committed });
            }
            // Last, so it sits inside the tail reader's window of the newest committed moves.
            Write(stream, group);
            Write(stream, group with { State = JournalState.Committed, TimestampUtc = Stamp.AddSeconds(1) });
            File.WriteAllBytes(paths.JournalFile, stream.ToArray());
        }
        Assert.True(new FileInfo(paths.JournalFile).Length >= OperationJournal.CompactionThresholdBytes);
        var expected = group with { State = JournalState.Committed, TimestampUtc = Stamp.AddSeconds(1) };

        var reader = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock());
        Assert.Contains(expected, reader.ReadCommittedMoves());

        var result = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock(),
            compactionFiles: new PhysicalJournalCompactionFiles()).TryCompact();
        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);

        Assert.Contains(expected, new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock()).ReadCommittedMoves());
    }

    private static void Write(Stream stream, JournalEntry entry) =>
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\r\n"));

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
