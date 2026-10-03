using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gaps of the small journal helpers: <see cref="JournalCompactionPlan"/> line splitting, the coded exception,
/// the line parser's malformed-JSON handling, the live-marker release of <see cref="JournalTransaction"/> and the startup
/// recovery summary log.
/// </summary>
public sealed class JournalSmallGapsTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private static JournalEntry Entry(string id, JournalState state, FileOperationType type = FileOperationType.Copy) =>
        new(id, type, state, $@"C:\photos\{id}.jpg", $@"C:\sel\{id}.jpg", 5, Stamp, Stamp);

    private static string Json(JournalEntry entry) => JsonSerializer.Serialize(entry);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // ---------------------------------------------------------------------------------------------------------
    // JournalCompactionPlan.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Build_LinesTerminatedByBareNewlines_DropsTheSupersededOne()
    {
        var snapshot = Bytes(Json(Entry("a", JournalState.Prepared)) + "\n" + Json(Entry("a", JournalState.Committed)) + "\n");

        var result = JournalCompactionPlan.Build(snapshot);

        Assert.Equal(1, result.DroppedLines);
        Assert.Equal(Bytes(Json(Entry("a", JournalState.Committed)) + "\n"), result.Kept);
    }

    [Fact]
    public void Build_BlankLineFollowedByEntries_DropsTheBlankLineAndStillJudgesTheEntries()
    {
        var snapshot = Bytes("\n" + Json(Entry("a", JournalState.Prepared)) + "\n" + Json(Entry("a", JournalState.Committed)) + "\n");

        var result = JournalCompactionPlan.Build(snapshot);

        Assert.Equal(2, result.DroppedLines); // the blank line and the superseded Prepared
        Assert.Equal(1, result.KeptLines);
        Assert.Equal(Bytes(Json(Entry("a", JournalState.Committed)) + "\n"), result.Kept);
    }

    [Fact]
    public void Build_LastLineWithoutATerminator_IsParsedAndKeptVerbatim()
    {
        var committed = Json(Entry("a", JournalState.Committed));
        var snapshot = Bytes(Json(Entry("a", JournalState.Prepared)) + "\n" + committed);

        var result = JournalCompactionPlan.Build(snapshot);

        Assert.Equal(1, result.DroppedLines); // the Prepared is superseded by the (unterminated) Committed
        Assert.Equal(Bytes(committed), result.Kept);
    }

    [Fact]
    public void Build_FirstLineIsACommittedMoveSupersededByALaterOne_StaysInsideTheCommittedMoveWindow()
    {
        var move = Entry("a", JournalState.Committed, FileOperationType.Move);
        var snapshot = Bytes(Json(move) + "\n" + Json(move with { TimestampUtc = Stamp.AddSeconds(1) }) + "\n");

        var result = JournalCompactionPlan.Build(snapshot);

        Assert.Equal(0, result.DroppedLines); // both are among the last 200 committed Moves ReadCommittedMoves returns
        Assert.Equal(snapshot, result.Kept);
    }

    // ---------------------------------------------------------------------------------------------------------
    // JournalCodedException / JournalLineParser.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void JournalCodedException_Parameterless_HasAnEmptyCode()
    {
        Assert.Equal(string.Empty, new JournalCodedException().Code);
    }

    [Fact]
    public void JournalCodedException_MessageAndInner_HasAnEmptyCodeAndKeepsBoth()
    {
        var inner = new InvalidOperationException("inner");

        var ex = new JournalCodedException("outer", inner);

        Assert.Equal(string.Empty, ex.Code);
        Assert.Equal("outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Theory]
    [InlineData("{\"Id\":\"a\",\"Type\":")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void TryParse_MalformedJson_ReturnsNullForBothOverloads(string line)
    {
        Assert.Null(JournalLineParser.TryParse(line));
        Assert.Null(JournalLineParser.TryParse(Encoding.UTF8.GetBytes(line)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // JournalTransaction releases the live marker as soon as the outcome line is written.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Commit_ReleasesTheLiveMarkerBeforeDispose()
    {
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), new FixedClock(Stamp));
        using var tx = new JournalTransaction(journal, new FixedClock(Stamp), Entry("op", JournalState.Prepared, FileOperationType.Move));
        tx.Begin();
        Assert.True(journal.LiveOperations.IsLive("op"));

        tx.Commit(out _);

        Assert.False(journal.LiveOperations.IsLive("op"));
    }

    [Fact]
    public void Fail_ReleasesTheLiveMarkerBeforeDispose()
    {
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), new FixedClock(Stamp));
        using var tx = new JournalTransaction(journal, new FixedClock(Stamp), Entry("op", JournalState.Prepared, FileOperationType.Move));
        tx.Begin();
        Assert.True(journal.LiveOperations.IsLive("op"));

        tx.Fail(new IOException("boom"), out _);

        Assert.False(journal.LiveOperations.IsLive("op"));
    }

    [Fact]
    public void DismissRolledBack_ReleasesTheLiveMarkerBeforeDispose()
    {
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), new FixedClock(Stamp));
        using var tx = new JournalTransaction(journal, new FixedClock(Stamp), Entry("op", JournalState.Prepared, FileOperationType.Move));
        tx.Begin();

        tx.DismissRolledBack(out _);

        Assert.False(journal.LiveOperations.IsLive("op"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // JournalStartupRecovery: the summary warning only when something was reconciled.
    // ---------------------------------------------------------------------------------------------------------

    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }

    [Fact]
    public async Task RunAsync_NothingToReconcile_LogsNoSummaryWarning()
    {
        var clock = new FixedClock(Stamp);
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), clock);
        var log = new RecordingLog();

        var failed = await JournalStartupRecovery.RunAsync(journal, clock, log);

        Assert.Empty(failed);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task RunAsync_OnePendingOperationReconciledAsFailed_LogsTheCounts()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\a.jpg", "12345", Stamp.AddDays(-1)); // source still there, destination missing
        var clock = new FixedClock(Stamp);
        var journal = new OperationJournal(Paths, fs, clock);
        journal.Append(new JournalEntry("a", FileOperationType.Move, JournalState.Prepared, @"C:\photos\a.jpg", @"C:\sel\a.jpg", 5,
            Stamp.AddDays(-1), Stamp.AddMinutes(-5)));
        var log = new RecordingLog();

        var failed = await JournalStartupRecovery.RunAsync(journal, clock, log);

        Assert.Single(failed);
        Assert.Equal("Startup journal reconcile: 1 pending operation(s), 1 failed.", Assert.Single(log.Warnings));
    }

    [Fact]
    public async Task RunAsync_OnePendingOperationReconciledAsCommitted_LogsZeroFailed()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\sel\a.jpg", "12345", Stamp.AddDays(-1)); // already moved
        var clock = new FixedClock(Stamp);
        var journal = new OperationJournal(Paths, fs, clock);
        journal.Append(new JournalEntry("a", FileOperationType.Move, JournalState.Prepared, @"C:\photos\a.jpg", @"C:\sel\a.jpg", 5,
            Stamp.AddDays(-1), Stamp.AddMinutes(-5)));
        var log = new RecordingLog();

        var failed = await JournalStartupRecovery.RunAsync(journal, clock, log);

        Assert.Empty(failed);
        Assert.Equal("Startup journal reconcile: 1 pending operation(s), 0 failed.", Assert.Single(log.Warnings));
    }
}