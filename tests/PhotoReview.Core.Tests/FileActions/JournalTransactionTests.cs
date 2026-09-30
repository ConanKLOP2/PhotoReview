using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
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

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
