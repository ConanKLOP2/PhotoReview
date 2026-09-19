using System.Text;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class OperationJournalTests
{
    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = utcNow;
        public DateTime UtcNow { get; set; }
        public long Timestamp => 0;
    }

    private sealed class FakeAppPaths : IAppPaths
    {
        public FakeAppPaths(string journalPath) => JournalFile = journalPath;
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile { get; }
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));
    private readonly string _journalPath = @"C:\data\operations.jsonl";

    private OperationJournal CreateJournal() =>
        new(new FakeAppPaths(_journalPath), _fs, _clock);

    [Fact(DisplayName = "Append creates directory and writes readable entries")]
    public void Append_CreatesDirectoryAndWritesReadableEntries()
    {
        var journal = CreateJournal();
        var entry = new JournalEntry("op-1", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1024, _clock.UtcNow, _clock.UtcNow);

        journal.Append(entry);

        Assert.True(_fs.FileExists(_journalPath));
        var committed = journal.ReadCommittedMoves();
        Assert.Single(committed);
        Assert.Equal("op-1", committed[0].Id);
        Assert.Equal(@"C:\photos\a.jpg", committed[0].Source);
        Assert.Equal(@"C:\photos\b.jpg", committed[0].Destination);
    }

    [Fact(DisplayName = "ReadPendingOperations and ReadFailedOperations reflect latest entry")]
    public void ReadOperations_ReflectsLatestState()
    {
        var journal = CreateJournal();
        var id = "op-retry";

        // Step 1: Failed
        journal.Append(new JournalEntry(id, FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1024, _clock.UtcNow, _clock.UtcNow, "Locked"));
        Assert.Single(journal.ReadFailedOperations());
        Assert.Empty(journal.ReadPendingOperations());

        // Step 2: In-flight retry with same id
        journal.Append(new JournalEntry(id, FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1024, _clock.UtcNow, _clock.UtcNow));
        Assert.Empty(journal.ReadFailedOperations());
        var pending = journal.ReadPendingOperations();
        Assert.Single(pending);
        Assert.Equal(id, pending[0].Id);

        // Step 3: Committed
        journal.Append(new JournalEntry(id, FileOperationType.Move, JournalState.Committed,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1024, _clock.UtcNow, _clock.UtcNow));
        Assert.Empty(journal.ReadFailedOperations());
        Assert.Empty(journal.ReadPendingOperations());
        Assert.Single(journal.ReadCommittedMoves());
    }

    [Fact(DisplayName = "Tolerates invalid and corrupted JSONL lines")]
    public void ToleratesCorruptedLines()
    {
        var journal = CreateJournal();
        var validEntry = new JournalEntry("op-good", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\1.jpg", @"C:\photos\2.jpg", 500, _clock.UtcNow, _clock.UtcNow);
        journal.Append(validEntry);

        // Manually inject bad lines into the file
        using (var stream = _fs.OpenAppendDurable(_journalPath))
        {
            var badContent = Encoding.UTF8.GetBytes("{not json}\r\n\r\n{ incomplete json \r\n");
            stream.Write(badContent, 0, badContent.Length);
            stream.Flush();
        }

        var validEntry2 = new JournalEntry("op-good-2", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\3.jpg", @"C:\photos\4.jpg", 600, _clock.UtcNow, _clock.UtcNow);
        journal.Append(validEntry2);

        var committed = journal.ReadCommittedMoves();
        Assert.Equal(2, committed.Count);
        Assert.Equal("op-good", committed[0].Id);
        Assert.Equal("op-good-2", committed[1].Id);
    }

    [Fact(DisplayName = "ReconcilePendingOperations for Recycle: committed if source absent, failed if source present")]
    public void ReconcileRecycle()
    {
        var journal = CreateJournal();

        // Recycle 1: source absent -> Committed
        journal.Append(new JournalEntry("rec-done", FileOperationType.Recycle, JournalState.Prepared,
            @"C:\photos\absent.jpg", null, 100, _clock.UtcNow, _clock.UtcNow));

        // Recycle 2: source still present -> Failed
        _fs.WriteAllTextAtomic(@"C:\photos\present.jpg", "hello");
        journal.Append(new JournalEntry("rec-fail", FileOperationType.Recycle, JournalState.Prepared,
            @"C:\photos\present.jpg", null, 5, _clock.UtcNow, _clock.UtcNow));

        var reconciled = journal.ReconcilePendingOperations();
        Assert.Equal(2, reconciled.Count);

        var recDone = reconciled.First(x => x.Id == "rec-done");
        Assert.Equal(JournalState.Committed, recDone.State);
        Assert.Null(recDone.Error);

        var recFail = reconciled.First(x => x.Id == "rec-fail");
        Assert.Equal(JournalState.Failed, recFail.State);
        Assert.Equal("Nguồn vẫn tồn tại sau khi khôi phục phiên.", recFail.Error);
    }

    [Fact(DisplayName = "ReconcilePendingOperations for Move: committed if source absent and destination size matches")]
    public void ReconcileMove()
    {
        var journal = CreateJournal();

        // Move 1: source absent, dest matches size 4 -> Committed
        _fs.WriteAllTextAtomic(@"C:\photos\dest1.jpg", "1234");
        journal.Append(new JournalEntry("move-done", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\src1.jpg", @"C:\photos\dest1.jpg", 4, _clock.UtcNow, _clock.UtcNow));

        // Move 2: source still exists -> Failed
        _fs.WriteAllTextAtomic(@"C:\photos\src2.jpg", "1234");
        _fs.WriteAllTextAtomic(@"C:\photos\dest2.jpg", "1234");
        journal.Append(new JournalEntry("move-fail-source-exists", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\src2.jpg", @"C:\photos\dest2.jpg", 4, _clock.UtcNow, _clock.UtcNow));

        // Move 3: dest size mismatch -> Failed
        _fs.WriteAllTextAtomic(@"C:\photos\dest3.jpg", "12");
        journal.Append(new JournalEntry("move-fail-size-mismatch", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\src3.jpg", @"C:\photos\dest3.jpg", 4, _clock.UtcNow, _clock.UtcNow));

        var reconciled = journal.ReconcilePendingOperations();
        Assert.Equal(3, reconciled.Count);

        Assert.Equal(JournalState.Committed, reconciled.First(x => x.Id == "move-done").State);
        Assert.Equal(JournalState.Failed, reconciled.First(x => x.Id == "move-fail-source-exists").State);
        Assert.Equal(JournalState.Failed, reconciled.First(x => x.Id == "move-fail-size-mismatch").State);
    }

    [Fact(DisplayName = "ReconcilePendingOperations for Copy: committed even if source still exists")]
    public void ReconcileCopy()
    {
        var journal = CreateJournal();

        // Copy 1: source exists, dest exists with matching size -> Committed
        _fs.WriteAllTextAtomic(@"C:\photos\src.jpg", "1234");
        _fs.WriteAllTextAtomic(@"C:\photos\dest.jpg", "1234");
        journal.Append(new JournalEntry("copy-done", FileOperationType.Copy, JournalState.Prepared,
            @"C:\photos\src.jpg", @"C:\photos\dest.jpg", 4, _clock.UtcNow, _clock.UtcNow));

        // Copy 2: dest missing -> Failed
        journal.Append(new JournalEntry("copy-fail", FileOperationType.Copy, JournalState.Prepared,
            @"C:\photos\src.jpg", @"C:\photos\missing.jpg", 4, _clock.UtcNow, _clock.UtcNow));

        var reconciled = journal.ReconcilePendingOperations();
        Assert.Equal(2, reconciled.Count);

        Assert.Equal(JournalState.Committed, reconciled.First(x => x.Id == "copy-done").State);
        Assert.Equal(JournalState.Failed, reconciled.First(x => x.Id == "copy-fail").State);
    }

    [Fact(DisplayName = "Constructor throws ArgumentNullException on null dependencies")]
    public void Constructor_NullValidation()
    {
        var paths = new FakeAppPaths(_journalPath);
        Assert.Throws<ArgumentNullException>(() => new OperationJournal(null!, _fs, _clock));
        Assert.Throws<ArgumentNullException>(() => new OperationJournal(paths, null!, _clock));
        Assert.Throws<ArgumentNullException>(() => new OperationJournal(paths, _fs, null!));
    }
}
