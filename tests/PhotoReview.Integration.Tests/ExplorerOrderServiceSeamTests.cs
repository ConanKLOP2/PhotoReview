using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Platform.Windows.Explorer;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// ExplorerOrderService pump/timeout/cancellation/prefetch behaviour with a fake query (no COM, no Explorer).
/// Coordination uses events and cancellation tokens only; the single wall-clock use is a generous hang guard.
/// </summary>
public sealed class ExplorerOrderServiceSeamTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "photo-review-seam");

    private sealed class MemoryLog : ILog
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];
        public bool Enabled => true;
        public List<string> Messages { get { lock (_gate) return [.. _messages]; } }
        public void Info(string message) { lock (_gate) _messages.Add("INFO: " + message); }
        public void Warn(string message) { lock (_gate) _messages.Add("WARN: " + message); }
        public void Error(string message, Exception? ex = null) { lock (_gate) _messages.Add($"ERROR: {message} {ex?.Message}"); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
    }

    private static ExplorerViewSnapshot Available(string folder, params string[] names) =>
        new(folder, names.Select(n => Path.Combine(folder, n)).ToArray(), [], ExplorerGroupState.None,
            ExplorerOrderStatus.Available, null, DateTime.UtcNow);

    /// <summary>Blocks until <paramref name="token"/> is cancelled. The service disposes its linked source once the caller has been answered, and a disposed source's wait handle throws: that only happens after cancellation, so it also means "cancelled".</summary>
    private static void Block(CancellationToken token)
    {
        try { token.WaitHandle.WaitOne(); }
        catch (ObjectDisposedException) { }
    }

    private static string Folder(string name) => Path.Combine(Root, name);

    private static ExplorerOrderService Create(ExplorerOrderService.ExplorerQuery query, ILog? log = null, TimeProvider? time = null, TimeSpan? maxAge = null)
        => new(log, query, time, maxAge);

    private static ExplorerViewSnapshot Quick(string folder, IProgress<ExplorerQueryProgress>? _, int __, CancellationToken ___) => Available(folder, "a.jpg");

    [Fact]
    public async Task QueryResultAndProgressArgumentsArePassedThrough()
    {
        var seenBatch = new List<int>();
        using var service = Create((folder, _, batch, _) => { lock (seenBatch) seenBatch.Add(batch); return Available(folder, "a.jpg"); });

        var full = await service.TryGetSnapshotAsync(Folder("x") + @"\", Long, CancellationToken.None);
        await service.TryGetSnapshotProgressiveAsync(Folder("x"), Long, batchSize: 0);
        await service.TryGetSnapshotProgressiveAsync(Folder("x"), Long, batchSize: 100000);
        await service.TryGetSnapshotProgressiveAsync(Folder("x"), Long, batchSize: 40);

        Assert.Equal(ExplorerOrderStatus.Available, full.Status);
        Assert.Equal(Folder("x"), full.Folder, ignoreCase: true);
        Assert.Equal([int.MaxValue, 1, 128, 40], seenBatch);
    }

    [Fact(DisplayName = "Timeout: caller gets TimedOut, the running query sees its token cancelled, and the pump is free for the next query")]
    public async Task TimeoutReleasesCallerCancelsWorkAndFreesPump()
    {
        var sawCancel = new ManualResetEventSlim();
        var calls = 0;
        using var service = Create((folder, _, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Block(token);
                sawCancel.Set();
                token.ThrowIfCancellationRequested();
            }
            return Available(folder, "a.jpg");
        });

        var timedOut = await service.TryGetSnapshotAsync(Folder("t"), TimeSpan.Zero, CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal(ExplorerOrderStatus.TimedOut, timedOut.Status);
        Assert.Equal(ExplorerReason.Timeout, timedOut.Reason);
        Assert.Empty(timedOut.OrderedPaths);

        Assert.True(sawCancel.Wait(HangGuard), "the abandoned query never observed cancellation");
        var next = await service.TryGetSnapshotAsync(Folder("t"), Long, CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal(ExplorerOrderStatus.Available, next.Status);
    }

    [Fact]
    public async Task NegativeTimeoutMeansExpiredNotException()
    {
        using var service = Create((folder, _, _, token) => { Block(token); token.ThrowIfCancellationRequested(); return Available(folder); });

        var snapshot = await service.TryGetSnapshotAsync(Folder("neg"), TimeSpan.FromSeconds(-5), CancellationToken.None).WaitAsync(HangGuard);

        Assert.Equal(ExplorerOrderStatus.TimedOut, snapshot.Status);
    }

    [Fact(DisplayName = "Cancellation while the query runs returns Canceled (not TimedOut) and cancels the work")]
    public async Task CancellationMidQueryReturnsCanceled()
    {
        var started = new ManualResetEventSlim();
        var sawCancel = new ManualResetEventSlim();
        using var service = Create((folder, _, _, token) =>
        {
            started.Set();
            Block(token);
            sawCancel.Set();
            token.ThrowIfCancellationRequested();
            return Available(folder);
        });
        using var cts = new CancellationTokenSource();

        var pending = service.TryGetSnapshotAsync(Folder("c"), Long, cts.Token);
        Assert.True(started.Wait(HangGuard));
        cts.Cancel();
        var snapshot = await pending.WaitAsync(HangGuard);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
        Assert.Equal(ExplorerReason.Canceled, snapshot.Reason);
        Assert.True(sawCancel.Wait(HangGuard));
    }

    [Fact]
    public async Task AlreadyCancelledTokenNeverStartsTheQuery()
    {
        var calls = 0;
        using var service = Create((folder, _, _, _) => { Interlocked.Increment(ref calls); return Available(folder); });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var snapshot = await service.TryGetSnapshotAsync(Folder("pre"), Long, cts.Token);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task QueryThatThrowsIsReportedAsFailedAndThePumpSurvives()
    {
        var calls = 0;
        var log = new MemoryLog();
        using var service = Create((folder, _, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("boom");
            return Available(folder, "a.jpg");
        }, log);

        var failed = await service.TryGetSnapshotAsync(Folder("f"), Long, CancellationToken.None);
        var recovered = await service.TryGetSnapshotAsync(Folder("f"), Long, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Failed, failed.Status);
        Assert.Equal("query-failed|IOException", failed.Reason);
        Assert.Contains(log.Messages, m => m.Contains("native view query failed", StringComparison.Ordinal));
        Assert.Equal(ExplorerOrderStatus.Available, recovered.Status);
    }

    [Fact]
    public async Task QueryThatCancelsItselfWithoutAnExternalCancelIsCanceled()
    {
        using var service = Create((_, _, _, _) => throw new OperationCanceledException());

        var snapshot = await service.TryGetSnapshotAsync(Folder("oc"), Long, CancellationToken.None);

        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
    }

    [Fact(DisplayName = "All queries run one at a time on a single STA thread")]
    public async Task QueriesAreSerializedOnOneStaThread()
    {
        var active = 0;
        var maxActive = 0;
        var threads = new HashSet<int>();
        var apartments = new HashSet<ApartmentState>();
        using var service = Create((folder, _, _, _) =>
        {
            var now = Interlocked.Increment(ref active);
            lock (threads) { maxActive = Math.Max(maxActive, now); threads.Add(Environment.CurrentManagedThreadId); apartments.Add(Thread.CurrentThread.GetApartmentState()); }
            Thread.SpinWait(2000);
            Interlocked.Decrement(ref active);
            return Available(folder, "a.jpg");
        });

        var all = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => service.TryGetSnapshotAsync(Folder("s" + i), Long, CancellationToken.None)))).WaitAsync(HangGuard);

        Assert.All(all, s => Assert.Equal(ExplorerOrderStatus.Available, s.Status));
        Assert.Equal(1, maxActive);
        Assert.Single(threads);
        Assert.Equal([ApartmentState.STA], apartments);
    }

    [Fact(DisplayName = "Prefetch for the same folder (any spelling) is joined once, then the next request queries afresh")]
    public async Task PrefetchIsJoinedOnceRegardlessOfPathSpelling()
    {
        var calls = new List<string>();
        using var service = Create((folder, _, _, _) => { lock (calls) calls.Add(folder); return Available(folder, "a.jpg"); });

        service.Prefetch(Folder("P"), Long);
        var joined = await service.TryGetSnapshotProgressiveAsync(Folder("p") + @"\", Long).WaitAsync(HangGuard);
        Assert.Equal(ExplorerOrderStatus.Available, joined.Status);
        Assert.Single(calls);

        await service.TryGetSnapshotProgressiveAsync(Folder("p"), Long).WaitAsync(HangGuard);
        Assert.Equal(2, calls.Count);
    }

    [Fact(DisplayName = "Switching folder cancels the stale prefetch instead of leaving it on the pump, and answers for the new folder")]
    public async Task PrefetchForOtherFolderIsCancelled()
    {
        var aStarted = new ManualResetEventSlim();
        var aSawCancel = new ManualResetEventSlim();
        using var service = Create((folder, _, _, token) =>
        {
            if (folder.EndsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                aStarted.Set();
                Block(token);
                aSawCancel.Set();
                token.ThrowIfCancellationRequested();
            }
            return Available(folder, "b.jpg");
        });

        service.Prefetch(Folder("A"), Long);
        Assert.True(aStarted.Wait(HangGuard));
        var snapshot = await service.TryGetSnapshotProgressiveAsync(Folder("B"), Long).WaitAsync(HangGuard);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.Equal(Folder("B"), snapshot.Folder, ignoreCase: true);
        Assert.True(aSawCancel.Wait(HangGuard));
    }

    [Fact]
    public async Task NewPrefetchSupersedesAndCancelsThePreviousOne()
    {
        var firstStarted = new ManualResetEventSlim();
        var firstSawCancel = new ManualResetEventSlim();
        using var service = Create((folder, _, _, token) =>
        {
            if (folder.EndsWith('1'))
            {
                firstStarted.Set();
                Block(token);
                firstSawCancel.Set();
                token.ThrowIfCancellationRequested();
            }
            return Available(folder, "a.jpg");
        });

        service.Prefetch(Folder("pf1"), Long);
        Assert.True(firstStarted.Wait(HangGuard));
        service.Prefetch(Folder("pf2"), Long);
        var joined = await service.TryGetSnapshotProgressiveAsync(Folder("pf2"), Long).WaitAsync(HangGuard);

        Assert.Equal(ExplorerOrderStatus.Available, joined.Status);
        Assert.True(firstSawCancel.Wait(HangGuard));
    }

    [Fact(DisplayName = "A prefetch older than the max age is discarded and cancelled: the folder is queried afresh")]
    public async Task StalePrefetchIsNotJoined()
    {
        var time = new ManualTime();
        var prefetchStarted = new ManualResetEventSlim();
        var prefetchSawCancel = new ManualResetEventSlim();
        var calls = 0;
        using var service = Create((folder, _, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // The prefetch: never completes on its own, so joining it would hang the caller.
                prefetchStarted.Set();
                Block(token);
                prefetchSawCancel.Set();
                token.ThrowIfCancellationRequested();
            }
            return Available(folder, "fresh.jpg");
        }, time: time, maxAge: TimeSpan.FromSeconds(30));

        service.Prefetch(Folder("stale"), Long);
        Assert.True(prefetchStarted.Wait(HangGuard));
        time.Advance(TimeSpan.FromSeconds(31));
        var snapshot = await service.TryGetSnapshotProgressiveAsync(Folder("stale"), Long).WaitAsync(HangGuard);

        Assert.Equal(ExplorerOrderStatus.Available, snapshot.Status);
        Assert.EndsWith("fresh.jpg", Assert.Single(snapshot.OrderedPaths), StringComparison.Ordinal);
        Assert.True(prefetchSawCancel.Wait(HangGuard));
    }

    [Fact(DisplayName = "A fresh prefetch within the max age is joined without a second query")]
    public async Task FreshPrefetchIsJoined()
    {
        var time = new ManualTime();
        var calls = 0;
        using var service = Create((folder, _, _, _) => { Interlocked.Increment(ref calls); return Available(folder, "a.jpg"); },
            time: time, maxAge: TimeSpan.FromSeconds(30));

        service.Prefetch(Folder("fresh"), Long);
        time.Advance(TimeSpan.FromSeconds(29));
        await service.TryGetSnapshotProgressiveAsync(Folder("fresh"), Long).WaitAsync(HangGuard);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact(DisplayName = "Joining a slow prefetch honours the caller's own timeout and cancels the prefetch")]
    public async Task JoinedPrefetchHonoursCallerTimeout()
    {
        var started = new ManualResetEventSlim();
        var sawCancel = new ManualResetEventSlim();
        using var service = Create((folder, _, _, token) =>
        {
            started.Set();
            Block(token);
            sawCancel.Set();
            token.ThrowIfCancellationRequested();
            return Available(folder);
        });

        service.Prefetch(Folder("slow"), Long);
        Assert.True(started.Wait(HangGuard));
        var snapshot = await service.TryGetSnapshotProgressiveAsync(Folder("slow"), TimeSpan.Zero).WaitAsync(HangGuard);

        Assert.Equal(ExplorerOrderStatus.TimedOut, snapshot.Status);
        Assert.True(sawCancel.Wait(HangGuard));
    }

    [Fact]
    public async Task JoinedPrefetchHonoursCallerCancellation()
    {
        var started = new ManualResetEventSlim();
        using var service = Create((folder, _, _, token) =>
        {
            started.Set();
            Block(token);
            token.ThrowIfCancellationRequested();
            return Available(folder);
        });
        using var cts = new CancellationTokenSource();

        service.Prefetch(Folder("jc"), Long);
        Assert.True(started.Wait(HangGuard));
        var pending = service.TryGetSnapshotProgressiveAsync(Folder("jc"), Long, cancellationToken: cts.Token);
        cts.Cancel();

        Assert.Equal(ExplorerOrderStatus.Canceled, (await pending.WaitAsync(HangGuard)).Status);
    }

    [Theory(DisplayName = "Unusable folder text yields a Failed snapshot, never an exception, and prefetch ignores it")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\photos\\a\0b")]
    public async Task InvalidFolderIsReportedNotThrown(string folder)
    {
        var calls = 0;
        using var service = Create((f, _, _, _) => { Interlocked.Increment(ref calls); return Available(f); });

        service.Prefetch(folder, Long);
        var full = await service.TryGetSnapshotAsync(folder, Long, CancellationToken.None);
        var progressive = await service.TryGetSnapshotProgressiveAsync(folder, Long);

        Assert.All(new[] { full, progressive }, s =>
        {
            Assert.Equal(ExplorerOrderStatus.Failed, s.Status);
            Assert.Equal("query-failed|ArgumentException", s.Reason);
        });
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task DisposeIsIdempotentAndLaterUseIsCancelledNotThrown()
    {
        var service = Create(Quick);

        service.Dispose();
        service.Dispose();

        service.Prefetch(Folder("late"), Long);
        var full = await service.TryGetSnapshotAsync(Folder("late"), Long, CancellationToken.None);
        var progressive = await service.TryGetSnapshotProgressiveAsync(Folder("late"), Long);
        Assert.Equal(ExplorerOrderStatus.Canceled, full.Status);
        Assert.Equal(ExplorerOrderStatus.Canceled, progressive.Status);
    }

    [Fact(DisplayName = "Dispose cancels an in-flight prefetch and returns even when the query never ends")]
    public async Task DisposeCancelsPrefetchAndDoesNotHangOnAStuckQuery()
    {
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var log = new MemoryLog();
        var service = Create((folder, _, _, _) =>
        {
            started.Set();
            release.Wait(CancellationToken.None); // a COM call that ignores the token
            return Available(folder);
        }, log);

        try
        {
            service.Prefetch(Folder("stuck"), Long);
            Assert.True(started.Wait(HangGuard));

            await Task.Run(service.Dispose).WaitAsync(HangGuard);

            Assert.Contains(log.Messages, m => m.Contains("did not exit", StringComparison.Ordinal));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ConcurrentPrefetchAndRequestsNeverThrowOrLeaveTheServiceStuck()
    {
        using var service = Create((folder, _, _, token) => { token.ThrowIfCancellationRequested(); return Available(folder, "a.jpg"); });

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            service.Prefetch(Folder("r" + (i % 3)), Long);
            var snapshot = await service.TryGetSnapshotProgressiveAsync(Folder("r" + (i % 5)), Long);
            Assert.True(snapshot.Status is ExplorerOrderStatus.Available or ExplorerOrderStatus.Canceled, snapshot.Status.ToString());
        }))).WaitAsync(HangGuard);

        var final = await service.TryGetSnapshotAsync(Folder("done"), Long, CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal(ExplorerOrderStatus.Available, final.Status);
    }
}
