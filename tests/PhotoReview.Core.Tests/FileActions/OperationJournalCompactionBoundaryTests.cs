using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Mutation-testing gaps of <see cref="OperationJournal.TryCompact"/> (size / gain boundaries, the reported byte counts with a
/// concurrent delta, stale-leftover cleanup) on an in-memory file system and an in-memory staged replacement.
/// </summary>
public sealed class OperationJournalCompactionBoundaryTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private const int OneMiB = 1024 * 1024;

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class MemoryCompactionFiles(InMemoryFileSystem fs) : IJournalCompactionFiles
    {
        public Action? BeforeLock { get; set; }

        public Stream OpenReadDenyWriters(string path)
        {
            BeforeLock?.Invoke();
            return fs.OpenReadShared(path);
        }

        public IStagedReplacement CreateStagedReplacement(string tempPath) => new Staged(fs);

        private sealed class Staged(InMemoryFileSystem fs) : IStagedReplacement
        {
            private readonly MemoryStream _bytes = new();
            public void Write(ReadOnlySpan<byte> bytes) => _bytes.Write(bytes);
            public void FlushToDisk() { }
            public void ReplaceAtomically(string destination) => fs.AddFile(destination, _bytes.ToArray());
            public void Dispose() => _bytes.Dispose();
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly MemoryCompactionFiles _files;
    private readonly FixedClock _clock = new(Stamp.AddDays(1));
    private readonly OperationJournal _journal;

    public OperationJournalCompactionBoundaryTests()
    {
        _files = new MemoryCompactionFiles(_fs);
        _journal = new OperationJournal(Paths, _fs, _clock, compactionFiles: _files);
    }

    private static byte[] Line(JournalEntry entry) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\r\n");

    private static JournalEntry Entry(int i, JournalState state) => new(
        $"j{i:D6}", FileOperationType.Copy, state, $@"C:\photos\{i}.jpg", $@"C:\sel\{i}.jpg", 12345, Stamp, Stamp);

    private static byte[] Spaces(int totalLengthWithNewline) =>
        [.. Enumerable.Repeat((byte)' ', totalLengthWithNewline - 2), (byte)'\r', (byte)'\n'];

    private static byte[] Garbage(int totalLengthWithNewline) =>
        [.. Enumerable.Repeat((byte)'x', totalLengthWithNewline - 2), (byte)'\r', (byte)'\n'];

    /// <summary>Prepared+Committed pairs (the Prepared half is droppable) then one blank line, exactly <paramref name="total"/> bytes.</summary>
    private static (byte[] Bytes, int Pairs) CompactibleJournal(int total)
    {
        var parts = new List<byte[]>();
        var size = 0;
        var pairs = 0;
        while (true)
        {
            var pair = Line(Entry(pairs, JournalState.Prepared)).Concat(Line(Entry(pairs, JournalState.Committed))).ToArray();
            if (size + pair.Length + 2 > total) break;
            parts.Add(pair);
            size += pair.Length;
            pairs++;
        }
        parts.Add(Spaces(total - size));
        return (parts.SelectMany(part => part).ToArray(), pairs);
    }

    private byte[] JournalBytes()
    {
        using var stream = _fs.OpenReadShared(Paths.JournalFile);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Size threshold.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TryCompact_JournalOfExactlyTheThreshold_IsCompacted()
    {
        var (bytes, pairs) = CompactibleJournal(OneMiB);
        _fs.AddFile(Paths.JournalFile, bytes);

        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        Assert.Equal(OneMiB, result.BytesBefore);
        var after = JournalBytes();
        Assert.Equal(after.Length, result.BytesAfter);
        Assert.Equal(pairs, _journal.ReadPendingAndFailedOperations().Count + Encoding.UTF8.GetString(after).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void TryCompact_JournalOneByteUnderTheThreshold_IsLeftAlone()
    {
        var (bytes, _) = CompactibleJournal(OneMiB - 1);
        _fs.AddFile(Paths.JournalFile, bytes);

        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.BelowThreshold, result.Outcome);
        Assert.Equal(OneMiB - 1, result.BytesBefore);
        Assert.Equal(bytes, JournalBytes());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Gain threshold (25% of the snapshot).
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>1 MiB journal: <paramref name="droppable"/> bytes of blank line + kept Prepared-only lines + one kept malformed line.</summary>
    private static byte[] JournalWithDroppableBytes(int droppable)
    {
        var parts = new List<byte[]> { Spaces(droppable) };
        var keptTarget = OneMiB - droppable;
        var size = 0;
        for (var i = 0; ; i++)
        {
            var line = Line(Entry(i, JournalState.Prepared));
            if (size + line.Length + 3 > keptTarget) break;
            parts.Add(line);
            size += line.Length;
        }
        parts.Add(Garbage(keptTarget - size));
        var bytes = parts.SelectMany(part => part).ToArray();
        Assert.Equal(OneMiB, bytes.Length);
        return bytes;
    }

    [Fact]
    public void TryCompact_ExactlyAQuarterDroppable_IsCompacted()
    {
        _fs.AddFile(Paths.JournalFile, JournalWithDroppableBytes(OneMiB / 4));

        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        Assert.Equal(OneMiB - OneMiB / 4, result.BytesAfter);
    }

    [Fact]
    public void TryCompact_OneByteLessThanAQuarterDroppable_IsNotWorthIt()
    {
        var bytes = JournalWithDroppableBytes(OneMiB / 4 - 1);
        _fs.AddFile(Paths.JournalFile, bytes);

        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.NotWorthIt, result.Outcome);
        Assert.Equal(bytes, JournalBytes());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Lines appended between the snapshot and the lock (the delta).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TryCompact_LinesAppendedMeanwhile_AreKeptAndBytesAfterCountsThem()
    {
        var (bytes, _) = CompactibleJournal(OneMiB);
        _fs.AddFile(Paths.JournalFile, bytes);
        var other = new OperationJournal(Paths, _fs, _clock);
        _files.BeforeLock = () => other.Append(Entry(900001, JournalState.Prepared));

        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        var after = JournalBytes();
        Assert.Equal(after.Length, result.BytesAfter);
        Assert.Equal(bytes.Length + Line(Entry(900001, JournalState.Prepared)).Length, result.BytesBefore);
        Assert.Equal("j900001", Assert.Single(_journal.ReadPendingOperations()).Id);
    }

    [Fact]
    public void TryCompact_TornLineAppendedMeanwhile_GetsANewlineAndBytesAfterCountsIt()
    {
        var (bytes, _) = CompactibleJournal(OneMiB);
        _fs.AddFile(Paths.JournalFile, bytes);
        _files.BeforeLock = () =>
        {
            using var stream = _fs.OpenAppend(Paths.JournalFile, durable: false);
            var torn = Encoding.UTF8.GetBytes("{\"Id\":\"torn\",\"Ty");
            stream.Write(torn, 0, torn.Length);
            stream.Flush();
        };

        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        var after = JournalBytes();
        Assert.Equal(after.Length, result.BytesAfter);
        Assert.Equal((byte)'\n', after[^1]);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Stale replacement files left by a crashed compaction.
    // ---------------------------------------------------------------------------------------------------------

    private static string Leftover(string name) => Paths.JournalFile + "." + name + OperationJournal.CompactionFileSuffix;

    [Fact]
    public void TryCompact_LeftoverExactlyAsOldAsTheStaleAge_IsRemoved()
    {
        _fs.AddFile(Leftover("old"), "partial", _clock.UtcNow - OperationJournal.StaleCompactionFileAge);
        _fs.AddFile(Leftover("young"), "partial", _clock.UtcNow - OperationJournal.StaleCompactionFileAge + TimeSpan.FromSeconds(1));

        _journal.TryCompact();

        Assert.False(_fs.FileExists(Leftover("old")));
        Assert.True(_fs.FileExists(Leftover("young")));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void TryCompact_LeftoverCannotBeDeleted_IsIgnoredAndTheRunContinues(Type failure)
    {
        _fs.AddFile(Leftover("old"), "partial", _clock.UtcNow - TimeSpan.FromDays(2));
        _fs.DeleteHook = _ => (Exception)Activator.CreateInstance(failure, "in use")!;

        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.BelowThreshold, result.Outcome); // no journal yet: nothing to compact, but not Failed
    }

    [Fact]
    public void TryCompact_JournalFolderDoesNotExist_ReportsBelowThresholdNotFailure()
    {
        var result = _journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.BelowThreshold, result.Outcome);
    }
}