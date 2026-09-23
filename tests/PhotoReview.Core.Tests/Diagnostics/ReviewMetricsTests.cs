using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.Core.Tests.Diagnostics;

[Trait("Category", "HotPath")]
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
    public void DecodeMillisecondsEwma_StartsAtFirstSampleThenSmooths()
    {
        var metrics = new ReviewMetrics();
        Assert.Equal(0, metrics.DecodeMillisecondsEwma);

        metrics.RecordSourceRead(100, 300);
        Assert.Equal(300, metrics.DecodeMillisecondsEwma);

        metrics.RecordSourceRead(100, 400);
        Assert.Equal(300 + ReviewMetrics.DecodeEwmaAlpha * 100, metrics.DecodeMillisecondsEwma, 6);

        metrics.RecordSourceRead(100, 0); // a zero-time read (e.g. rounding) is not a decode sample
        Assert.Equal(300 + ReviewMetrics.DecodeEwmaAlpha * 100, metrics.DecodeMillisecondsEwma, 6);
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

    [Fact(DisplayName = "SourceOpenCount totals every open and TopSourceOpens lists the 10 most opened paths")]
    public void SourceOpensAreCountedInTotalAndPerPath()
    {
        var metrics = new ReviewMetrics();
        for (var i = 0; i < 12; i++)
            for (var n = 0; n <= i; n++)
                metrics.RecordSourceOpen($@"C:\photos\img{i:D2}.jpg");
        metrics.RecordSourceOpen(@"C:\PHOTOS\IMG11.JPG"); // same path, different case

        var snapshot = metrics.Snapshot();

        Assert.Equal(78 + 1, snapshot.SourceOpenCount);
        Assert.Equal(10, snapshot.TopSourceOpens.Count);
        Assert.Equal(@"C:\photos\img11.jpg", snapshot.TopSourceOpens[0].Path);
        Assert.Equal(13, snapshot.TopSourceOpens[0].Count);
        Assert.Equal(@"C:\photos\img10.jpg", snapshot.TopSourceOpens[1].Path);
        Assert.DoesNotContain(snapshot.TopSourceOpens, e => e.Path.EndsWith("img00.jpg", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "StatCount, SessionWriteCount and DecoderFallbackCount accumulate")]
    public void StatSessionWriteAndFallbackCountsAccumulate()
    {
        var metrics = new ReviewMetrics();
        metrics.RecordStat();
        metrics.RecordStat();
        metrics.RecordSessionWrite();
        metrics.RecordDecoderFallback(PhotoReview.Core.Model.DecoderBackend.WicDirect);
        metrics.RecordDecoderFallback(PhotoReview.Core.Model.DecoderBackend.TurboJpeg);
        metrics.RecordDecoderFallback(PhotoReview.Core.Model.DecoderBackend.WicDirect);

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.StatCount);
        Assert.Equal(1, snapshot.SessionWriteCount);
        Assert.Equal(3, snapshot.DecoderFallbackCount);
    }

    [Fact(DisplayName = "AR04: CrossThreadPresentCount starts at 0 and accumulates")]
    public void CrossThreadPresentCountAccumulates()
    {
        var metrics = new ReviewMetrics();
        Assert.Equal(0, metrics.Snapshot().CrossThreadPresentCount);

        metrics.RecordCrossThreadPresent();
        metrics.RecordCrossThreadPresent();

        Assert.Equal(2, metrics.Snapshot().CrossThreadPresentCount);
    }

    [Theory(DisplayName = "Present latency lands in the documented histogram bucket")]
    [InlineData(0, "<=8")]
    [InlineData(8, "<=8")]
    [InlineData(9, "<=16")]
    [InlineData(16, "<=16")]
    [InlineData(17, "<=33")]
    [InlineData(33, "<=33")]
    [InlineData(34, "<=50")]
    [InlineData(50, "<=50")]
    [InlineData(51, "<=100")]
    [InlineData(100, "<=100")]
    [InlineData(101, "<=200")]
    [InlineData(200, "<=200")]
    [InlineData(201, "<=500")]
    [InlineData(500, "<=500")]
    [InlineData(501, ">500")]
    [InlineData(60_000, ">500")]
    public void PresentLatencyIsBucketed(long milliseconds, string expectedLabel)
    {
        var metrics = new ReviewMetrics();
        metrics.RecordPresented(milliseconds);

        var histogram = metrics.Snapshot().PresentHistogram;

        Assert.Equal(8, histogram.Count);
        Assert.Equal(expectedLabel, Assert.Single(histogram, b => b.Count == 1).Label);
    }

    [Fact(DisplayName = "New metric fields are present in the serialized snapshot (benchmark report)")]
    public void NewFieldsAreSerialized()
    {
        var metrics = new ReviewMetrics();
        metrics.RecordSourceOpen(@"C:\photos\a.jpg");
        metrics.RecordStat();
        metrics.RecordSessionWrite();
        metrics.RecordPresented(20);

        var json = System.Text.Json.JsonSerializer.Serialize(metrics.Snapshot());

        foreach (var field in new[] { "SourceOpenCount", "TopSourceOpens", "StatCount", "SessionWriteCount", "PresentHistogram", "DecoderFallbackCount", "CrossThreadPresentCount" })
            Assert.Contains($"\"{field}\"", json);
    }
}

