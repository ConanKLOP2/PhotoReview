using System.Diagnostics;
using System.Text.Json;
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

    private static Thread Background(ThreadStart body) => new(body) { IsBackground = true }; // a hung worker must not outlive the test host

    /// <summary>Bounded barrier wait: a participant that died before signalling fails the others instead of blocking them forever.</summary>
    private static void Rendezvous(Barrier barrier)
    {
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(20))) throw new TimeoutException("barrier participants did not all arrive within 20 s");
    }

    /// <summary>
    /// Waits for the threads, failing only when NOBODY makes progress for 30 s (a real hang). The run itself takes as long as the
    /// machine's CPU share makes it: a fixed total budget would again measure the scheduler. On a hang the retry loops are stopped.
    /// </summary>
    private static void JoinWhileProgressing(IReadOnlyList<Thread> threads, Func<int> progress, CancellationTokenSource stop, string what)
    {
        var last = -1;
        var lastChange = Stopwatch.GetTimestamp();
        try
        {
            foreach (var thread in threads)
            {
                while (!thread.Join(TimeSpan.FromSeconds(1)))
                {
                    var now = progress();
                    if (now != last) { last = now; lastChange = Stopwatch.GetTimestamp(); }
                    else Assert.True(Stopwatch.GetTimestamp() - lastChange < 30 * Stopwatch.Frequency, $"{what} made no progress for 30 s (hung)");
                }
            }
        }
        catch
        {
            stop.Cancel(); // do not leave a retry loop spinning behind a failed test
            throw;
        }
    }

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
        var refusals = 0;
        using var stop = new CancellationTokenSource();

        var threads = Enumerable.Range(0, writers).Select(w => Background(Captured(() =>
        {
            // One instance per "process": its lock does not serialize the others, only the file sharing does.
            var journal = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock());
            Rendezvous(start);
            for (var i = 0; i < perWriter; i++)
            {
                var id = $"w{w}-{i}";
                // The journal's own retry budget is a COUNT of attempts (OperationJournal.AppendAttempts, ~100 ms of sleeps), not
                // a promise: a writer that is descheduled while it holds the file makes the others exhaust it (production then
                // reports a journal error and the caller retries). How many appends survive that is CPU scheduling, so it is not
                // a pass/fail condition (it flaked at 72..118 of 240 under background load). Like the production caller, retry the
                // refused append; every refusal must leave no half-written record (checked below), and each record is
                // acknowledged exactly once.
                while (true)
                {
                    try
                    {
                        journal.Append(Entry(id));
                        acknowledged.Add(id);
                        break;
                    }
                    catch (IOException) when (!stop.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref refusals);
                        Thread.Yield();
                    }
                }
            }
        }))).ToList();
        threads.ForEach(t => t.Start());
        JoinWhileProgressing(threads, () => acknowledged.Count, stop, "writer threads");
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
        // Nothing lost, nothing invented: every record every writer was told is stored is exactly the set on disk.
        // (refusals = how often the retry budget was exhausted: scheduling-dependent, reported for information only.)
        Assert.True(writers * perWriter == acknowledged.Count, $"{acknowledged.Count}/{writers * perWriter} acknowledged ({Volatile.Read(ref refusals)} refused attempts)");
        Assert.Equal(acknowledged.Count, ids.Count);
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
        using var stop = new CancellationTokenSource();
        var committed =new System.Collections.Concurrent.ConcurrentBag<string>();
        var commit = Background(Captured(() =>
        {
            Rendezvous(start);
            foreach (var prepared in entries)
            {
                File.WriteAllText(prepared.Destination!, "x"); // the copy completes, then Committed is journaled
                while (true)
                {
                    try
                    {
                        owner.Append(prepared with { State = JournalState.Committed });
                        committed.Add(prepared.Id);
                        break;
                    }
                    catch (IOException) when (!stop.IsCancellationRequested)
                    {
                        // The journal's bounded (count-based) append retries ran out while the reconciler held the file; on a
                        // starved machine that is scheduling, not a defect, so retry like the production caller instead of
                        // asserting how many got through at the first attempt.
                        Thread.Yield();
                    }
                }
            }
        }));
        var reconcile = Background(Captured(() =>
        {
            Rendezvous(start);
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
        JoinWhileProgressing([commit, reconcile], () => committed.Count, stop, "commit/reconcile threads");
        RethrowThreadFailures();

        // Reconcile may have judged an operation Failed just before its destination appeared; whoever won each race, the
        // copy really completed, so every operation whose Committed was acknowledged must end Committed (FA-01).
        var notCommitted = owner.ReadFailedOperations().Concat(owner.ReadPendingOperations()).Select(e => e.Id).ToHashSet();
        foreach (var id in committed) Assert.DoesNotContain(id, notCommitted);
        Assert.Equal(operations, committed.Count);
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
            Rendezvous(start);
            var result = await service.ExecuteAsync(new FileActionRequest($@"C:\photos\a{i}.jpg", FileOperationType.Move, "sel"));
            if (result.Rejected && Interlocked.Increment(ref rejected) == 7) allLosersTurnedAway.TrySetResult();
            return result;
        })).ToList();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the winner is inside its Move
        // Hold the winner until every other action has been rejected: releasing earlier would let a slow starter
        // find the gate free and succeed (the test then fails on a loaded machine).
        try { await allLosersTurnedAway.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { release.TrySetResult(); } // never leave the winner parked in its Move when the wait above times out
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
