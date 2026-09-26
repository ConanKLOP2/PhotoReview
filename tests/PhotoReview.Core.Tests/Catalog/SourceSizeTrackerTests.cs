using System;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Tests.Fakes;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
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

    [Fact(DisplayName = "After Observe, GetTotal sums the observed snapshot and never reads the live catalog (R2-F-11)")]
    public void GetTotal_AfterObserve_UsesSnapshotNotLiveCatalog()
    {
        var catalog = new ReviewCatalog();
        var fs = new CountingFileSystem(new PhysicalFileSystem(), new ReviewMetrics());
        var tracker = new SourceSizeTracker(catalog, fs);
        catalog.Reset(["file1", "file2"]);
        catalog.UpdateMetadata("file1", 100L, DateTime.UtcNow);
        catalog.UpdateMetadata("file2", 200L, DateTime.UtcNow);

        tracker.Observe(catalog.EntriesSnapshot());
        Assert.Equal(300L, tracker.GetTotal());

        // The UI thread now changes the live catalog; the preload thread must keep seeing its own snapshot.
        catalog.Reset(["other"]);
        catalog.UpdateMetadata("other", 5L, DateTime.UtcNow);
        Assert.Equal(300L, tracker.GetTotal());

        // A new preload lifetime observes a new snapshot.
        tracker.Observe(catalog.EntriesSnapshot());
        Assert.Equal(5L, tracker.GetTotal());
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
    public async System.Threading.Tasks.Task GetTotal_MultipleCallsAreThreadSafe()
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
        await System.Threading.Tasks.Task.WhenAll(t1, t2);

        Assert.Equal(300L, total1);
        Assert.Equal(300L, total2);
    }

    [Fact(DisplayName = "Observe (UI thread) is not blocked while GetTotal (preload thread) is statting files")]
    public async Task Observe_WhileGetTotalIsStatting_DoesNotBlock()
    {
        using var inStat = new ManualResetEventSlim(false);
        using var releaseStat = new ManualResetEventSlim(false);
        var fs = new InMemoryFileSystem { StatHook = _ => { inStat.Set(); releaseStat.Wait(TimeSpan.FromSeconds(60)); return null; } };
        var catalog = new ReviewCatalog();
        var tracker = new SourceSizeTracker(catalog, fs);
        catalog.Reset(["file1"]); // no cached Length, so GetTotal must stat
        tracker.Observe(catalog.EntriesSnapshot());

        var total = Task.Run(tracker.GetTotal);
        Assert.True(inStat.Wait(TimeSpan.FromSeconds(10)), "GetTotal never started statting");

        using var observed = new ManualResetEventSlim(false);
        var observe = Task.Run(() => { tracker.Observe(catalog.EntriesSnapshot()); observed.Set(); });
        var observeFinished = observed.Wait(TimeSpan.FromSeconds(10));
        releaseStat.Set();
        Assert.True(observeFinished, "Observe stayed blocked behind GetTotal's file-system stat");
        await Task.WhenAll(total, observe);
    }

    [Fact(DisplayName = "A GetTotal that finishes late for an older snapshot cannot replace the cached total of the newer snapshot")]
    public async Task GetTotal_OlderSnapshotFinishingLate_DoesNotOverwriteNewerCachedTotal()
    {
        using var inStat = new ManualResetEventSlim(false);
        using var releaseStat = new ManualResetEventSlim(false);
        var statCalls = 0;
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\photos\a.jpg", new byte[100]);
        fs.StatHook = _ =>
        {
            Interlocked.Increment(ref statCalls);
            inStat.Set();
            releaseStat.Wait(TimeSpan.FromSeconds(60));
            return null;
        };
        var catalog = new ReviewCatalog();
        var tracker = new SourceSizeTracker(catalog, fs);

        catalog.Reset([@"C:\photos\a.jpg"]); // no cached Length: snapshot A must stat
        tracker.Observe(catalog.EntriesSnapshot());
        var totalA = Task.Run(tracker.GetTotal);
        Assert.True(inStat.Wait(TimeSpan.FromSeconds(10)), "GetTotal(A) never started statting");

        catalog.Reset([@"C:\photos\b.jpg"]);
        catalog.UpdateMetadata(@"C:\photos\b.jpg", 5L, DateTime.UtcNow); // snapshot B needs no stat
        tracker.Observe(catalog.EntriesSnapshot());
        Assert.Equal(5L, tracker.GetTotal());

        releaseStat.Set();
        Assert.Equal(100L, await totalA); // the late caller still gets its own result

        Assert.Equal(5L, tracker.GetTotal());
        Assert.Equal(1, Volatile.Read(ref statCalls)); // B's cache hit: no extra stat
        Assert.Equal(0, tracker.LastFsCallCount);
    }
}

