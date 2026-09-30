using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Deterministic tests of the priority/cancellable single-slot gate; needs neither libraw.dll nor the corpus.</summary>
public sealed class LibRawFullDecodeGateTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>A gate whose test can wait until N waiters are really queued (no sleeps).</summary>
    private sealed class Harness : IDisposable
    {
        private readonly SemaphoreSlim _queued = new(0);
        internal FullDecodeGate Gate { get; }
        internal List<string> Order { get; } = [];

        internal Harness(int maxQueuedPreloads = 2) => Gate = new FullDecodeGate(maxQueuedPreloads, _ => _queued.Release());

        public void Dispose() => _queued.Dispose();

        internal void AwaitQueued(int count)
        {
            for (var i = 0; i < count; i++) Assert.True(_queued.Wait(Bound), "waiter never queued");
        }

        /// <summary>Starts a thread that enters the gate, records its name, then holds the slot until released.</summary>
        internal (Task Task, SemaphoreSlim Release) Start(string name, SourceReadPriority priority, CancellationToken token = default)
        {
            var release = new SemaphoreSlim(0);
            var task = Task.Factory.StartNew(() =>
            {
                using var lease = Gate.Enter(priority, token);
                lock (Order) Order.Add(name);
                Assert.True(release.Wait(Bound));
            }, TaskCreationOptions.LongRunning);
            return (task, release);
        }
    }

    [Fact]
    public async Task Enter_ViewerArrivesAfterThreeQueuedPreloads_IsServedBeforeThem()
    {
        using var h = new Harness(maxQueuedPreloads: 3);
        var running = h.Gate.Enter(SourceReadPriority.Preload, CancellationToken.None);
        var p1 = h.Start("p1", SourceReadPriority.Preload);
        h.AwaitQueued(1);
        var p2 = h.Start("p2", SourceReadPriority.Preload);
        h.AwaitQueued(1);
        var p3 = h.Start("p3", SourceReadPriority.Preload);
        h.AwaitQueued(1);
        var viewer = h.Start("viewer", SourceReadPriority.Viewer);
        h.AwaitQueued(1);

        running.Dispose();
        foreach (var entry in new[] { viewer, p1, p2, p3 }) entry.Release.Release();
        await Task.WhenAll([viewer.Task, p1.Task, p2.Task, p3.Task]).WaitAsync(Bound);

        Assert.Equal(["viewer", "p1", "p2", "p3"], h.Order);
    }

    [Fact]
    public async Task Enter_ViewerWaitersAreServedInArrivalOrder()
    {
        using var h = new Harness();
        var running = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        var v1 = h.Start("v1", SourceReadPriority.Viewer);
        h.AwaitQueued(1);
        var v2 = h.Start("v2", SourceReadPriority.Viewer);
        h.AwaitQueued(1);

        running.Dispose();
        v1.Release.Release();
        v2.Release.Release();
        await Task.WhenAll([v1.Task, v2.Task]).WaitAsync(Bound);

        Assert.Equal(["v1", "v2"], h.Order);
    }

    [Fact]
    public async Task Enter_WhileADecodeRuns_TheRunningDecodeIsNeverInterruptedAndTheSlotStaysTaken()
    {
        using var h = new Harness();
        using var runningToken = new CancellationTokenSource();
        var running = h.Gate.Enter(SourceReadPriority.Preload, runningToken.Token);
        var viewer = h.Start("viewer", SourceReadPriority.Viewer);
        h.AwaitQueued(1);

        // A waiting viewer neither steals the slot nor cancels the holder (its token stays untouched).
        Assert.Equal(0, h.Gate.SlotsAvailable);
        Assert.False(runningToken.IsCancellationRequested);
        Assert.Empty(h.Order);

        running.Dispose();
        viewer.Release.Release();
        await viewer.Task.WaitAsync(Bound);
        Assert.Equal(1, h.Gate.SlotsAvailable);
    }

    [Fact]
    public async Task Enter_QueuedWaiterCancelled_ReleasesItWithoutTouchingTheSlot()
    {
        using var h = new Harness();
        var running = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiter = h.Start("cancelled", SourceReadPriority.Viewer, cts.Token);
        h.AwaitQueued(1);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.Task);

        Assert.Equal(0, h.Gate.SlotsAvailable); // the holder still owns the slot
        Assert.Equal(0, h.Gate.QueuedViewers);  // and the cancelled waiter left the queue
        running.Dispose();
        Assert.Equal(1, h.Gate.SlotsAvailable);
        Assert.Empty(h.Order);
    }

    [Fact]
    public async Task Enter_CancelledPreloadWaiter_FreesItsQueueSpotForAnotherPreload()
    {
        using var h = new Harness(maxQueuedPreloads: 1);
        var running = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var first = h.Start("first", SourceReadPriority.Preload, cts.Token);
        h.AwaitQueued(1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Task);

        var second = h.Start("second", SourceReadPriority.Preload); // would be refused if the cancelled waiter still counted
        h.AwaitQueued(1);
        running.Dispose();
        second.Release.Release();

        await second.Task.WaitAsync(Bound);
        Assert.Equal(["second"], h.Order);
    }

    [Fact]
    public void Enter_AlreadyCancelledToken_ThrowsBeforeTakingTheSlot()
    {
        var gate = new FullDecodeGate(2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => gate.Enter(SourceReadPriority.Viewer, cts.Token));

        Assert.Equal(1, gate.SlotsAvailable);
    }

    [Fact]
    public async Task Enter_MoreThanTheMaximumQueuedPreloads_FailFastWithoutQueueing()
    {
        using var h = new Harness(maxQueuedPreloads: 2);
        var running = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        var p1 = h.Start("p1", SourceReadPriority.Preload);
        h.AwaitQueued(1);
        var p2 = h.Start("p2", SourceReadPriority.Preload);
        h.AwaitQueued(1);

        Assert.Throws<DecoderBusyException>(() => h.Gate.Enter(SourceReadPriority.Preload, new CancellationTokenSource(Bound).Token));

        Assert.Equal(2, h.Gate.QueuedPreloads);
        // A viewer is never refused because preloads are queued.
        var viewer = h.Start("viewer", SourceReadPriority.Viewer);
        h.AwaitQueued(1);
        running.Dispose();
        foreach (var entry in new[] { viewer, p1, p2 }) entry.Release.Release();
        await Task.WhenAll([viewer.Task, p1.Task, p2.Task]).WaitAsync(Bound);
        Assert.Equal(["viewer", "p1", "p2"], h.Order);
    }

    [Fact]
    public async Task Enter_PreloadWaiterThatSawThreeDecodesFinish_IsPromotedAheadOfNewerViewers()
    {
        using var h = new Harness(maxQueuedPreloads: 2);
        var running = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        var preload = h.Start("p", SourceReadPriority.Preload);
        h.AwaitQueued(1);
        var v1 = h.Start("v1", SourceReadPriority.Viewer);
        h.AwaitQueued(1);
        var v2 = h.Start("v2", SourceReadPriority.Viewer);
        h.AwaitQueued(1);
        var v3 = h.Start("v3", SourceReadPriority.Viewer);
        h.AwaitQueued(1);
        var v4 = h.Start("v4", SourceReadPriority.Viewer);
        h.AwaitQueued(1);

        running.Dispose(); // completion 1: the viewer lane keeps priority while the preload is young
        foreach (var entry in new[] { v1, v2, preload, v3, v4 }) entry.Release.Release();
        await Task.WhenAll([v1.Task, v2.Task, preload.Task, v3.Task, v4.Task]).WaitAsync(Bound);

        // v1 and v2 finish (completions 2 and 3): the preload has now aged out and goes before v3/v4.
        Assert.Equal(["v1", "v2", "p", "v3", "v4"], h.Order);
    }

    [Fact]
    public async Task Enter_PreloadWaiterBelowTheAgingBound_StillYieldsToViewers()
    {
        using var h = new Harness(maxQueuedPreloads: 2);
        var running = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        var preload = h.Start("p", SourceReadPriority.Preload);
        h.AwaitQueued(1);
        var v1 = h.Start("v1", SourceReadPriority.Viewer);
        h.AwaitQueued(1);
        var v2 = h.Start("v2", SourceReadPriority.Viewer);
        h.AwaitQueued(1);

        running.Dispose();
        foreach (var entry in new[] { v1, v2, preload }) entry.Release.Release();
        await Task.WhenAll([v1.Task, v2.Task, preload.Task]).WaitAsync(Bound);

        Assert.Equal(["v1", "v2", "p"], h.Order); // only 2 completions before the preload's turn: not aged yet
    }

    [Fact]
    public async Task Lease_DisposedTwice_ReleasesTheSlotOnlyOnce()
    {
        using var h = new Harness();
        var first = h.Gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);
        var waiter = h.Start("waiter", SourceReadPriority.Viewer);
        h.AwaitQueued(1);

        first.Dispose();
        Assert.Equal(0, h.Gate.SlotsAvailable); // handed to the waiter
        first.Dispose(); // must not release the waiter's slot
        Assert.Equal(0, h.Gate.SlotsAvailable);

        waiter.Release.Release();
        await waiter.Task.WaitAsync(Bound);
        Assert.True(SpinWait.SpinUntil(() => h.Gate.SlotsAvailable == 1, Bound));
    }

    [Fact]
    public async Task Enter_EveryPath_LeavesTheGateFreeAtTheEnd()
    {
        using var h = new Harness(maxQueuedPreloads: 1);
        Task queuedTask;
        using (h.Gate.Enter(SourceReadPriority.Preload, CancellationToken.None))
        {
            var queued = h.Start("p", SourceReadPriority.Preload);
            queuedTask = queued.Task;
            h.AwaitQueued(1);
            Assert.Throws<DecoderBusyException>(() => h.Gate.Enter(SourceReadPriority.Preload, new CancellationTokenSource(Bound).Token));
            queued.Release.Release();
        }

        await queuedTask.WaitAsync(Bound);
        Assert.True(SpinWait.SpinUntil(() => h.Gate.SlotsAvailable == 1, Bound));
        Assert.Equal(0, h.Gate.QueuedPreloads);
        Assert.Equal(0, h.Gate.QueuedViewers);
    }
}
