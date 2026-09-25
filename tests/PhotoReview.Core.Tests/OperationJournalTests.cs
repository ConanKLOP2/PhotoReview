using System.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

    [Fact(DisplayName = "A journal line with an unknown Type or State is skipped, not read as a pending Move")]
    public void UnknownTypeOrState_IsNotReadAsPendingMove()
    {
        var bogus = new[]
        {
            """{"Id":"bogus-1","Type":"Teleport","State":"Prepared","Source":"C:/x.jpg","Destination":"C:/y.jpg","Size":1,"LastWriteUtc":"2026-01-01T00:00:00Z","TimestampUtc":"2026-01-01T00:00:00Z"}""",
            """{"Id":"bogus-2","Type":"Move","State":"Exploded","Source":"C:/x.jpg","Destination":"C:/y.jpg","Size":1,"LastWriteUtc":"2026-01-01T00:00:00Z","TimestampUtc":"2026-01-01T00:00:00Z"}""",
            """{"Id":"bogus-3","Source":"C:/x.jpg","Destination":"C:/y.jpg","Size":1,"LastWriteUtc":"2026-01-01T00:00:00Z","TimestampUtc":"2026-01-01T00:00:00Z"}""",
        };
        File.AppendAllLines(JournalFile, bogus);

        Assert.Empty(_journal.ReadPendingOperations());
    }

    [Fact(DisplayName = "An append after a torn (newline-less) last line starts on its own line and stays readable")]
    public void Append_AfterTornLastLine_KeepsTheNewEntry()
    {
        File.AppendAllText(JournalFile, "{\"Id\":\"torn\",\"Type\":\"Mov"); // power loss mid-record: no trailing newline
        var freshJournal = new OperationJournal(); // a new process: has not checked the tail yet
        var id = Guid.NewGuid().ToString("N");

        freshJournal.Append(new JournalEntry(id, FileOperationType.Move, JournalState.Prepared, _source, _destination,
            _info.Length, _info.LastWriteTimeUtc, DateTime.UtcNow));

        Assert.Contains(freshJournal.ReadPendingOperations(), e => e.Id == id);
    }

    [Fact(DisplayName = "Journal entries are durably written as JSONL")]
    [Trait("Category", "Integration")]
    public void JournalEntriesAreDurablyWrittenAsJsonl()
    {
        var journalFile = JournalFile;
        Assert.True(new FileInfo(journalFile).Length > 0
            && File.ReadAllLines(journalFile).All(line => line.StartsWith('{')));
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

    [Fact(DisplayName = "Dismiss removes failed and pending entries from Recovery without touching others")]
    public void Dismiss_FailedAndPending_RemovedFromRecoveryLists()
    {
        var journal = CreateJournal();
        journal.Append(new JournalEntry("failed", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1024, _clock.UtcNow, _clock.UtcNow, "Locked"));
        journal.Append(new JournalEntry("pending", FileOperationType.Copy, JournalState.Prepared,
            @"C:\photos\c.jpg", @"C:\photos\d.jpg", 1024, _clock.UtcNow, _clock.UtcNow));
        journal.Append(new JournalEntry("kept", FileOperationType.Recycle, JournalState.Failed,
            @"C:\photos\e.jpg", null, 1024, _clock.UtcNow, _clock.UtcNow, "Locked"));

        var dismissed = journal.Dismiss([journal.ReadFailedOperations().Single(x => x.Id == "failed"), journal.ReadPendingOperations().Single()]);

        Assert.All(dismissed, x => Assert.Equal(JournalState.Dismissed, x.State));
        Assert.Empty(journal.ReadPendingOperations());
        Assert.Equal("kept", Assert.Single(journal.ReadFailedOperations()).Id);
        Assert.Empty(journal.ReconcilePendingOperations());
        Assert.Empty(journal.Dismiss([]));
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
        // Q-L3: the journal stores a stable code + invariant English; the UI shows the localized text by code.
        Assert.Equal(JournalErrors.SourceStillExistsAfterRecovery, recFail.ErrorCode);
        Assert.Equal("The source still exists after session recovery.", recFail.Error);
        Assert.Equal("Nguồn vẫn tồn tại sau khi khôi phục phiên.", JournalErrors.Describe(recFail));
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

    [Fact(DisplayName = "Large journal startup reads only recent committed moves within a bounded-work budget (Q-S3/TS05)")]
    public void LargeJournal_ReadCommittedMoves_IsBoundedAndFast()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-T62-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operations.jsonl");
        try
        {
            const int totalEntries = 100_000;
            var setupWatch = Stopwatch.StartNew();

            // TS05: format the JSONL line directly instead of JsonSerializer.Serialize per entry -
            // reflection-based serialization of 100_000 entries was the ~3.3 s setup cost this test
            // used to pay before it ever measured the thing it cares about (the bounded tail read).
            var tsText = _clock.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            using (var writer = new StreamWriter(path, false, Encoding.UTF8, 256 * 1024))
            {
                for (var i = 0; i < totalEntries; i++)
                {
                    writer.Write(
                        "{\"Id\":\"old-" + i.ToString(CultureInfo.InvariantCulture) +
                        "\",\"Type\":\"Move\",\"State\":\"Committed\",\"Source\":\"C:\\\\photos\\\\source-" +
                        i.ToString(CultureInfo.InvariantCulture) +
                        ".jpg\",\"Destination\":\"C:\\\\photos\\\\dest-" +
                        i.ToString(CultureInfo.InvariantCulture) +
                        ".jpg\",\"Size\":" + i.ToString(CultureInfo.InvariantCulture) +
                        ",\"LastWriteUtc\":\"" + tsText + "\",\"TimestampUtc\":\"" + tsText +
                        "\",\"Error\":null}\n");
                }
            }
            setupWatch.Stop();

            // Q-S3: assert bounded work (bytes read and lines scanned), not just wall-clock time -
            // this is what actually enforces "startup reads only the recent tail" deterministically.
            var boundedFileSystem = new BoundedReadFileSystem(new PhysicalFileSystem());
            var journal = new OperationJournal(new FakeAppPaths(path), boundedFileSystem, _clock);

            var readWatch = Stopwatch.StartNew();
            var entries = journal.ReadCommittedMoves();
            readWatch.Stop();

            Assert.Equal(200, entries.Count);
            Assert.Equal("old-99800", entries[0].Id);
            Assert.Equal("old-99999", entries[^1].Id);

            // Each line is ~140 bytes, so the 200-entry tail is ~28 KB. The reverse-tail reader
            // doubles its read window (starting at 256 KB) until it has >= 200 committed moves, so
            // for this all-Move-Committed fixture it reads exactly one window. Bound generously at
            // 1 MB (~35x the 200-entry tail) - a regression to a full-file scan would read close to
            // the whole ~14 MB file, which trips this by more than an order of magnitude.
            const long maxBytes = 1024 * 1024;
            Assert.True(boundedFileSystem.TotalBytesRead <= maxBytes,
                $"ReadCommittedMoves read {boundedFileSystem.TotalBytesRead} bytes " +
                $"(bounded-work budget: {maxBytes}). A full-file-scan regression reads ~{new FileInfo(path).Length} bytes.");

            const long maxLinesScanned = 3_000; // >> 200-entry tail, << 100_000 total lines
            Assert.True(boundedFileSystem.ApproxLinesScanned <= maxLinesScanned,
                $"ReadCommittedMoves scanned ~{boundedFileSystem.ApproxLinesScanned} lines (budget: {maxLinesScanned}).");

            // Generous wall-clock backstop only (TS05/Q-S3): the byte/line-bounded assertions above
            // are the real, deterministic regression guard; this just catches pathological slowness
            // (e.g. an O(n^2) rewrite) that the counters wouldn't otherwise flag.
            Assert.True(readWatch.ElapsedMilliseconds < 2000,
                $"Journal tail read took {readWatch.ElapsedMilliseconds} ms (backstop budget: 2000 ms). Setup: {setupWatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory(DisplayName = "Reverse reader returns exactly the last <=200 committed Moves across window doublings (CORE-08)")]
    [InlineData(37)]  // dense: a single 256 KB window already holds > 200 Moves
    [InlineData(400)] // sparse: needs several doublings, each head line cut mid-line
    [InlineData(5000)] // fewer than 200 Moves overall: reads back to the start of the file
    public void LargeJournal_ReadCommittedMoves_ReturnsLastMovesAcrossWindows(int movePeriod)
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-C08b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operations.jsonl");
        try
        {
            const int totalEntries = 20_000; // ~4.5 MB: over the 1 MB full-scan threshold
            var tsText = _clock.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var expected = new List<string>();
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 256 * 1024))
            {
                for (var i = 0; i < totalEntries; i++)
                {
                    var id = "e-" + i.ToString(CultureInfo.InvariantCulture);
                    var isMove = i % movePeriod == 0;
                    if (isMove) expected.Add(id);
                    writer.Write(
                        "{\"Id\":\"" + id + "\",\"Type\":\"" + (isMove ? "Move" : "Recycle") +
                        "\",\"State\":\"Committed\",\"Source\":\"C:\\\\photos\\\\s-" + id +
                        ".jpg\",\"Destination\":null,\"Size\":1,\"LastWriteUtc\":\"" + tsText + "\",\"TimestampUtc\":\"" + tsText +
                        "\",\"Error\":null}\n");
                }
            }

            var journal = new OperationJournal(new FakeAppPaths(path), new PhysicalFileSystem(), _clock);

            Assert.Equal(expected.TakeLast(200), journal.ReadCommittedMoves().Select(e => e.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Move-sparse large journal: reverse reader reads and parses each byte once (CORE-08, ADR 0003)")]
    [Trait("Category", "Slow")]
    public void LargeMoveSparseJournal_ReadCommittedMoves_ParsesEachByteOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-C08-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operations.jsonl");
        try
        {
            const int totalEntries = 100_000;
            const int movePeriod = 1_000; // 1 Move per 1,000 Recycle: far fewer than the 200-entry limit, so the whole file is needed
            var tsText = _clock.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 256 * 1024))
            {
                for (var i = 0; i < totalEntries; i++)
                {
                    var id = i.ToString(CultureInfo.InvariantCulture);
                    var isMove = i % movePeriod == 0;
                    writer.Write(
                        "{\"Id\":\"e-" + id + "\",\"Type\":\"" + (isMove ? "Move" : "Recycle") +
                        "\",\"State\":\"Committed\",\"Source\":\"C:\\\\photos\\\\source-" + (isMove ? "\\\"Type\\\":\\\"Recycle\\\"-" : "") + id + // Move paths embed the skip token (JSON-escaped): must not be skipped
                        ".jpg\",\"Destination\":" + (isMove ? "\"C:\\\\photos\\\\dest-" + id + ".jpg\"" : "null") +
                        ",\"Size\":" + id + ",\"LastWriteUtc\":\"" + tsText + "\",\"TimestampUtc\":\"" + tsText +
                        "\",\"Error\":null}\n");
                }
            }

            var fileLength = new FileInfo(path).Length;
            // Timed on the plain file system (the counting wrapper scans every byte itself); bytes counted in a second read.
            var timedJournal = new OperationJournal(new FakeAppPaths(path), new PhysicalFileSystem(), _clock);
            _ = timedJournal.ReadCommittedMoves(); // warm-up: JIT + OS file cache, as in a real second launch
            var watch = Stopwatch.StartNew();
            var entries = timedJournal.ReadCommittedMoves();
            watch.Stop();

            var boundedFileSystem = new BoundedReadFileSystem(new PhysicalFileSystem());
            var journal = new OperationJournal(new FakeAppPaths(path), boundedFileSystem, _clock);
            Assert.Equal(entries.Select(e => e.Id), journal.ReadCommittedMoves().Select(e => e.Id));

            Assert.Equal(totalEntries / movePeriod, entries.Count);
            Assert.Equal("e-0", entries[0].Id);
            Assert.Equal("e-99000", entries[^1].Id);

            // Doubling windows that each re-read the tail would read ~2x the file; reading only the new prefix reads it once.
            Assert.True(boundedFileSystem.TotalBytesRead <= fileLength + 8 * 1024,
                $"Read {boundedFileSystem.TotalBytesRead} bytes of a {fileLength}-byte journal (each byte must be read once).");
            Assert.True(watch.ElapsedMilliseconds < 100,
                $"Move-sparse tail read took {watch.ElapsedMilliseconds} ms (ADR 0003 budget: 100 ms).");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Recovery list (pending + failed) parses the journal once, not once per state (R2-F-17)")]
    public void ReadPendingAndFailedOperations_ParsesJournalOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-F17-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "operations.jsonl");
        try
        {
            var seed = new OperationJournal(new FakeAppPaths(path), new PhysicalFileSystem(), _clock);
            JournalEntry Entry(string id, JournalState state) => new(id, FileOperationType.Move, state, "C:\\a\\" + id + ".jpg", "C:\\b\\" + id + ".jpg",
                4, _clock.UtcNow, _clock.UtcNow);
            seed.Append(Entry("done", JournalState.Prepared));
            seed.Append(Entry("done", JournalState.Committed));
            seed.Append(Entry("stuck", JournalState.Prepared));
            seed.Append(Entry("broken", JournalState.Prepared));
            seed.Append(Entry("broken", JournalState.Failed));
            var fileLength = new FileInfo(path).Length;

            var counting = new BoundedReadFileSystem(new PhysicalFileSystem());
            var journal = new OperationJournal(new FakeAppPaths(path), counting, _clock);
            var list = journal.ReadPendingAndFailedOperations();

            Assert.Equal(["stuck", "broken"], list.Select(e => e.Id));
            Assert.True(counting.TotalBytesRead <= fileLength,
                $"Read {counting.TotalBytesRead} bytes of a {fileLength}-byte journal (one pass expected).");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// TS05: wraps <see cref="IFileSystem.OpenReadShared"/> to count bytes actually read and an
    /// approximate line count (newline bytes seen), so the bounded-tail-read contract can be
    /// asserted deterministically instead of only via wall-clock time.
    /// </summary>
    private sealed class BoundedReadFileSystem(IFileSystem inner) : IFileSystem
    {
        private long _totalBytesRead;
        private long _approxLinesScanned;

        public long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);
        public long ApproxLinesScanned => Interlocked.Read(ref _approxLinesScanned);

        public Stream OpenReadShared(string path, int bufferSize = 65536) =>
            new CountingReadStream(inner.OpenReadShared(path, bufferSize), this);

        private void RecordRead(byte[] buffer, int offset, int count)
        {
            Interlocked.Add(ref _totalBytesRead, count);
            var lines = 0;
            for (var i = offset; i < offset + count; i++)
            {
                if (buffer[i] == (byte)'\n') lines++;
            }
            Interlocked.Add(ref _approxLinesScanned, lines);
        }

        private sealed class CountingReadStream(Stream inner, BoundedReadFileSystem owner) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = inner.Read(buffer, offset, count);
                if (read > 0) owner.RecordRead(buffer, offset, read);
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => inner.Flush();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }

        // Pass-through: everything else is untouched, only OpenReadShared is instrumented.
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileStat? GetFileStat(string path) => inner.GetFileStat(path);
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenAppendDurable(string path) => inner.OpenAppendDurable(path);
        public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }
}
