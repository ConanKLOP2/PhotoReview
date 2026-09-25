using System.IO;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Review r7 item 1: the INV-6 / ADR 0003 startup reconcile + Undo bootstrap is wired again.</summary>
public sealed class JournalStartupRecoveryTests
{
    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public long Timestamp => 0;
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new InvalidOperationException("not used");
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private static readonly DateTime Start = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WriteTime = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(Start);
    private readonly OperationJournal _journal;
    private readonly UndoService _undo;

    public JournalStartupRecoveryTests()
    {
        _journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), _fs, _clock);
        _undo = new UndoService(_journal, _fs, new NoRecycleBin());
    }

    private static JournalEntry Pending(string id, FileOperationType type, string source, string? destination, long size, DateTime prepared) =>
        new(id, type, JournalState.Prepared, source, destination, size, WriteTime, prepared);

    [Fact(DisplayName = "Startup reconciles a pending Move that completed and offers it for Undo")]
    public async Task RunAsync_CompletedPendingMove_CommittedAndUndoable()
    {
        _fs.AddFile(@"C:\photos\sel\a.jpg", "12345", WriteTime);
        _journal.Append(Pending("m1", FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, Start.AddMinutes(-5)));

        var failed = await JournalStartupRecovery.RunAsync(_journal, _undo, _clock, ImmediateUiScheduler.Instance);

        Assert.Empty(failed);
        Assert.Empty(_journal.ReadPendingOperations());
        Assert.Equal("m1", Assert.Single(_journal.ReadCommittedMoves()).Id);
        Assert.True(_undo.CanUndoMove);
        Assert.Equal((@"C:\photos\a.jpg", @"C:\photos\sel\a.jpg"), _undo.MoveHistory.Peek());
    }

    [Fact(DisplayName = "Startup returns the interrupted operations it had to mark Failed")]
    public async Task RunAsync_UnconfirmedPending_ReturnsFailed()
    {
        _fs.AddFile(@"C:\photos\b.jpg", "x", WriteTime); // recycle never happened
        _journal.Append(Pending("r1", FileOperationType.Recycle, @"C:\photos\b.jpg", null, 1, Start.AddMinutes(-1)));

        var failed = await JournalStartupRecovery.RunAsync(_journal, _undo, _clock, ImmediateUiScheduler.Instance);

        Assert.Equal("r1", Assert.Single(failed).Id);
        Assert.Equal("r1", Assert.Single(_journal.ReadFailedOperations()).Id);
    }

    [Fact(DisplayName = "An operation this process prepared after startup began is not judged mid-flight")]
    public async Task RunAsync_EntryPreparedAfterStart_LeftPending()
    {
        _fs.AddFile(@"C:\photos\c.jpg", "x", WriteTime);
        _journal.Append(Pending("live", FileOperationType.Move, @"C:\photos\c.jpg", @"C:\photos\sel\c.jpg", 1, Start));

        var failed = await JournalStartupRecovery.RunAsync(_journal, _undo, _clock, ImmediateUiScheduler.Instance);

        Assert.Empty(failed);
        Assert.Equal("live", Assert.Single(_journal.ReadPendingOperations()).Id);
    }

    [Fact(DisplayName = "Journal work runs off the caller: RunAsync returns while the journal read is still blocked")]
    public async Task RunAsync_DoesNotBlockCaller()
    {
        using var release = new ManualResetEventSlim();
        _journal.Append(Pending("m1", FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, Start.AddMinutes(-5)));
        // Bounded so a synchronous implementation fails the assertion below instead of hanging the run.
        _fs.OpenReadHook = _ => { release.Wait(TimeSpan.FromSeconds(10)); return null; };

        var run = JournalStartupRecovery.RunAsync(_journal, _undo, _clock, ImmediateUiScheduler.Instance);
        var completedBeforeRelease = run.IsCompleted;
        release.Set();
        await run;

        Assert.False(completedBeforeRelease);
    }

    [Fact(DisplayName = "An unreadable journal is logged, not thrown")]
    public async Task RunAsync_JournalReadFails_ReturnsEmpty()
    {
        _journal.Append(Pending("m1", FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, Start.AddMinutes(-5)));
        _fs.OpenReadHook = _ => new IOException("locked");

        var failed = await JournalStartupRecovery.RunAsync(_journal, _undo, _clock, ImmediateUiScheduler.Instance);

        Assert.Empty(failed);
    }

    [Fact(DisplayName = "Journal history is seeded below Moves the user made before seeding finished")]
    public void SeedHistory_KeepsSessionMovesOnTop()
    {
        _fs.AddFile(@"C:\photos\sel\old.jpg", "12345", WriteTime);
        _journal.Append(new JournalEntry("old", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\old.jpg", @"C:\photos\sel\old.jpg", 5, WriteTime, Start.AddDays(-1)));
        var history = _undo.ReadStartupHistory();
        _undo.Register(new FileActionResult(true, FileOperationType.Move, @"C:\photos\new.jpg", @"C:\photos\sel\new.jpg", 3, WriteTime, null));

        _undo.SeedHistory(history);

        Assert.Equal(2, _undo.MoveHistoryCount);
        Assert.Equal((@"C:\photos\new.jpg", @"C:\photos\sel\new.jpg"), _undo.MoveHistory.Peek());
    }

    [Fact(DisplayName = "A Move registered during startup is not added twice from the journal")]
    public void SeedHistory_SkipsMoveAlreadyRegisteredInSession()
    {
        _fs.AddFile(@"C:\photos\sel\a.jpg", "12345", WriteTime);
        _journal.Append(new JournalEntry("a", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, WriteTime, Start));
        _undo.Register(new FileActionResult(true, FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, WriteTime, null));

        _undo.SeedHistory(_undo.ReadStartupHistory());

        Assert.Equal(1, _undo.MoveHistoryCount);
    }
}
