using System.Globalization;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// RV-T01 / RV-T05 gap tests for <see cref="OperationJournal"/>: the reverse (tail) committed-Move reader on lines longer than
/// its 256 KiB window and on torn tails, and <see cref="OperationJournal.Dismiss"/> against a live-marked Prepared line.
/// </summary>
public sealed class OperationJournalGapTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private const long Window = 256 * 1024;

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    private static JournalEntry CommittedMove(int i, string? error = null) => new(
        "m" + i.ToString("D6", CultureInfo.InvariantCulture), FileOperationType.Move, JournalState.Committed,
        $@"C:\photos\s{i.ToString("D6", CultureInfo.InvariantCulture)}.jpg", $@"C:\photos\sel\s{i.ToString("D6", CultureInfo.InvariantCulture)}.jpg", 10 + i, Stamp, Stamp, error);

    private static string Line(JournalEntry entry) => JsonSerializer.Serialize(entry) + "\n";

    /// <summary>~1.3 MiB of committed Moves (so the reverse reader runs), ids m000000..m(count-1) in order.</summary>
    private static string SmallMoveLines(int count)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++) text.Append(Line(CommittedMove(i, new string('p', 200))));
        return text.ToString();
    }

    private static OperationJournal Journal(InMemoryFileSystem fs, string content)
    {
        fs.AddFile(Paths.JournalFile, content);
        Assert.True(fs.GetFileStat(Paths.JournalFile)!.Length >= 1024 * 1024, "fixture must exceed the full-scan threshold");
        return new OperationJournal(Paths, fs, new Clock());
    }

    [Fact]
    public void ReadCommittedMoves_LastLineLongerThanTheWindow_WidensAndReturnsNewest200InOrder()
    {
        const int small = 5000;
        var giant = CommittedMove(small, new string('g', (int)Window + 50_000)); // one committed Move line longer than a window
        var journal = Journal(new InMemoryFileSystem(), SmallMoveLines(small) + Line(giant));

        var ids = journal.ReadCommittedMoves().Select(e => e.Id).ToArray();

        Assert.Equal(200, ids.Length);
        Assert.Equal(Enumerable.Range(small - 199, 199).Select(i => "m" + i.ToString("D6", CultureInfo.InvariantCulture)).Append("m" + small.ToString("D6", CultureInfo.InvariantCulture)), ids);
    }

    [Fact]
    public void ReadCommittedMoves_TornGiantLastLineWithoutNewline_IsSkippedAndEarlierMovesKept()
    {
        const int small = 5000;
        var torn = "{\"Id\":\"torn\",\"Type\":\"Move\",\"State\":\"Committed\",\"Source\":\"" + new string('t', (int)Window + 50_000); // no closing, no newline
        var journal = Journal(new InMemoryFileSystem(), SmallMoveLines(small) + torn);

        var ids = journal.ReadCommittedMoves().Select(e => e.Id).ToArray();

        Assert.Equal(Enumerable.Range(small - 200, 200).Select(i => "m" + i.ToString("D6", CultureInfo.InvariantCulture)), ids);
    }

    [Fact]
    public void ReadCommittedMoves_TornShortLastLine_IsIgnoredAndEarlierLinesKept()
    {
        const int small = 5000;
        var tornTail = Line(CommittedMove(small))[..40]; // half a line, no newline: a writer died mid-append
        var journal = Journal(new InMemoryFileSystem(), SmallMoveLines(small) + tornTail);

        var ids = journal.ReadCommittedMoves().Select(e => e.Id).ToArray();

        Assert.Equal(200, ids.Length);
        Assert.Equal("m" + (small - 1).ToString("D6", CultureInfo.InvariantCulture), ids[^1]);
        Assert.DoesNotContain("m" + small.ToString("D6", CultureInfo.InvariantCulture), ids);
    }

    // RV-T05. Documented behaviour (OperationJournal.Dismiss remarks): Dismiss only compares the snapshot with the CURRENT latest
    // line (R09); it never consults the Q-R27 live marker. A user clicking Dismiss on a Prepared line whose owner is still
    // running dismisses it; the owner's later outcome line is appended after the Dismissed line and wins, so nothing is hidden.
    [Fact]
    public void Dismiss_PreparedSnapshotMarkedLiveByAnotherProcess_IsDismissedAndTheOwnersLaterOutcomeStillWins()
    {
        var fs = new InMemoryFileSystem();
        var registry = new InProcessLiveOperationRegistry();
        var recovery = new OperationJournal(Paths, fs, new Clock(), liveOperations: registry);
        var owner = new OperationJournal(Paths, fs, new Clock(), liveOperations: registry);
        var prepared = new JournalEntry("op-live", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 10, Stamp, Stamp);
        using var marker = registry.Begin(prepared.Id);
        owner.Append(prepared);
        Assert.True(registry.IsLive(prepared.Id));

        var outcome = recovery.Dismiss([prepared]);

        Assert.Empty(outcome.Skipped);
        Assert.Equal(JournalState.Dismissed, Assert.Single(outcome.Dismissed).State);
        Assert.Empty(recovery.ReadPendingOperations());

        owner.Append(prepared with { State = JournalState.Committed });
        Assert.Contains(recovery.ReadCommittedMoves(), e => e.Id == "op-live");
        Assert.Empty(recovery.ReadFailedOperations());
        Assert.Empty(recovery.ReadPendingOperations());
    }
}
