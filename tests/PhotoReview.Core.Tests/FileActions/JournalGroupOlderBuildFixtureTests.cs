using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// ADR 0003 downgrade guard against AUTHENTIC older-build journal lines. The fixtures in <c>Fixtures/journal-older-build</c> were
/// produced by compiling origin/master's <c>PhotoReview.Core</c> (which has no GroupId/GroupMembers) and running its own
/// <c>OperationJournal.ReconcilePendingOperations</c> / <c>Dismiss</c> over a group Prepared line written by the new build's
/// serializer (<c>prepared-group-new-build.jsonl</c>), against real files laid out as described per scenario below. Nothing in them
/// is hand-built, so these tests pin what an older build REALLY appends, which <see cref="JournalGroupDowngradeTests"/> only assumes.
/// Fixture paths are under <c>C:\prfixture</c>; the tests replay them on an in-memory file system.
/// </summary>
public sealed class JournalGroupOlderBuildFixtureTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JournalGroupMember[] Members =
    [
        new(@"C:\prfixture\photos\a.jpg", @"C:\prfixture\selected\a.jpg", 10, Stamp),
        new(@"C:\prfixture\photos\a.cr2", @"C:\prfixture\selected\a.cr2", 100, Stamp),
    ];

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journal-older-build", name + ".jsonl"));

    private static (OperationJournal Journal, InMemoryFileSystem Fs) Load(string fixture, bool firstMemberMoved, bool secondMemberMoved)
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(firstMemberMoved ? @"C:\prfixture\selected\a.jpg" : @"C:\prfixture\photos\a.jpg", new string('x', 10), Stamp);
        fs.AddFile(secondMemberMoved ? @"C:\prfixture\selected\a.cr2" : @"C:\prfixture\photos\a.cr2", new string('x', 100), Stamp);
        fs.AddFile(Paths.JournalFile, Fixture(fixture), Stamp);
        return (new OperationJournal(Paths, fs, new FixedClock(Stamp.AddMinutes(1))), fs);
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void Fixtures_PreparedLineIsWhatTheNewSerializerWrites_AndOlderBuildLinesCarryNoGroupFields()
    {
        var fs = new InMemoryFileSystem();
        var journal = new OperationJournal(Paths, fs, new FixedClock(Stamp));
        journal.Append(new JournalEntry("group-op", FileOperationType.Move, JournalState.Prepared,
            Members[0].Source, Members[0].Destination, Members[0].Size, Members[0].LastWriteUtc, Stamp,
            GroupId: "capture-a", GroupMembers: Members));
        Assert.Equal(Lines(Fixture("prepared-group-new-build")), Lines(fs.ReadAllText(Paths.JournalFile)));

        foreach (var name in new[] { "a-first-member-only-committed", "b-fully-moved-committed", "d-nothing-moved-failed", "c-nothing-moved-failed-then-dismissed" })
        {
            var lines = Lines(Fixture(name));
            using var prepared = JsonDocument.Parse(lines[0]);
            Assert.True(prepared.RootElement.TryGetProperty("GroupMembers", out _));
            foreach (var line in lines.Skip(1))
            {
                using var doc = JsonDocument.Parse(line);
                Assert.False(doc.RootElement.TryGetProperty("GroupMembers", out _), name);
                Assert.False(doc.RootElement.TryGetProperty("GroupId", out _), name);
            }
        }
    }

    [Fact]
    public void OlderBuildCommittedAfterOnlyTheFirstMemberMoved_IsSurfacedAsFailedGroup()
    {
        // The older build saw "first member's source gone, destination there" and appended Committed.
        var (journal, _) = Load("a-first-member-only-committed", firstMemberMoved: true, secondMemberMoved: false);

        var surfaced = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, surfaced.State);
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal("group-op", failed.Id);
        Assert.Equal("capture-a", failed.GroupId);
        Assert.Equal(Members, failed.GroupMembers);
        Assert.Equal(OperationJournal.SettledByOlderBuildText, failed.Error);
        Assert.Null(failed.ErrorCode);
        Assert.Empty(journal.ReadPendingOperations());
        Assert.Single(journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public void OlderBuildCommittedAfterEveryMemberMoved_StaysCommittedWithMembersRestored()
    {
        var (journal, fs) = Load("b-fully-moved-committed", firstMemberMoved: true, secondMemberMoved: true);

        Assert.Empty(journal.ReconcilePendingOperations());

        Assert.Empty(journal.ReadPendingAndFailedOperations());
        Assert.Equal(Members, journal.ReadCommittedMoves()[^1].GroupMembers);
        var lengthAfterFirst = fs.ReadAllText(Paths.JournalFile).Length;
        Assert.Empty(journal.ReconcilePendingOperations()); // repaired once, then stable
        Assert.Equal(lengthAfterFirst, fs.ReadAllText(Paths.JournalFile).Length);
    }

    [Fact]
    public void OlderBuildFailedLine_GetsItsMembersBackAndKeepsItsOwnErrorCode()
    {
        var (journal, _) = Load("d-nothing-moved-failed", firstMemberMoved: false, secondMemberMoved: false);

        Assert.Empty(journal.ReconcilePendingOperations());

        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(Members, failed.GroupMembers);
        Assert.Equal("capture-a", failed.GroupId);
        Assert.Equal(JournalErrors.PendingUnconfirmed, failed.ErrorCode);
        Assert.Equal(Stamp.AddSeconds(30), failed.TimestampUtc); // the older build's verdict time is preserved
        Assert.Single(journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public void OlderBuildDismissedLine_IsLeftAlone()
    {
        var (journal, fs) = Load("c-nothing-moved-failed-then-dismissed", firstMemberMoved: false, secondMemberMoved: false);
        var before = fs.ReadAllText(Paths.JournalFile);

        Assert.Empty(journal.ReconcilePendingOperations());

        Assert.Equal(before, fs.ReadAllText(Paths.JournalFile));
        Assert.Empty(journal.ReadPendingAndFailedOperations());
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
