using System.IO;
using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Barrier-released contention on one journal file and on the single-action gate.</summary>
public sealed class JournalConcurrencyTests : IDisposable
{
    private readonly TempRoot _root = new("journal-concurrency");

    public void Dispose() => _root.Dispose();

    private static JournalEntry Entry(string id, JournalState state = JournalState.Prepared) => new(
        id, FileOperationType.Copy, state, $@"C:\photos\{id}.jpg", $@"C:\photos\sel\{id}.jpg", 1, DateTime.UnixEpoch, DateTime.UnixEpoch);

    private readonly System.Collections.Concurrent.ConcurrentQueue<System.Runtime.ExceptionServices.ExceptionDispatchInfo> _threadFailures = new();

    /// <summary>
    /// An exception escaping a raw <see cref="Thread"/> kills the whole test host (every test in the run is lost, as seen
    /// once with a journal file-lock IOException); capture it and rethrow it on the test thread instead.
    /// </summary>
    private ThreadStart Captured(Action body) => () =>
    {
        try { body(); }
        catch (Exception ex) { _threadFailures.Enqueue(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex)); }
    };

    private void RethrowThreadFailures()
    {
        if (_threadFailures.TryDequeue(out var first)) first.Throw();
    }

    [Fact(DisplayName = "Many journal instances (processes) appending at once never corrupt or lose an acknowledged record")]
    public void ManyWritersOneFile_NoTornOrLostAcknowledgedRecords()
    {
        const int writers = 6;
        const int perWriter = 40;
        var paths = new AppPaths(_root.Path);
        var start = new Barrier(writers);
        var acknowledged = new System.Collections.Concurrent.ConcurrentBag<string>();

        var threads = Enumerable.Range(0, writers).Select(w => new Thread(Captured(() =>
        {
            // One instance per "process": its lock does not serialize the others, only the file sharing does.
            var journal = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock());
            start.SignalAndWait();
            for (var i = 0; i < perWriter; i++)
            {
                var id = $"w{w}-{i}";
                try
                {
                    journal.Append(Entry(id));
                    acknowledged.Add(id);
                }
                catch (IOException)
                {
                    // Bounded retries exhausted under heavy contention: allowed, but the record must then not exist half-written.
                }
            }
        }))).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        RethrowThreadFailures();

        var lines = File.ReadAllLines(paths.JournalFile).Where(l => l.Length > 0).ToList();
        var ids = new HashSet<string>();
        foreach (var line in lines)
        {
            var entry = JsonSerializer.Deserialize<JournalEntry>(line); // a glued/torn line throws here
            Assert.NotNull(entry);
            Assert.True(ids.Add(entry.Id), $"record {entry.Id} was written twice");
        }
        foreach (var id in acknowledged) Assert.Contains(id, ids);
        Assert.Equal(ids.Count, lines.Count);
        Assert.True(acknowledged.Count >= writers * perWriter / 2, $"only {acknowledged.Count}/{writers * perWriter} appends got through the retry budget");
    }

    [Fact(DisplayName = "Concurrent reconcile and append on two instances leave every record parseable and no false Failed after Committed")]
    public void ReconcileWhileOtherProcessCommits_LatestStateStaysCommitted()
    {
        const int operations = 30;
        var paths = new AppPaths(_root.Path);
        var fs = new PhysicalFileSystem();
        var clock = new SystemClock();
        var owner = new OperationJournal(paths, fs, clock);
        var reconciler = new OperationJournal(paths, fs, clock);
        var photoDir = _root.Dir("photos");
        var selDir = _root.Dir("sel");
        var entries = new List<JournalEntry>();
        for (var i = 0; i < operations; i++)
        {
            var source = Path.Combine(photoDir, $"{i}.jpg");
            var destination = Path.Combine(selDir, $"{i}.jpg");
            File.WriteAllText(source, "x");
            var prepared = new JournalEntry($"c{i}", FileOperationType.Copy, JournalState.Prepared, source, destination, 1, DateTime.UnixEpoch, DateTime.UnixEpoch);
            owner.Append(prepared);
            entries.Add(prepared);
        }

        var start = new Barrier(2);
        var committed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var commit = new Thread(Captured(() =>
        {
            start.SignalAndWait();
            foreach (var prepared in entries)
            {
                File.WriteAllText(prepared.Destination!, "x"); // the copy completes, then Committed is journaled
                try
                {
                    owner.Append(prepared with { State = JournalState.Committed });
                    committed.Add(prepared.Id);
                }
                catch (IOException)
                {
                    // Bounded append retries exhausted while the reconciler held the file (a loaded machine): production
                    // reports this as a journal error, the operation stays Prepared or is reconciled; not asserted below.
                }
            }
        }));
        var reconcile = new Thread(Captured(() =>
        {
            start.SignalAndWait();
            try
            {
                reconciler.ReconcilePendingOperations();
            }
            catch (IOException)
            {
                // Same bounded-retry outcome on the reconciler side; JournalStartupRecovery catches it in production.
            }
        }));
        commit.Start();
        reconcile.Start();
        commit.Join();
        reconcile.Join();
        RethrowThreadFailures();

        // Reconcile may have judged an operation Failed just before its destination appeared; whoever won each race, the
        // copy really completed, so every operation whose Committed was acknowledged must end Committed (FA-01).
        var notCommitted = owner.ReadFailedOperations().Concat(owner.ReadPendingOperations()).Select(e => e.Id).ToHashSet();
        foreach (var id in committed) Assert.DoesNotContain(id, notCommitted);
        Assert.True(committed.Count >= operations / 2, $"only {committed.Count}/{operations} Committed appends got through the retry budget");
    }

    [Fact(DisplayName = "The single-action gate admits exactly one of many simultaneous actions; the rest are rejected as busy")]
    public async Task ManySimultaneousActions_ExactlyOneRuns()
    {
        var disk = new InMemoryFileSystem();
        var paths = new AppPaths(@"C:\Users\test\AppData\Local");
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var journal = new OperationJournal(paths, disk, new SystemClock());
        var running = 0;
        var maxRunning = 0;
        var service = new FileActionService(journal, disk, new SystemClock(), new NoBin(), moveOverride: async (from, to) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref maxRunning)) < now && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen) { }
            entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref running);
            disk.Move(from, to);
        });
        for (var i = 0; i < 8; i++) disk.AddFile($@"C:\photos\a{i}.jpg", "x");

        var start = new Barrier(8);
        var rejected = 0;
        var allLosersTurnedAway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            start.SignalAndWait();
            var result = await service.ExecuteAsync(new FileActionRequest($@"C:\photos\a{i}.jpg", FileOperationType.Move, "sel"));
            if (result.Rejected && Interlocked.Increment(ref rejected) == 7) allLosersTurnedAway.TrySetResult();
            return result;
        })).ToList();

        await entered.Task; // the winner is inside its Move
        // Hold the winner until every other action has been rejected: releasing earlier would let a slow starter
        // find the gate free and succeed (the test then fails on a loaded machine).
        await allLosersTurnedAway.Task.WaitAsync(TimeSpan.FromSeconds(30));
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(7, results.Count(r => r.Rejected));
        Assert.Equal(1, maxRunning);
        Assert.False(service.IsBusy);
    }

    private sealed class NoBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}
