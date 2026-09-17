using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.Core.Tests.Diagnostics;

public sealed class ReviewMetricsTests
{
    [Fact]
    public void SnapshotInitialValuesAreZero()
    {
        var metrics = new ReviewMetrics();
        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.CacheHits);
        Assert.Equal(0, snapshot.CacheMisses);
        Assert.Equal(0, snapshot.SourceBytesRead);
        Assert.Equal(0, snapshot.SourceReads);
        Assert.Equal(0, snapshot.DecodeMilliseconds);
        Assert.Equal(0, snapshot.PresentedImages);
        Assert.Equal(0, snapshot.PresentMilliseconds);
        Assert.Equal(0, snapshot.PreloadHits);
        Assert.Equal(0, snapshot.InflightJoins);
        Assert.Equal(0, snapshot.DiskCacheHits);
        Assert.Equal(0, snapshot.QueueWaitMilliseconds);
        Assert.Equal(0, snapshot.UiAssignMilliseconds);
    }

    [Fact]
    public void RecordMethodsAccumulateCorrectly()
    {
        var metrics = new ReviewMetrics();

        metrics.RecordCacheHit();
        metrics.RecordCacheHit();
        metrics.RecordCacheMiss();

        metrics.RecordSourceRead(1024, 15);
        metrics.RecordSourceRead(2048, 25);

        metrics.RecordPresented(50);
        metrics.RecordPreloadHit();
        metrics.RecordInflightJoin();
        metrics.RecordDiskCacheHit();
        metrics.RecordQueueWait(5);
        metrics.RecordUiAssign(10);

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.CacheHits);
        Assert.Equal(1, snapshot.CacheMisses);
        Assert.Equal(3072, snapshot.SourceBytesRead);
        Assert.Equal(2, snapshot.SourceReads);
        Assert.Equal(40, snapshot.DecodeMilliseconds);
        Assert.Equal(1, snapshot.PresentedImages);
        Assert.Equal(50, snapshot.PresentMilliseconds);
        Assert.Equal(1, snapshot.PreloadHits);
        Assert.Equal(1, snapshot.InflightJoins);
        Assert.Equal(1, snapshot.DiskCacheHits);
        Assert.Equal(5, snapshot.QueueWaitMilliseconds);
        Assert.Equal(10, snapshot.UiAssignMilliseconds);
    }

    [Fact]
    public void ConcurrentRecordingIsThreadSafe()
    {
        var metrics = new ReviewMetrics();
        const int iterations = 1000;

        Parallel.For(0, iterations, _ =>
        {
            metrics.RecordCacheHit();
            metrics.RecordCacheMiss();
            metrics.RecordSourceRead(100, 2);
            metrics.RecordPresented(5);
            metrics.RecordPreloadHit();
            metrics.RecordInflightJoin();
            metrics.RecordDiskCacheHit();
            metrics.RecordQueueWait(1);
            metrics.RecordUiAssign(1);
        });

        var snapshot = metrics.Snapshot();

        Assert.Equal(iterations, snapshot.CacheHits);
        Assert.Equal(iterations, snapshot.CacheMisses);
        Assert.Equal(iterations * 100, snapshot.SourceBytesRead);
        Assert.Equal(iterations, snapshot.SourceReads);
        Assert.Equal(iterations * 2, snapshot.DecodeMilliseconds);
        Assert.Equal(iterations, snapshot.PresentedImages);
        Assert.Equal(iterations * 5, snapshot.PresentMilliseconds);
        Assert.Equal(iterations, snapshot.PreloadHits);
        Assert.Equal(iterations, snapshot.InflightJoins);
        Assert.Equal(iterations, snapshot.DiskCacheHits);
        Assert.Equal(iterations, snapshot.QueueWaitMilliseconds);
        Assert.Equal(iterations, snapshot.UiAssignMilliseconds);
    }
}
