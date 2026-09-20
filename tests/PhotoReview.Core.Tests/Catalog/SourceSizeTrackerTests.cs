using System;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

public class SourceSizeTrackerTests
{
    [Fact]
    public void GetTotal_WithCachedMetadata_AvoidsFsCall()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset(["file1", "file2", "file3"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);
        catalog.UpdateMetadata("file3", 300L, DateTime.UtcNow);

        var total = tracker.GetTotal();

        Assert.Equal(600L, total);
        Assert.Equal(0, tracker.LastFsCallCount);
    }

    [Fact]
    public void GetTotal_PartialMetadata_CallsFsForMissing()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset(["file1", "file2"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);

        _ = tracker.GetTotal();

        Assert.Equal(1, tracker.LastFsCallCount);
    }

    [Fact]
    public void GetTotal_CachesResults_NoRecomputeWhenUnchanged()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset(["file1", "file2"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);

        var total1 = tracker.GetTotal();
        var total2 = tracker.GetTotal();

        Assert.Equal(300L, total1);
        Assert.Equal(300L, total2);
        Assert.Equal(0, tracker.LastFsCallCount);
    }

    [Fact]
    public void GetTotal_InvalidatesOnMembership_Remove()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset(["file1", "file2", "file3"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);
        catalog.UpdateMetadata("file3", 300L, DateTime.UtcNow);

        var total1 = tracker.GetTotal();
        catalog.Remove("file2");
        var total2 = tracker.GetTotal();

        Assert.Equal(600L, total1);
        Assert.Equal(400L, total2);
    }

    [Fact]
    public void GetTotal_InvalidatesOnMembership_Restore()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset(["file1", "file2"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);

        var total1 = tracker.GetTotal();
        catalog.Remove("file2");
        var total2 = tracker.GetTotal();
        catalog.Restore("file2", 1);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);
        var total3 = tracker.GetTotal();

        Assert.Equal(300L, total1);
        Assert.Equal(100L, total2);
        Assert.Equal(300L, total3);
    }

    [Fact]
    public void GetTotal_InvalidatesOnMembership_ReplaceOrder()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset(["file1", "file2", "file3"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);
        catalog.UpdateMetadata("file3", 300L, DateTime.UtcNow);

        var total1 = tracker.GetTotal();
        catalog.ReplaceOrder(["file3", "file1", "file2"]);
        var total2 = tracker.GetTotal();

        Assert.Equal(600L, total1);
        Assert.Equal(600L, total2);
    }

    [Fact]
    public void GetTotal_HandlesEmptyPreloaded()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        var total = tracker.GetTotal();

        Assert.Equal(0L, total);
        Assert.Equal(0, tracker.LastFsCallCount);
    }

    [Fact]
    public void GetTotal_MultipleCallsAreThreadSafe()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset(["file1", "file2"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);

        long total1 = 0, total2 = 0;
        var t1 = new System.Threading.Tasks.Task(() => total1 = tracker.GetTotal());
        var t2 = new System.Threading.Tasks.Task(() => total2 = tracker.GetTotal());

        t1.Start();
        t2.Start();
        System.Threading.Tasks.Task.WaitAll(t1, t2);

        Assert.Equal(300L, total1);
        Assert.Equal(300L, total2);
    }
}
