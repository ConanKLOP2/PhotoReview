using System.IO;
using System.Runtime.CompilerServices;
using PhotoReview.Imaging.Caching;
using PhotoReview.TestSupport.Windows.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// RV-I10: a shared thumbnail load that faults after its only caller cancelled must not leave an unobserved task exception.
/// Deterministic: the faulted task is tracked by a weak reference and the test first proves it was actually collected
/// (bounded GC loop, no sleeps), so "no UnobservedTaskException" can never pass just because the finalizer did not run yet.
/// </summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")]
public sealed class ThumbnailCacheUnobservedFaultTests : IDisposable
{
    private const int MaxGcRounds = 20;
    private readonly TempRoot _root = new("thumb-unobserved");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "RV-I10: caller cancels, then the shared load faults: the fault is observed (no UnobservedTaskException once collected)")]
    public void CancelledCaller_ThenLoadFaults_ExceptionIsObserved()
    {
        var marker = "rv-i10-" + Guid.NewGuid().ToString("N");
        var unobserved = CountUnobserved(marker, () => StartCancelThenFault(marker));

        Assert.Equal(0, unobserved);
    }

    [Fact(DisplayName = "RV-I10 harness check: a faulted task nobody observes IS reported once it is collected")]
    public void Harness_DetectsAnUnobservedFault()
    {
        var marker = "rv-i10-harness-" + Guid.NewGuid().ToString("N");
        var unobserved = CountUnobserved(marker, () => CreateUnobservedFault(marker));

        Assert.Equal(1, unobserved);
    }

    // Runs the scenario in a non-inlined frame, then collects until its faulted task is gone (bounded) and finalizers ran.
    private static int CountUnobserved(string marker, Func<WeakReference> scenario)
    {
        var count = 0;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message.Contains(marker, StringComparison.Ordinal)))
                Interlocked.Increment(ref count);
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var faulted = scenario();
            for (var round = 0; round < MaxGcRounds && faulted.IsAlive; round++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.False(faulted.IsAlive, $"The faulted task was still reachable after {MaxGcRounds} GC rounds; the check would be vacuous.");
            // The task's exception holder is finalized in the round that collected the task; run any still pending.
            GC.WaitForPendingFinalizers();
            return Volatile.Read(ref count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference StartCancelThenFault(string marker)
    {
        var source = _root.File("a.jpg", EmbeddedThumbnailJpegFixture.CreateWithThumbnail(48, 16));
        var load = new TaskCompletionSource<IDecodedImage?>();
        var cache = new ThumbnailCache(_root.Dir("disk"), persistNewThumbnails: false, embeddedThumbnailReader: (path, token) => load.Task);
        Task? shared = null;
        cache.SharedLoadForTests = task => shared = task;
        using var cts = new CancellationTokenSource();

        var pending = cache.GetAsync(source, cts.Token);
        cts.Cancel();
        Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).GetAwaiter().GetResult();

        load.SetException(new IOException(marker));
        Assert.NotNull(shared);
        // The load's await continuation may be queued to the thread pool (the test thread has a SynchronizationContext,
        // so it is not inlined): wait for the fault WITHOUT observing it -- a WaitAsync/await here would mark it handled.
        Assert.True(((IAsyncResult)shared!).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "The shared load never completed.");
        Assert.True(shared.IsFaulted, "The shared load should have faulted.");
        cache.SharedLoadForTests = null;
        cache.Dispose();
        return new WeakReference(shared);
    }

    [Fact(DisplayName = "RV-I10 premise: a WaitAsync caller that cancelled before the source faulted does not observe that fault")]
    public void CancelledWaitAsync_DoesNotObserveALaterFault()
    {
        var marker = "rv-i10-premise-" + Guid.NewGuid().ToString("N");
        var unobserved = CountUnobserved(marker, () => CancelWaitThenFault(marker));

        Assert.Equal(1, unobserved); // why ThumbnailCache must observe the shared load itself
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CancelWaitThenFault(string marker)
    {
        var source = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var waiting = source.Task.WaitAsync(cts.Token);
        cts.Cancel();
        Assert.True(waiting.IsCanceled);
        source.SetException(new IOException(marker));
        return new WeakReference(source.Task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnobservedFault(string marker)
    {
        var source = new TaskCompletionSource();
        source.SetException(new IOException(marker));
        return new WeakReference(source.Task);
    }
}
