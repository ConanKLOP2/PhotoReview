using System.IO;
using System.Runtime.CompilerServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Caching;
using PhotoReview.TestSupport.Windows.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>RV-I10: a shared thumbnail load that faults after its only caller cancelled must not leave an unobserved task exception.</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")]
public sealed class ThumbnailCacheUnobservedFaultTests : IDisposable
{
    private readonly TempRoot _root = new("thumb-unobserved");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "RV-I10: caller cancels, then the shared load faults: no UnobservedTaskException after GC")]
    public void CancelledCaller_ThenLoadFaults_ExceptionIsObserved()
    {
        var marker = "rv-i10-" + Guid.NewGuid().ToString("N");
        var unobserved = 0;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message.Contains(marker, StringComparison.Ordinal)))
                Interlocked.Increment(ref unobserved);
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            StartCancelThenFault(marker);
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    // Separate, non-inlined frame: nothing from here stays reachable from the test method's locals once it returns.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartCancelThenFault(string marker)
    {
        var source = _root.File("a.jpg", EmbeddedThumbnailJpegFixture.CreateWithThumbnail(48, 16));
        // Synchronous continuations: SetException below runs the load's fault and the in-flight cleanup inline.
        var load = new TaskCompletionSource<IDecodedImage?>();
        var cache = new ThumbnailCache(_root.Dir("disk"), persistNewThumbnails: false, embeddedThumbnailReader: (path, token) => load.Task);
        using var cts = new CancellationTokenSource();

        var pending = cache.GetAsync(source, cts.Token);
        cts.Cancel();
        Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).GetAwaiter().GetResult();

        load.SetException(new IOException(marker));
        cache.Dispose();
    }
}
