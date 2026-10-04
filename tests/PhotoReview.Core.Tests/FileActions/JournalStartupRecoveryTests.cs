using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Review r7 item 1: the INV-6 / ADR 0003 startup reconcile is wired again. P03 (2026-09-27): the Undo
/// bootstrap from journal history it also used to do is gone - Undo is limited to the current session.</summary>
public sealed class JournalStartupRecoveryTests
{
    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
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

    [Fact(DisplayName = "Startup reconciles a pending Move that completed, but P03 no longer offers it for Undo")]
    public async Task RunAsync_CompletedPendingMove_CommittedButNotUndoable()
    {
        _fs.AddFile(@"C:\photos\sel\a.jpg", "12345", WriteTime);
        _journal.Append(Pending("m1", FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, Start.AddMinutes(-5)));

        var failed = await JournalStartupRecovery.RunAsync(_journal, _clock);

        Assert.Empty(failed);
        Assert.Empty(_journal.ReadPendingOperations());
        Assert.Equal("m1", Assert.Single(_journal.ReadCommittedMoves()).Id);
        // P03 (2026-09-27): Undo is limited to the current session; RunAsync does not touch UndoService at all,
        // so a freshly constructed one has nothing to undo even though the journal now has a Committed Move.
        Assert.False(_undo.MoveHistory.Count > 0);
    }

    [Fact(DisplayName = "P03: a journal full of committed Moves from a previous run does not seed the Undo stack, but in-session Moves are still undoable")]
    public async Task RunAsync_JournalHasCommittedMoves_UndoStackEmptyUntilSessionRegistersAMove()
    {
        _fs.AddFile(@"C:\photos\sel\old.jpg", "12345", WriteTime);
        // Simulates a previous session/run: already Committed before this process's startup began.
        _journal.Append(new JournalEntry("old", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\old.jpg", @"C:\photos\sel\old.jpg", 5, WriteTime, Start.AddDays(-1)));

        var failed = await JournalStartupRecovery.RunAsync(_journal, _clock);

        Assert.Empty(failed);
        // Mutation-check: re-adding a call that seeds `_undo` from journal history here would make this fail.
        Assert.False(_undo.MoveHistory.Count > 0);
        Assert.Empty(_undo.MoveHistory);

        // In-session undo still works: a Move registered after startup is undoable regardless of journal history.
        _fs.AddFile(@"C:\photos\sel\new.jpg", "abc", WriteTime);
        _undo.Register(new FileActionResult(true, FileOperationType.Move, @"C:\photos\new.jpg", @"C:\photos\sel\new.jpg", 3, WriteTime, null));

        Assert.True(_undo.MoveHistory.Count > 0);
        Assert.Equal((@"C:\photos\new.jpg", @"C:\photos\sel\new.jpg"), _undo.MoveHistory.Peek());
    }

    [Fact(DisplayName = "Startup returns the interrupted operations it had to mark Failed")]
    public async Task RunAsync_UnconfirmedPending_ReturnsFailed()
    {
        _fs.AddFile(@"C:\photos\b.jpg", "x", WriteTime); // recycle never happened
        _journal.Append(Pending("r1", FileOperationType.Recycle, @"C:\photos\b.jpg", null, 1, Start.AddMinutes(-1)));

        var failed = await JournalStartupRecovery.RunAsync(_journal, _clock);

        Assert.Equal("r1", Assert.Single(failed).Id);
        Assert.Equal("r1", Assert.Single(_journal.ReadFailedOperations()).Id);
    }

    [Fact(DisplayName = "An operation this process prepared after startup began is not judged mid-flight")]
    public async Task RunAsync_EntryPreparedAfterStart_LeftPending()
    {
        _fs.AddFile(@"C:\photos\c.jpg", "x", WriteTime);
        _journal.Append(Pending("live", FileOperationType.Move, @"C:\photos\c.jpg", @"C:\photos\sel\c.jpg", 1, Start));

        var failed = await JournalStartupRecovery.RunAsync(_journal, _clock);

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

        var run = JournalStartupRecovery.RunAsync(_journal, _clock);
        var completedBeforeRelease = run.IsCompleted;
        release.Set();
        await run;

        Assert.False(completedBeforeRelease);
    }

    [Fact(DisplayName = "Dismissing a Failed entry drops its error code as well as its text")]
    public void Dismiss_ClearsErrorCode()
    {
        var failed = Pending("f1", FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, Start) with
        {
            State = PhotoReview.Core.Model.JournalState.Failed,
            Error = "boom",
            ErrorCode = PhotoReview.Core.FileActions.JournalErrors.DestinationOutsideSource,
        };
        _journal.Append(failed);

        var dismissed = Assert.Single(_journal.Dismiss([failed]).Dismissed);

        Assert.Null(dismissed.ErrorCode);
        var lines = _fs.ReadAllText(new AppPaths(@"C:\Users\test\AppData\Local").JournalFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("ErrorCode", lines[^1], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An unreadable journal is logged, not thrown")]
    public async Task RunAsync_JournalReadFails_ReturnsEmpty()
    {
        _journal.Append(Pending("m1", FileOperationType.Move, @"C:\photos\a.jpg", @"C:\photos\sel\a.jpg", 5, Start.AddMinutes(-5)));
        _fs.OpenReadHook = _ => new IOException("locked");

        var failed = await JournalStartupRecovery.RunAsync(_journal, _clock);

        Assert.Empty(failed);
    }
}
