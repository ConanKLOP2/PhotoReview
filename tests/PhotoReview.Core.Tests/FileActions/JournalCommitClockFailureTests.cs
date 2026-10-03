using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// A clock that fails while the Committed line is stamped -- after the file mutation already completed -- is a journal
/// failure (JournalPersisted=false, Succeeded=true, no Failed line), never an exception that makes the caller journal a
/// Failed outcome for an operation that succeeded. Fakes only; the real Recycle Bin is never touched.
/// </summary>
public sealed class JournalCommitClockFailureTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    /// <summary>Reads normally until armed, then throws on every read (armed once the Prepared line is written).</summary>
    private sealed class ArmableThrowingClock : IClock
    {
        public bool Armed { get; set; }

        public DateTime UtcNow => Armed ? throw new InvalidOperationException("clock failure") : Stamp;
    }

    private sealed class Bin(InMemoryFileSystem disk) : IRecycleBin
    {
        public bool CanRecycle(string path) => true;
        public void SendToRecycleBin(string path) => disk.Delete(path);
        public void DeletePermanently(string path) => disk.Delete(path);
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class World
    {
        public InMemoryFileSystem Disk { get; } = new();
        public ArmableThrowingClock Clock { get; } = new();
        public InProcessLiveOperationRegistry Markers { get; } = new();
        public OperationJournal Journal { get; }
        public FileActionService Service { get; }

        public World()
        {
            Disk.AddFile(Jpeg, "jpeg", Stamp);
            Journal = new OperationJournal(Paths, Disk, new FixedClock(), liveOperations: Markers);
            Service = new FileActionService(Journal, Disk, Clock, new Bin(Disk));
            // The first append is the Prepared line; every clock read after it belongs to the Committed stamp.
            Disk.OpenAppendHook = _ =>
            {
                Clock.Armed = true;
                return null;
            };
        }
    }

    [Theory(DisplayName = "ExecuteAsync: a clock that fails stamping Committed after the mutation still reports success with no Failed line")]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    [InlineData(FileOperationType.Recycle)]
    public async Task ExecuteAsync_ClockThrowsStampingCommitted_SucceedsWithJournalErrorAndNoFailedLine(FileOperationType operation)
    {
        var world = new World();
        var destinationFolder = operation == FileOperationType.Recycle ? null : "selected";

        var result = await world.Service.ExecuteAsync(new FileActionRequest(Jpeg, operation, destinationFolder));

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.False(result.JournalPersisted);
        Assert.NotNull(result.JournalError);
        Assert.Equal(operation == FileOperationType.Copy, world.Disk.FileExists(Jpeg)); // Move/Recycle removed it, Copy kept it
        if (result.DestinationPath is { } destination)
            Assert.True(world.Disk.FileExists(destination));

        var prepared = Assert.Single(world.Journal.ReadPendingOperations());
        Assert.Equal(operation, prepared.Type);
        Assert.Empty(world.Journal.ReadFailedOperations());
        Assert.Empty(world.Journal.ReadCommittedMoves());
        Assert.False(world.Markers.IsLive(prepared.Id));
    }

    [Fact(DisplayName = "JournalTransaction.Commit: a clock that throws returns a Committed entry and a journal error without throwing")]
    public void Commit_ClockThrows_ReturnsEntryAndJournalErrorWithoutThrowing()
    {
        var fs = new InMemoryFileSystem();
        var markers = new InProcessLiveOperationRegistry();
        var journal = new OperationJournal(Paths, fs, new FixedClock(), liveOperations: markers);
        var clock = new ArmableThrowingClock();
        var prepared = new JournalEntry("op", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\selected\a.jpg", 10, Stamp, Stamp);
        using var tx = new JournalTransaction(journal, clock, prepared);
        tx.Begin();
        clock.Armed = true;

        var committed = tx.Commit(out var journalError);

        Assert.Equal(JournalState.Committed, committed.State);
        Assert.Equal("op", committed.Id);
        Assert.NotNull(journalError);
        Assert.Equal("op", Assert.Single(journal.ReadPendingOperations()).Id); // only Prepared is on disk: reconcile will judge it
        Assert.Empty(journal.ReadCommittedMoves());
    }
}