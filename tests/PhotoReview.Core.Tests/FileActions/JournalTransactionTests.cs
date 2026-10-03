using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class JournalTransactionTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fail_RetrySupersededByAnotherWriter_ReturnsNullAndAppendsNothing()
    {
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), new FixedClock(Stamp));
        var failed = new JournalEntry("op", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp, Stamp, Error: "x", ErrorCode: JournalErrors.PendingUnconfirmed);
        journal.Append(failed);
        using var tx = new JournalTransaction(journal, new FixedClock(Stamp), failed with { State = JournalState.Prepared }, retryOf: failed);
        Assert.True(tx.TryBegin());
        // Another process resolves the Id after this retry's Prepared.
        journal.Append(failed with { State = JournalState.Committed, Error = null, ErrorCode = null });

        var result = tx.Fail(new IOException("boom"), out var journalError);

        Assert.Null(result);
        Assert.Null(journalError);
        Assert.True(tx.Superseded);
        Assert.Empty(journal.ReadFailedOperations());
    }

    [Fact]
    public void Fail_RetryStillCurrent_ReturnsTheFailedRecord()
    {
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), new FixedClock(Stamp));
        var failed = new JournalEntry("op", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp, Stamp, Error: "x", ErrorCode: JournalErrors.PendingUnconfirmed);
        journal.Append(failed);
        using var tx = new JournalTransaction(journal, new FixedClock(Stamp), failed with { State = JournalState.Prepared }, retryOf: failed);
        Assert.True(tx.TryBegin());

        var result = tx.Fail(new IOException("boom"), out _);

        Assert.NotNull(result);
        Assert.Equal(JournalState.Failed, result.State);
        Assert.False(tx.Superseded);
        Assert.Equal("op", Assert.Single(journal.ReadFailedOperations()).Id);
    }

    [Fact]
    public void Fail_OutcomeAppendThrows_ReturnsNullAndReportsTheJournalError()
    {
        var fs = new InMemoryFileSystem();
        var journal = new OperationJournal(Paths, fs, new FixedClock(Stamp));
        var prepared = new JournalEntry("op", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp, Stamp);
        using var tx = new JournalTransaction(journal, new FixedClock(Stamp), prepared);
        tx.Begin();
        fs.OpenAppendHook = _ => new IOException("disk full"); // only the outcome line fails

        var result = tx.Fail(new IOException("boom"), out var journalError);

        Assert.Null(result); // no record was written: a caller must not treat it as a journaled Failed line
        Assert.NotNull(journalError);
        fs.OpenAppendHook = null;
        Assert.Equal("op", Assert.Single(journal.ReadPendingOperations()).Id); // still only Prepared: reconcile will judge it
    }

    [Fact]
    public void DismissRolledBack_OutcomeAppendThrows_ReturnsNullAndReportsTheJournalError()
    {
        var fs = new InMemoryFileSystem();
        var journal = new OperationJournal(Paths, fs, new FixedClock(Stamp));
        var prepared = new JournalEntry("op", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp, Stamp);
        using var tx = new JournalTransaction(journal, new FixedClock(Stamp), prepared);
        tx.Begin();
        fs.OpenAppendHook = _ => new IOException("disk full");

        var result = tx.DismissRolledBack(out var journalError);

        Assert.Null(result);
        Assert.NotNull(journalError);
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    [Fact]
    public void ReadLatestEntry_StaleReconcileFailedAfterCommittedRetry_ReturnsTheCommittedLine()
    {
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), new FixedClock(Stamp));
        var failed = new JournalEntry("op", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp, Stamp, Error: "x", ErrorCode: JournalErrors.PendingUnconfirmed);
        var committed = failed with { State = JournalState.Committed, Error = null, ErrorCode = null };
        journal.Append(failed);
        journal.Append(committed);
        journal.Append(failed); // another process's reconcile verdict from a stale snapshot

        Assert.Equal(committed, journal.ReadLatestEntry("op"));
    }

    [Fact]
    public void ReadLatestEntry_NonReconcileFailedAfterCommitted_StillWins()
    {
        var journal = new OperationJournal(Paths, new InMemoryFileSystem(), new FixedClock(Stamp));
        var committed = new JournalEntry("op", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp, Stamp);
        var failed = committed with { State = JournalState.Failed, Error = "io", ErrorCode = null };
        journal.Append(committed);
        journal.Append(failed);

        Assert.Equal(failed, journal.ReadLatestEntry("op"));
    }
}
