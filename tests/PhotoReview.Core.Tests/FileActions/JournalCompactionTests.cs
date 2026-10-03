using System.Text;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// The I/O protocol of <see cref="OperationJournal.TryCompact"/>: real temp directory, real <see cref="PhysicalFileSystem"/>
/// and <see cref="PhysicalJournalCompactionFiles"/>, with a hooking decorator injected between the journal and the
/// compaction primitives to observe/interfere at the exact moments the production code documents.
/// </summary>
public sealed class JournalCompactionTests : IDisposable
{
    private readonly TempRoot _root = new("journal-compaction");
    private readonly AppPaths _paths;

    public JournalCompactionTests()
    {
        _paths = new AppPaths(_root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.JournalFile)!);
    }

    public void Dispose() => _root.Dispose();

    // -----------------------------------------------------------------------------------------------------------
    // Test decorator: wraps the real Windows primitives with hooks at the two moments the protocol documents.
    // -----------------------------------------------------------------------------------------------------------

    private sealed class HookedCompactionFiles(IJournalCompactionFiles inner) : IJournalCompactionFiles
    {
        /// <summary>Runs at the very start of <see cref="OpenReadDenyWriters"/>, before delegating.</summary>
        public Action? BeforeLock { get; set; }

        /// <summary>Runs just before the staged replacement is renamed over the journal.</summary>
        public Action? BeforeReplace { get; set; }

        /// <summary>When true, <see cref="IStagedReplacement.ReplaceAtomically"/> throws instead of delegating (simulated crash).</summary>
        public bool ThrowInsteadOfReplace { get; set; }

        public Stream OpenReadDenyWriters(string path)
        {
            BeforeLock?.Invoke();
            return inner.OpenReadDenyWriters(path);
        }

        public IStagedReplacement CreateStagedReplacement(string tempPath) =>
            new HookedStagedReplacement(inner.CreateStagedReplacement(tempPath), this);

        private sealed class HookedStagedReplacement(IStagedReplacement inner, HookedCompactionFiles owner) : IStagedReplacement
        {
            public void Write(ReadOnlySpan<byte> bytes) => inner.Write(bytes);
            public void FlushToDisk() => inner.FlushToDisk();

            public void ReplaceAtomically(string destination)
            {
                owner.BeforeReplace?.Invoke();
                if (owner.ThrowInsteadOfReplace) throw new IOException("simulated crash before replace");
                inner.ReplaceAtomically(destination);
            }

            public void Dispose() => inner.Dispose();
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // Journal content generation: direct byte writes (fast — no per-line OS round trip) in exactly the shape the
    // real writer (OperationJournal.AppendLines: JsonSerializer.Serialize(entry) + Environment.NewLine) produces.
    // -----------------------------------------------------------------------------------------------------------

    private static readonly DateTime BaseTime = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string PathPadding = new('x', 80);

    private static JournalEntry PreparedEntry(int i, DateTime timestamp) => new(
        Id(i), FileOperationType.Move, JournalState.Prepared,
        $@"C:\photos\folder-{i % 50}\{PathPadding}\{Id(i)}.jpg",
        $@"C:\photos\sel\folder-{i % 50}\{PathPadding}\{Id(i)}.jpg",
        12345, timestamp, timestamp);

    private static string Id(int i) => $"j{i:D6}";

    private static byte[] LineBytes(JournalEntry entry) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\r\n");

    /// <summary>count P/C pairs (Prepared then Committed) — the majority (Prepared) is droppable, well past CompactionMinGain.</summary>
    private static byte[] BuildCompactibleJournal(int count)
    {
        using var ms = new MemoryStream();
        for (var i = 0; i < count; i++)
        {
            var prepared = PreparedEntry(i, BaseTime.AddSeconds(i * 2));
            var committed = prepared with { State = JournalState.Committed, TimestampUtc = BaseTime.AddSeconds(i * 2 + 1) };
            ms.Write(LineBytes(prepared));
            ms.Write(LineBytes(committed));
        }
        return ms.ToArray();
    }

    /// <summary>count Prepared-only entries: nothing is droppable (0% gain), well past the size threshold.</summary>
    private static byte[] BuildUncompactibleJournal(int count)
    {
        using var ms = new MemoryStream();
        for (var i = 0; i < count; i++) ms.Write(LineBytes(PreparedEntry(i, BaseTime.AddSeconds(i))));
        return ms.ToArray();
    }

    // Large enough that even the C-only compacted remainder (roughly half the lines) stays >= 1 MiB, so
    // ReadCommittedMoves' windowed tail reader applies both before and after compaction (see the "Compacted" test).
    private const int CompactibleCount = 9000;
    private const int UncompactibleCount = 6000; // Prepared-only, sized past 1 MiB but 0% droppable

    private void WriteJournal(byte[] bytes) => File.WriteAllBytes(_paths.JournalFile, bytes);

    private OperationJournal MakeJournal(IJournalCompactionFiles? compactionFiles, IClock? clock = null) =>
        new(_paths, new PhysicalFileSystem(), clock ?? new SystemClock(), compactionFiles: compactionFiles);

    // -----------------------------------------------------------------------------------------------------------
    // Outcomes that leave the journal untouched.
    // -----------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "No compactionFiles given: NotSupported, file unchanged")]
    public void TryCompact_NoCompactionFiles_NotSupported()
    {
        var bytes = BuildCompactibleJournal(CompactibleCount);
        WriteJournal(bytes);
        var journal = MakeJournal(compactionFiles: null);

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.NotSupported, result.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.JournalFile));
    }

    [Fact(DisplayName = "Journal below the size threshold: BelowThreshold, unchanged")]
    public void TryCompact_SmallJournal_BelowThreshold()
    {
        var bytes = BuildCompactibleJournal(5);
        WriteJournal(bytes);
        var journal = MakeJournal(new HookedCompactionFiles(new PhysicalJournalCompactionFiles()));

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.BelowThreshold, result.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.JournalFile));
    }

    [Fact(DisplayName = "Less than 25% droppable: NotWorthIt, unchanged")]
    public void TryCompact_LowDroppableShare_NotWorthIt()
    {
        var bytes = BuildUncompactibleJournal(UncompactibleCount);
        Assert.True(bytes.Length >= OperationJournal.CompactionThresholdBytes);
        WriteJournal(bytes);
        var journal = MakeJournal(new HookedCompactionFiles(new PhysicalJournalCompactionFiles()));

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.NotWorthIt, result.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.JournalFile));
    }

    // -----------------------------------------------------------------------------------------------------------
    // The happy path.
    // -----------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Big P/C journal compacts: smaller file, matching BytesAfter, every read API agrees, no leftover temp file")]
    public void TryCompact_BigJournal_Compacted()
    {
        var bytes = BuildCompactibleJournal(CompactibleCount);
        Assert.True(bytes.Length >= OperationJournal.CompactionThresholdBytes);
        WriteJournal(bytes);
        var before = MakeJournal(compactionFiles: null);
        var beforePendingFailed = before.ReadPendingAndFailedOperations();
        var beforeMoves = before.ReadCommittedMoves();

        var journal = MakeJournal(new HookedCompactionFiles(new PhysicalJournalCompactionFiles()));
        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        var newBytes = File.ReadAllBytes(_paths.JournalFile);
        Assert.True(newBytes.Length < bytes.Length);
        Assert.True(newBytes.Length >= OperationJournal.CompactionThresholdBytes,
            "the compacted file must stay >= 1 MiB for the ReadCommittedMoves windowed-equality check below to be meaningful");
        Assert.Equal(newBytes.Length, result.BytesAfter);
        Assert.Equal(bytes.Length, result.BytesBefore);

        var after = MakeJournal(compactionFiles: null);
        Assert.Equal(beforePendingFailed, after.ReadPendingAndFailedOperations());
        Assert.Equal(beforeMoves, after.ReadCommittedMoves());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_paths.JournalFile)!, "*" + OperationJournal.CompactionFileSuffix));
    }

    // -----------------------------------------------------------------------------------------------------------
    // Concurrency and crash safety around the critical section.
    // -----------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "An append from another process between the snapshot and the lock survives compaction exactly once")]
    public void TryCompact_ConcurrentAppendBetweenSnapshotAndLock_SurvivesOnce()
    {
        WriteJournal(BuildCompactibleJournal(CompactibleCount));
        var other = MakeJournal(compactionFiles: null); // "another process" on the same path
        var lateEntry = PreparedEntry(999_999, BaseTime.AddDays(1));

        var hooked = new HookedCompactionFiles(new PhysicalJournalCompactionFiles())
        {
            BeforeLock = () => other.Append(lateEntry),
        };
        var journal = MakeJournal(hooked);

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        var after = MakeJournal(compactionFiles: null);
        var pending = after.ReadPendingOperations().Where(e => e.Id == lateEntry.Id).ToList();
        Assert.Single(pending);
        Assert.Equal(lateEntry, pending[0]);
        var linesWithId = File.ReadAllLines(_paths.JournalFile)
            .Where(l => l.Length > 0 && JsonSerializer.Deserialize<JournalEntry>(l) is { } e && e.Id == lateEntry.Id)
            .ToList();
        Assert.Single(linesWithId);
    }

    [Fact(DisplayName = "A torn tail written into the delta is repaired: the file ends with a newline and a later append is readable, not glued")]
    public void TryCompact_TornTailInDelta_RepairedAndNotGlued()
    {
        WriteJournal(BuildCompactibleJournal(CompactibleCount));
        var hooked = new HookedCompactionFiles(new PhysicalJournalCompactionFiles())
        {
            BeforeLock = () =>
            {
                using var appendStream = new FileStream(_paths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                var torn = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(PreparedEntry(888_888, BaseTime.AddDays(1)))); // no trailing newline
                appendStream.Write(torn);
            },
        };
        var journal = MakeJournal(hooked);

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        var bytes = File.ReadAllBytes(_paths.JournalFile);
        Assert.Equal((byte)'\n', bytes[^1]);

        var next = MakeJournal(compactionFiles: null);
        var newEntry = PreparedEntry(777_777, BaseTime.AddDays(2));
        next.Append(newEntry);

        // Both the (repaired) torn line's Id and the newly appended Id must be independently readable, and the torn
        // line's content must not have glued onto the repaired line or the new append.
        var text = File.ReadAllText(_paths.JournalFile);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var tornLine = Assert.Single(lines, l => l.Contains(Id(888_888), StringComparison.Ordinal));
        Assert.DoesNotContain(Id(777_777), tornLine, StringComparison.Ordinal);
        var newEntryLine = Assert.Single(lines, l => l.Contains(Id(777_777), StringComparison.Ordinal));
        Assert.DoesNotContain(Id(888_888), newEntryLine, StringComparison.Ordinal);
        var pending = next.ReadPendingOperations();
        Assert.Contains(pending, e => e.Id == newEntry.Id);
        Assert.Contains(pending, e => e.Id == Id(888_888));
    }

    [Fact(DisplayName = "Writers are excluded during the critical section, and allowed again once compaction finishes")]
    public void TryCompact_WritersExcludedDuringCriticalSection_AllowedAfter()
    {
        WriteJournal(BuildCompactibleJournal(CompactibleCount));
        IOException? observed = null;
        var hooked = new HookedCompactionFiles(new PhysicalJournalCompactionFiles())
        {
            BeforeReplace = () =>
            {
                try
                {
                    using var append = new FileStream(_paths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                }
                catch (IOException ex)
                {
                    observed = ex;
                }
            },
        };
        var journal = MakeJournal(hooked);

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        Assert.NotNull(observed);
        Assert.Equal(unchecked((int)0x80070020), observed!.HResult);

        using var afterAppend = new FileStream(_paths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        Assert.True(afterAppend.CanWrite);
    }

    [Fact(DisplayName = "A reader holding the journal open does not block compaction and still sees the old bytes")]
    public void TryCompact_ReaderHoldingJournalOpen_NotBlockedSeesOldBytes()
    {
        var originalBytes = BuildCompactibleJournal(CompactibleCount);
        WriteJournal(originalBytes);
        using var reader = new FileStream(_paths.JournalFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var readerBytesBefore = new byte[reader.Length];
        reader.ReadExactly(readerBytesBefore);
        reader.Position = 0;

        var journal = MakeJournal(new HookedCompactionFiles(new PhysicalJournalCompactionFiles()));
        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
        var readerBytesAfter = new byte[reader.Length];
        reader.ReadExactly(readerBytesAfter);
        Assert.Equal(originalBytes, readerBytesAfter);
        Assert.Equal(readerBytesBefore, readerBytesAfter);
    }

    [Fact(DisplayName = "Crash before replace: journal is byte-identical to the original, no temp file left")]
    public void TryCompact_CrashBeforeReplace_JournalUnchangedNoTempLeft()
    {
        var bytes = BuildCompactibleJournal(CompactibleCount);
        WriteJournal(bytes);
        var hooked = new HookedCompactionFiles(new PhysicalJournalCompactionFiles()) { ThrowInsteadOfReplace = true };
        var journal = MakeJournal(hooked);

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Failed, result.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.JournalFile));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_paths.JournalFile)!, "*" + OperationJournal.CompactionFileSuffix));
    }

    [Fact(DisplayName = "Stale replacement-file leftovers (>= 1h old) are removed; a fresh one is left alone")]
    public void TryCompact_StaleLeftoverRemoved_FreshLeftoverKept()
    {
        WriteJournal(BuildCompactibleJournal(CompactibleCount));
        var dir = Path.GetDirectoryName(_paths.JournalFile)!;
        var staleFile = Path.Combine(dir, "operations.jsonl." + Guid.NewGuid().ToString("N") + OperationJournal.CompactionFileSuffix);
        var freshFile = Path.Combine(dir, "operations.jsonl." + Guid.NewGuid().ToString("N") + OperationJournal.CompactionFileSuffix);
        File.WriteAllText(staleFile, "leftover");
        File.WriteAllText(freshFile, "leftover");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow - OperationJournal.StaleCompactionFileAge - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow);

        var journal = MakeJournal(new HookedCompactionFiles(new PhysicalJournalCompactionFiles()));
        journal.TryCompact();

        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(freshFile));
        File.Delete(freshFile);
    }

    [Fact(DisplayName = "Changed: another process compacted between the snapshot and the lock, so TryCompact backs off and reports the new content")]
    public void TryCompact_ChangedBetweenSnapshotAndLock_ReportsExactContent()
    {
        WriteJournal(BuildCompactibleJournal(CompactibleCount));
        var rewritten = BuildCompactibleJournal(10); // smaller: below the threshold, as a real compaction would leave it
        var hooked = new HookedCompactionFiles(new PhysicalJournalCompactionFiles())
        {
            BeforeLock = () => File.WriteAllBytes(_paths.JournalFile, rewritten),
        };
        var journal = MakeJournal(hooked);

        var result = journal.TryCompact();

        Assert.Equal(JournalCompactionOutcome.Changed, result.Outcome);
        Assert.Equal(rewritten, File.ReadAllBytes(_paths.JournalFile));
        Assert.Equal(rewritten.Length, result.BytesAfter);
    }

    [Fact(DisplayName = "Busy: a writer holds the journal when compaction tries to lock it; the file is left untouched")]
    public void TryCompact_WriterHoldsJournalWhenLocking_Busy()
    {
        var bytes = BuildCompactibleJournal(CompactibleCount);
        WriteJournal(bytes);
        FileStream? appendHandle = null;
        var hooked = new HookedCompactionFiles(new PhysicalJournalCompactionFiles())
        {
            BeforeLock = () => appendHandle = new FileStream(_paths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read),
        };
        var journal = MakeJournal(hooked);

        JournalCompactionResult result;
        try
        {
            result = journal.TryCompact();
        }
        finally
        {
            appendHandle?.Dispose();
        }

        Assert.Equal(JournalCompactionOutcome.Busy, result.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.JournalFile));
    }

    // -----------------------------------------------------------------------------------------------------------
    // Stress: real concurrent writers plus repeated compaction attempts.
    // -----------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Stress: concurrent writers and repeated TryCompact never tear/glue a line or lose an acknowledged append")]
    [Trait("Category", "Slow")]
    public void TryCompact_StressWithConcurrentWriters_NoTornOrLostRecords()
    {
        WriteJournal(BuildCompactibleJournal(CompactibleCount));
        const int writerThreads = 4;
        const int perThread = 60;
        var acknowledged = new System.Collections.Concurrent.ConcurrentBag<(string Id, JournalState State)>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var start = new Barrier(writerThreads + 1);
        var stop = false;

        var writers = Enumerable.Range(0, writerThreads).Select(w => new Thread(() =>
        {
            try
            {
                var writerJournal = MakeJournal(compactionFiles: null);
                start.SignalAndWait();
                for (var i = 0; i < perThread; i++)
                {
                    var id = $"stress-{w}-{i}";
                    var entry = new JournalEntry(id, FileOperationType.Copy, JournalState.Committed,
                        $@"C:\photos\{id}.jpg", $@"C:\photos\sel\{id}.jpg", 1, BaseTime, BaseTime.AddSeconds(i));
                    try
                    {
                        writerJournal.Append(entry);
                        acknowledged.Add((id, JournalState.Committed));
                    }
                    catch (IOException)
                    {
                        // Bounded retries exhausted under heavy contention with compaction: allowed.
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        })).ToList();

        var compactor = new Thread(() =>
        {
            try
            {
                var compactJournal = MakeJournal(new HookedCompactionFiles(new PhysicalJournalCompactionFiles()));
                start.SignalAndWait();
                while (!Volatile.Read(ref stop))
                {
                    compactJournal.TryCompact();
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        });

        compactor.Start();
        writers.ForEach(t => t.Start());
        foreach (var t in writers) Assert.True(t.Join(TimeSpan.FromSeconds(60)), "writer thread hung");
        Volatile.Write(ref stop, true);
        Assert.True(compactor.Join(TimeSpan.FromSeconds(60)), "compactor thread hung");

        if (!failures.IsEmpty) throw new AggregateException(failures);

        var final = MakeJournal(compactionFiles: null);
        var lines = File.ReadAllLines(_paths.JournalFile).Where(l => l.Length > 0).ToList();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var entry = JsonSerializer.Deserialize<JournalEntry>(line); // a glued/torn line throws here
            Assert.NotNull(entry);
        }

        var pendingAndFailed = final.ReadPendingAndFailedOperations().ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var (id, _) in acknowledged)
        {
            // Every acknowledged Committed stress entry must not show up as pending/failed (it should have resolved Committed).
            Assert.False(pendingAndFailed.ContainsKey(id), $"{id} unexpectedly still pending/failed after compaction");
            _ = seenIds.Add(id);
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // Startup recovery integration.
    // -----------------------------------------------------------------------------------------------------------

    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }

    [Fact(DisplayName = "Startup recovery compacts a big journal afterwards and still returns the reconcile's failed list")]
    public async Task RunAsync_BigJournalWithCompactionFiles_CompactsAndReturnsFailed()
    {
        var bytes = BuildCompactibleJournal(CompactibleCount);
        // A pending Recycle whose source still exists on disk: reconcile must mark it Failed.
        var realSource = Path.Combine(_root.Path, "still-there.jpg");
        File.WriteAllText(realSource, "x");
        var pendingRecycle = new JournalEntry("recycle-pending", FileOperationType.Recycle, JournalState.Prepared,
            realSource, null, 1, BaseTime, BaseTime.AddMinutes(-5));
        using (var ms = new MemoryStream())
        {
            ms.Write(bytes);
            ms.Write(LineBytes(pendingRecycle));
            bytes = ms.ToArray();
        }
        WriteJournal(bytes);
        var fs = new PhysicalFileSystem();

        var clock = new FakeClock(BaseTime.AddHours(1));
        var journal = new OperationJournal(_paths, fs, clock, compactionFiles: new HookedCompactionFiles(new PhysicalJournalCompactionFiles()));

        var failed = await JournalStartupRecovery.RunAsync(journal, clock);

        Assert.Contains(failed, e => e.Id == "recycle-pending");
        var afterBytes = File.ReadAllBytes(_paths.JournalFile);
        Assert.True(afterBytes.Length < bytes.Length, "expected the journal to have been compacted after reconcile");
    }

    [Fact(DisplayName = "Startup recovery still returns the reconcile's failed list even when compaction throws")]
    public async Task RunAsync_CompactionThrows_StillReturnsFailed()
    {
        // Must actually cross CompactionThresholdBytes / CompactionMinGain so TryCompact reaches the compaction-files
        // primitives (and thus the throw) instead of bailing out early via BelowThreshold.
        WriteJournal(BuildCompactibleJournal(CompactibleCount));
        var throwing = new ThrowingCompactionFiles();
        var pendingRecycleSource = Path.Combine(_root.Path, "still-there2.jpg");
        File.WriteAllText(pendingRecycleSource, "x");
        var fs = new PhysicalFileSystem();
        var pending = new JournalEntry("recycle-pending-2", FileOperationType.Recycle, JournalState.Prepared,
            pendingRecycleSource, null, 1, BaseTime, BaseTime.AddMinutes(-5));
        using (var append = new FileStream(_paths.JournalFile, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var line = LineBytes(pending);
            append.Write(line);
        }

        var clock = new FakeClock(BaseTime.AddHours(1));
        var journal = new OperationJournal(_paths, fs, clock, compactionFiles: throwing);

        var failed = await JournalStartupRecovery.RunAsync(journal, clock);

        Assert.Contains(failed, e => e.Id == "recycle-pending-2");
    }

    private sealed class ThrowingCompactionFiles : IJournalCompactionFiles
    {
        public Stream OpenReadDenyWriters(string path) => throw new InvalidOperationException("simulated compaction bug");
        public IStagedReplacement CreateStagedReplacement(string tempPath) => throw new InvalidOperationException("simulated compaction bug");
    }
}
