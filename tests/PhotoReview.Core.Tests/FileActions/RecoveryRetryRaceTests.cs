using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// P02: two PhotoReview processes (InstanceMode.PerFolder) share one operations.jsonl and can retry the same Failed
/// Recovery entry at nearly the same time. Each "process" here is its own <see cref="OperationJournal"/> instance (own
/// lock, own process-local live registry, so the Q-R27 marker does not see the other one) over one shared in-memory
/// file system; the other process's retry is interleaved deterministically from a file-system hook.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class RecoveryRetryRaceTests
{
    private const string JournalPath = @"C:\data\operations.jsonl";
    private const string Source = @"C:\photos\a.jpg";
    private const string Destination = @"D:\sorted\a.jpg";

    private sealed class SteppingClock : IClock
    {
        private DateTime _now = new(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);

        // Every read is a distinct instant, as with real clocks in two processes.
        public DateTime UtcNow => _now = _now.AddMilliseconds(1);
    }

    private sealed class Paths : IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => JournalPath;
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly SteppingClock _clock = new();
    private readonly OperationJournal _journalA;
    private readonly OperationJournal _journalB;
    private readonly RecoveryRetryService _processA;
    private readonly RecoveryRetryService _processB;

    public RecoveryRetryRaceTests()
    {
        _journalA = new OperationJournal(new Paths(), _fs, _clock);
        _journalB = new OperationJournal(new Paths(), _fs, _clock);
        _processA = new RecoveryRetryService(_journalA, _fs, _clock);
        _processB = new RecoveryRetryService(_journalB, _fs, _clock);
    }

    /// <summary>A failed Move of <see cref="Source"/> in the journal, as both Recovery windows snapshot it.</summary>
    private JournalEntry SeedFailedMove(FileOperationType type = FileOperationType.Move)
    {
        _fs.WriteAllTextAtomic(Source, "photo-bytes");
        var stat = _fs.GetFileStat(Source)!;
        _journalA.Append(new JournalEntry("op-race", type, JournalState.Failed, Source, Destination,
            stat.Length, stat.LastWriteUtc, _clock.UtcNow, "The process cannot access the file"));
        return Assert.Single(_journalA.ReadFailedOperations());
    }

    private List<JournalEntry> History()
    {
        var lines = _fs.ReadAllText(JournalPath).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Select(line => System.Text.Json.JsonSerializer.Deserialize<JournalEntry>(line)!).ToList();
    }

    [Fact(DisplayName = "P02: loser's Prepared lands after the winner's Committed; its failure is not journaled over the winner's Committed")]
    public async Task ConcurrentRetry_LoserPreparedAfterWinnerCommitted_DoesNotOverwriteCommittedWithFailed()
    {
        var snapshot = SeedFailedMove();

        // B (the loser) passes the latest-entry check; right as it opens the journal to append its Prepared, A (the
        // winner) runs its whole retry: check, Prepared, Move, Committed. B's Prepared then lands after A's Committed
        // and B's Move throws because A already moved the file.
        RecoveryRetryResult? winner = null;
        _fs.OpenAppendHook = _ =>
        {
            if (winner is not null) return null;
            winner = new RecoveryRetryResult(false, "running", null); // re-entrancy guard for A's own appends
            winner = _processA.RetryMoveOrCopyAsync(snapshot).GetAwaiter().GetResult();
            return null;
        };

        var loser = await _processB.RetryMoveOrCopyAsync(snapshot);
        _fs.OpenAppendHook = null;

        Assert.NotNull(winner);
        Assert.True(winner.Succeeded);
        Assert.Equal(JournalState.Committed, winner.Entry!.State);
        Assert.False(loser.Succeeded);
        Assert.True(loser.Superseded);
        Assert.Equal(Tr.CoreRecoveryAlreadyHandled, loser.Message);
        Assert.False(_fs.FileExists(Source));
        Assert.True(_fs.FileExists(Destination));

        // The bug: a Failed record after A's Committed made the completed Move look permanently Failed.
        Assert.DoesNotContain(_journalA.ReadFailedOperations(), e => e.Id == "op-race");
        var history = History();
        Assert.Equal(
            [JournalState.Failed, JournalState.Prepared, JournalState.Committed, JournalState.Prepared],
            history.Select(e => e.State).ToArray());

        // B's dangling Prepared is resolved by the next startup reconcile from the file system: the Move did happen.
        var reconciled = Assert.Single(_journalB.ReconcilePendingOperations());
        Assert.Equal(JournalState.Committed, reconciled.State);
        Assert.Empty(_journalA.ReadPendingAndFailedOperations());
    }

    [Fact(DisplayName = "P02: a retry that starts after the other process's Prepared is refused, the first retry commits")]
    public async Task ConcurrentRetry_SecondStartsAfterFirstPrepared_IsRefusedAndFirstCommits()
    {
        var snapshot = SeedFailedMove();

        // B has appended its Prepared; just before its Move, A retries the same snapshot.
        RecoveryRetryResult? late = null;
        _fs.MoveHook = (_, _) =>
        {
            if (late is not null) return null;
            late = new RecoveryRetryResult(false, "running", null);
            late = _processA.RetryMoveOrCopyAsync(snapshot).GetAwaiter().GetResult();
            return null;
        };

        var first = await _processB.RetryMoveOrCopyAsync(snapshot);
        _fs.MoveHook = null;

        Assert.True(first.Succeeded);
        Assert.False(first.Superseded);
        Assert.NotNull(late);
        Assert.False(late.Succeeded);
        Assert.True(late.Superseded);
        Assert.Null(late.Entry);
        Assert.False(_fs.FileExists(Source));
        Assert.True(_fs.FileExists(Destination));
        Assert.Equal(
            [JournalState.Failed, JournalState.Prepared, JournalState.Committed],
            History().Select(e => e.State).ToArray());
        Assert.Empty(_journalA.ReadPendingAndFailedOperations());
    }

    [Theory(DisplayName = "P02: retrying a stale snapshot (another window already appended a newer record) changes nothing")]
    [InlineData(JournalState.Committed)]
    [InlineData(JournalState.Failed)]
    [InlineData(JournalState.Dismissed)]
    public async Task Retry_StaleSnapshot_IsRefusedWithoutMutationOrJournalAppend(JournalState newer)
    {
        var snapshot = SeedFailedMove(FileOperationType.Copy);
        // Another process resolved the same Id after this window took its snapshot (a Failed here stands for another
        // process's retry that failed again: its record differs from the snapshot).
        _journalB.Append(snapshot with { State = newer, TimestampUtc = _clock.UtcNow, Error = "other window" });
        var before = History().Count;

        var result = await _processA.RetryMoveOrCopyAsync(snapshot);

        Assert.False(result.Succeeded);
        Assert.True(result.Superseded);
        Assert.Equal(Tr.CoreRecoveryAlreadyHandled, result.Message);
        Assert.Null(result.Entry);
        Assert.False(_fs.FileExists(Destination));
        Assert.True(_fs.FileExists(Source));
        Assert.Equal(before, History().Count);
    }

    [Fact(DisplayName = "P02: retrying an Id whose live marker is held elsewhere (Q-R27) changes nothing")]
    public async Task Retry_IdLiveInAnotherProcess_IsRefusedWithoutMutationOrJournalAppend()
    {
        var snapshot = SeedFailedMove();
        var before = History().Count;
        using (_journalA.LiveOperations.Begin(snapshot.Id)) // another process is executing this Id right now
        {
            var result = await _processA.RetryMoveOrCopyAsync(snapshot);

            Assert.True(result.Superseded);
            Assert.False(result.Succeeded);
            Assert.True(_fs.FileExists(Source));
            Assert.False(_fs.FileExists(Destination));
            Assert.Equal(before, History().Count);
        }

        // Marker gone: the same snapshot is still current, so the retry now runs.
        var retried = await _processA.RetryMoveOrCopyAsync(snapshot);
        Assert.True(retried.Succeeded);
        Assert.False(retried.Superseded);
    }

    [Fact(DisplayName = "P02: an ordinary failed retry of a current snapshot still journals Failed")]
    public async Task Retry_CurrentSnapshot_GenuineFailure_StillJournalsFailed()
    {
        var snapshot = SeedFailedMove();
        _fs.MoveHook = (_, _) => new IOException("disk unplugged");

        var result = await _processA.RetryMoveOrCopyAsync(snapshot);

        Assert.False(result.Succeeded);
        Assert.False(result.Superseded);
        var failed = Assert.Single(_journalA.ReadFailedOperations());
        Assert.Equal(JournalState.Failed, failed.State);
        Assert.NotEqual(snapshot, failed);
        Assert.Equal(
            [JournalState.Failed, JournalState.Prepared, JournalState.Failed],
            History().Select(e => e.State).ToArray());
    }
}
