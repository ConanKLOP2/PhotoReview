using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// The power-loss-safe journal mode flushes the append stream to disk (<c>FileStream.Flush(true)</c>), which a fake cannot observe;
/// the <see cref="OperationJournal.FlushToDisk"/> seam records the call. Real temp file only; the Recycle Bin is never involved.
/// </summary>
public sealed class OperationJournalDurableFlushTests : IDisposable
{
    private readonly TempRoot _root = new("journal-flush");
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc));

    public void Dispose() => _root.Dispose();

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class Paths(string journal) : IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile { get; } = journal;
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private JournalEntry Entry(string id) =>
        new(id, FileOperationType.Move, JournalState.Committed, @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1, _clock.UtcNow, _clock.UtcNow);

    [Fact]
    public void Append_PowerLossSafeOnARealFile_FlushesToDiskOncePerAppendAfterTheBytesWereWritten()
    {
        var path = _root.Combine("operations.jsonl");
        var flushedSizes = new List<long>();
        var journal = new OperationJournal(new Paths(path), new PhysicalFileSystem(), _clock, () => JournalDurability.PowerLossSafe)
        {
            FlushToDisk = fs => flushedSizes.Add(fs.Length),
        };

        journal.Append(Entry("op-1"));
        journal.Append(Entry("op-2"));

        Assert.Equal(2, flushedSizes.Count);
        Assert.True(flushedSizes[0] > 0, "the flush must come after the line was written");
        Assert.True(flushedSizes[1] > flushedSizes[0]);
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void Append_FastMode_NeverFlushesToDisk()
    {
        var calls = 0;
        var journal = new OperationJournal(new Paths(_root.Combine("operations.jsonl")), new PhysicalFileSystem(), _clock)
        {
            FlushToDisk = _ => calls++,
        };

        journal.Append(Entry("op-1"));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Append_PowerLossSafeOnAStreamThatIsNotAFileStream_FallsBackToAPlainFlush()
    {
        var calls = 0;
        var memory = new InMemoryFileSystem();
        var journal = new OperationJournal(new Paths(@"C:\data\operations.jsonl"), memory, _clock, () => JournalDurability.PowerLossSafe)
        {
            FlushToDisk = _ => calls++,
        };

        journal.Append(Entry("op-1"));

        Assert.Equal(0, calls);
        Assert.Single(memory.ReadLines(@"C:\data\operations.jsonl"));
    }
}