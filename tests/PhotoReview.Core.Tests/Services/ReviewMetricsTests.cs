using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Services;

/// <summary>Review metrics counters.</summary>
public sealed class ReviewMetricsTests
{
    [Fact(DisplayName = "Review metrics snapshot preserves counters")]
    public void ReviewMetricsSnapshotPreservesCounters()
    {
        var metrics = new ReviewMetrics();
        metrics.RecordCacheHit();
        metrics.RecordCacheMiss();
        metrics.RecordSourceRead(128, 7);
        metrics.RecordPresented(11);
        var snapshot = metrics.Snapshot();
        Assert.True(snapshot.CacheHits == 1 && snapshot.CacheMisses == 1 && snapshot.SourceReads == 1
            && snapshot.SourceBytesRead == 128 && snapshot.DecodeMilliseconds == 7
            && snapshot.PresentedImages == 1 && snapshot.PresentMilliseconds == 11);
    }

    [Fact(DisplayName = "Concurrent preload diagnostics retain every delivery and timing event")]
    public void ConcurrentPreloadDiagnosticsRetainEveryEvent()
    {
        var metrics = new ReviewMetrics();
        Parallel.For(0, 500, _ =>
        {
            metrics.RecordPreloadHit();
            metrics.RecordInflightJoin();
            metrics.RecordDiskCacheHit();
            metrics.RecordQueueWait(2);
            metrics.RecordUiAssign(3);
        });
        var snapshot = metrics.Snapshot();
        Assert.True(snapshot.PreloadHits == 500 && snapshot.InflightJoins == 500
            && snapshot.DiskCacheHits == 500 && snapshot.QueueWaitMilliseconds == 1000
            && snapshot.UiAssignMilliseconds == 1500);
    }
}

