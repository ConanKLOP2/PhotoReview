using System.Text;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests;

/// <summary>
/// Journal durability and Move-commit contracts. The constructor
/// reproduces the original suite's committed-Move setup for every test.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class OperationJournalTests : IDisposable
{
    private readonly DataRootFixture _data = new();
    private readonly OperationJournal _journal = new();
    private readonly string _source;
    private readonly string _destination;
    private readonly string _operationId;
    private readonly FileInfo _info;

    public OperationJournalTests()
    {
        var root = _data.Path;
        _source = Path.Combine(root, "source.jpg");
        _destination = Path.Combine(root, "dest", "source.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(_destination)!);
        File.WriteAllBytes(_source, [1, 2, 3, 4]);
        _info = new FileInfo(_source);
        _operationId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(_operationId, FileOperationType.Move, JournalState.Prepared, _source, _destination,
            _info.Length, _info.LastWriteTimeUtc, DateTime.UtcNow));
        File.Move(_source, _destination);
        _journal.Append(new JournalEntry(_operationId, FileOperationType.Move, JournalState.Committed, _source, _destination,
            _info.Length, _info.LastWriteTimeUtc, DateTime.UtcNow));
    }

    public void Dispose() => _data.Dispose();

    private string JournalFile =>
        Directory.GetFiles(Path.Combine(_data.Path, "app-data"), "operations.jsonl").Single();

    [Fact(DisplayName = "Journal committed Move")]
    public void JournalCommittedMove() =>
        Assert.Contains(_journal.ReadCommittedMoves(),
            x => x.Source == _source && x.Destination == _destination);

    [Fact(DisplayName = "Journal has no pending committed Move")]
    public void JournalHasNoPendingCommittedMove() => Assert.Empty(_journal.ReadPendingOperations());

    [Fact(DisplayName = "Journal entries are durably written as JSONL")]
    [Trait("Category", "Integration")]
    public void JournalEntriesAreDurablyWrittenAsJsonl()
    {
        var journalFile = JournalFile;
        Assert.True(new FileInfo(journalFile).Length > 0
            && File.ReadAllLines(journalFile).All(line => line.StartsWith("{", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "Journal concurrent append/read remains line-consistent")]
    [Trait("Category", "Integration")]
    public void JournalConcurrentAppendReadRemainsLineConsistent()
    {
        Parallel.For(0, 8, i => _journal.Append(new JournalEntry($"parallel-{i}", FileOperationType.Move, JournalState.Committed,
            _source, _destination, 4, _info.LastWriteTimeUtc, DateTime.UtcNow)));
        Assert.Equal(8, _journal.ReadCommittedMoves()
            .Count(x => x.Id.StartsWith("parallel-", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "Move preserves bytes")]
    public void MovePreservesBytes() =>
        Assert.True(File.ReadAllBytes(_destination).SequenceEqual(new byte[] { 1, 2, 3, 4 }));

    [Fact(DisplayName = "Legacy operations fixture deserializes to enums")]
    public void LegacyOperationsFixtureDeserializesToEnums()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "operations-legacy.jsonl");
        Assert.True(File.Exists(fixturePath), $"Fixture not found at {fixturePath}");
        var lines = File.ReadAllLines(fixturePath);
        var entries = lines.Select(line => JsonSerializer.Deserialize<JournalEntry>(line)!).ToList();
        Assert.Equal(4, entries.Count);

        Assert.Equal("op-1", entries[0].Id);
        Assert.Equal(FileOperationType.Move, entries[0].Type);
        Assert.Equal(JournalState.Prepared, entries[0].State);

        Assert.Equal("op-1", entries[1].Id);
        Assert.Equal(FileOperationType.Move, entries[1].Type);
        Assert.Equal(JournalState.Committed, entries[1].State);

        Assert.Equal("op-2", entries[2].Id);
        Assert.Equal(FileOperationType.Recycle, entries[2].Type);
        Assert.Equal(JournalState.Prepared, entries[2].State);

        Assert.Equal("op-3", entries[3].Id);
        Assert.Equal(FileOperationType.Copy, entries[3].Type);
        Assert.Equal(JournalState.Failed, entries[3].State);
    }
}

/// <summary>Pending-operation reconciliation contracts.</summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class JournalReconciliationTests : IDisposable
{
    private readonly DataRootFixture _data = new();
    private readonly OperationJournal _journal = new();

    public void Dispose() => _data.Dispose();

    [Fact(DisplayName = "Pending move is committed only when source is absent and destination fingerprint matches")]
    public void PendingMoveIsCommittedWhenSourceAbsentAndFingerprintMatches()
    {
        var pendingMoveSource = Path.Combine(_data.Path, "pending-move.jpg");
        var pendingMoveDestination = Path.Combine(_data.Path, "pending-dest", "pending-move.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(pendingMoveDestination)!);
        File.WriteAllBytes(pendingMoveDestination, [8, 9, 10]);
        var pendingMoveId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(pendingMoveId, FileOperationType.Move, JournalState.Prepared, pendingMoveSource,
            pendingMoveDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
        var moveReconciled = _journal.ReconcilePendingOperations();
        Assert.Contains(moveReconciled, x => x.Id == pendingMoveId && x.State == JournalState.Committed);
    }

    [Fact(DisplayName = "Pending copy with mismatched destination is failed without replay")]
    public void PendingCopyWithMismatchedDestinationIsFailed()
    {
        var mismatchedSource = Path.Combine(_data.Path, "pending-mismatch.jpg");
        var mismatchedDestination = Path.Combine(_data.Path, "pending-mismatch-dest.jpg");
        File.WriteAllBytes(mismatchedDestination, [1, 2]);
        var mismatchId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(mismatchId, FileOperationType.Copy, JournalState.Prepared, mismatchedSource,
            mismatchedDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
        var mismatchReconciled = _journal.ReconcilePendingOperations();
        Assert.True(mismatchReconciled.Any(x => x.Id == mismatchId && x.State == JournalState.Failed)
            && _journal.ReadPendingOperations().All(x => x.Id != mismatchId));
    }

    [Fact(DisplayName = "Pending move with source still present is failed without replay")]
    public void PendingMoveWithSourceStillPresentIsFailed()
    {
        var sourceStillExists = Path.Combine(_data.Path, "pending-source-exists.jpg");
        var sourceStillDestination = Path.Combine(_data.Path, "pending-source-exists-dest.jpg");
        File.WriteAllBytes(sourceStillExists, [4, 5, 6]);
        File.WriteAllBytes(sourceStillDestination, [4, 5, 6]);
        var sourceExistsId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(sourceExistsId, FileOperationType.Move, JournalState.Prepared, sourceStillExists,
            sourceStillDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
        var sourceExistsReconciled = _journal.ReconcilePendingOperations();
        Assert.Contains(sourceExistsReconciled, x => x.Id == sourceExistsId && x.State == JournalState.Failed);
    }
}

/// <summary>Unit tests with mocked dependencies for precise contract verification.</summary>
[Trait("Category", "HotPath")]
public sealed class OperationJournalUnitTests
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

        journal.Append(new JournalEntry(id, FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1024, _clock.UtcNow, _clock.UtcNow, "Locked"));
        Assert.Single(journal.ReadFailedOperations());
        Assert.Empty(journal.ReadPendingOperations());

        journal.Append(new JournalEntry(id, FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1024, _clock.UtcNow, _clock.UtcNow));
        Assert.Empty(journal.ReadFailedOperations());
        var pending = journal.ReadPendingOperations();
        Assert.Single(pending);
        Assert.Equal(id, pending[0].Id);

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

        journal.Append(new JournalEntry("rec-done", FileOperationType.Recycle, JournalState.Prepared,
            @"C:\photos\absent.jpg", null, 100, _clock.UtcNow, _clock.UtcNow));

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

        _fs.WriteAllTextAtomic(@"C:\photos\dest1.jpg", "1234");
        journal.Append(new JournalEntry("move-done", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\src1.jpg", @"C:\photos\dest1.jpg", 4, _clock.UtcNow, _clock.UtcNow));

        _fs.WriteAllTextAtomic(@"C:\photos\src2.jpg", "1234");
        _fs.WriteAllTextAtomic(@"C:\photos\dest2.jpg", "1234");
        journal.Append(new JournalEntry("move-fail-source-exists", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\src2.jpg", @"C:\photos\dest2.jpg", 4, _clock.UtcNow, _clock.UtcNow));

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

        _fs.WriteAllTextAtomic(@"C:\photos\src.jpg", "1234");
        _fs.WriteAllTextAtomic(@"C:\photos\dest.jpg", "1234");
        journal.Append(new JournalEntry("copy-done", FileOperationType.Copy, JournalState.Prepared,
            @"C:\photos\src.jpg", @"C:\photos\dest.jpg", 4, _clock.UtcNow, _clock.UtcNow));

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

    [Fact(DisplayName = "Large journal startup reads only recent committed moves within threshold")]
    public void LargeJournal_ReadCommittedMoves_IsBoundedAndFast()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-T62-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operations.jsonl");
        try
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                for (var i = 0; i < 100_000; i++)
                {
                    var entry = new JournalEntry($"old-{i}", FileOperationType.Move, JournalState.Committed,
                        $@"C:\photos\source-{i}.jpg", $@"C:\photos\dest-{i}.jpg", i,
                        _clock.UtcNow, _clock.UtcNow);
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                }
            }

            var journal = new OperationJournal(new FakeAppPaths(path), new PhysicalFileSystem(), _clock);
            var stopwatch = Stopwatch.StartNew();
            var entries = journal.ReadCommittedMoves();
            stopwatch.Stop();

            Assert.Equal(200, entries.Count);
            Assert.Equal("old-99800", entries[0].Id);
            Assert.Equal("old-99999", entries[^1].Id);
            Assert.True(stopwatch.ElapsedMilliseconds < 100,
                $"Large journal startup took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
