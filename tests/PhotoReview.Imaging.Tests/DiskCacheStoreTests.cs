using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// DiskCacheStore is the shared atomic-write/quota-prune primitive behind both
/// ThumbnailCache and PreviewImageService's disk cache (PR-029); tested directly here
/// since it has no async/background timing to race against.
/// </summary>
[Trait("Category", "Slow")]
public sealed class DiskCacheStoreTests : IDisposable
{
    private readonly TempRoot _root = new("disk-cache-store");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "PruneDirectory deletes least-recently-used files until at or under quota")]
    public void PruneDirectoryEvictsLeastRecentlyUsedFirst()
    {
        var dir = _root.Dir("prune");
        var oldest = WriteFile(dir, "oldest.png", 100, accessedAgo: TimeSpan.FromMinutes(30));
        var middle = WriteFile(dir, "middle.png", 100, accessedAgo: TimeSpan.FromMinutes(20));
        var newest = WriteFile(dir, "newest.png", 100, accessedAgo: TimeSpan.FromMinutes(10));

        DiskCacheStore.PruneDirectory(dir, "*.png", maxBytes: 150, logContext: "test");

        Assert.True(!File.Exists(oldest) && !File.Exists(middle) && File.Exists(newest));
    }

    [Fact(DisplayName = "PruneDirectory is a no-op when the directory is already within quota")]
    public void PruneDirectoryLeavesFilesUntouchedWithinQuota()
    {
        var dir = _root.Dir("within-quota");
        var a = WriteFile(dir, "a.png", 50, accessedAgo: TimeSpan.FromMinutes(5));
        var b = WriteFile(dir, "b.png", 50, accessedAgo: TimeSpan.FromMinutes(1));

        DiskCacheStore.PruneDirectory(dir, "*.png", maxBytes: 1_000, logContext: "test");

        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact(DisplayName = "PruneDirectory on a missing directory does not throw")]
    public void PruneDirectoryToleratesMissingDirectory()
    {
        var missing = _root.Combine("does-not-exist");

        var thrown = Record.Exception(() => DiskCacheStore.PruneDirectory(missing, "*.png", maxBytes: 10, logContext: "test"));

        Assert.Null(thrown);
        Assert.False(Directory.Exists(missing)); // pruning must not create the directory it was asked to trim
    }

    [Fact(DisplayName = "PruneDirectory removes preview metadata with evicted PNG and orphan metadata")]
    public void PruneDirectoryKeepsPngAndMetadataPaired()
    {
        var dir = _root.Dir("paired-prune");
        var old = WriteFile(dir, "old.png", 100, accessedAgo: TimeSpan.FromMinutes(30));
        File.WriteAllText(old + ".meta", "Wpf|1");
        var current = WriteFile(dir, "current.png", 100, accessedAgo: TimeSpan.FromMinutes(1));
        File.WriteAllText(Path.Combine(dir, "orphan.png.meta"), "Wpf|1");

        DiskCacheStore.PruneDirectory(dir, "*.png", maxBytes: 150, logContext: "test", companionSuffix: ".meta");

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(old + ".meta"));
        Assert.True(File.Exists(current));
        Assert.False(File.Exists(Path.Combine(dir, "orphan.png.meta")));
    }

    [Fact(DisplayName = "WriteAtomicallyAsync leaves no temp file behind and the final file is readable")]
    public async Task WriteAtomicallyAsyncProducesOnlyTheFinalFile()
    {
        var dir = _root.Dir("write-atomic");
        var cachePath = Path.Combine(dir, "out.png");
        var image = DecodeFixture();

        await DiskCacheStore.WriteAtomicallyAsync(image, cachePath);

        var files = Directory.GetFiles(dir);
        Assert.True(files is [var only] && only == cachePath && new FileInfo(cachePath).Length > 0);
    }

    private static WpfDecodedImage DecodeFixture()
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(TestImages.PreviewPng);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return new WpfDecodedImage(bitmap);
    }

    [Fact(DisplayName = "ClearDirectory removes every matching file")]
    public void ClearDirectoryRemovesAllMatchingFiles()
    {
        var dir = _root.Dir("clear");
        var a = WriteFile(dir, "a.png", 10, accessedAgo: TimeSpan.Zero);
        var b = WriteFile(dir, "b.png", 10, accessedAgo: TimeSpan.Zero);

        DiskCacheStore.ClearDirectory(dir, "*.png", logContext: "test");

        Assert.True(!File.Exists(a) && !File.Exists(b));
    }

    [Fact(DisplayName = "DiskCacheStore instance SchedulePrune and WaitForPruneAsync complete successfully")]
    public async Task InstanceSchedulePruneAndWaitForPruneAsync()
    {
        var dir = _root.Dir("instance-prune");
        var oldest = WriteFile(dir, "oldest.png", 100, accessedAgo: TimeSpan.FromMinutes(30));
        var newest = WriteFile(dir, "newest.png", 100, accessedAgo: TimeSpan.FromMinutes(10));

        var store = new DiskCacheStore(dir, "*.png", maxBytes: 150);
        store.SchedulePrune();
        var finished = await store.WaitForPruneAsync(TimeSpan.FromSeconds(5));

        Assert.True(finished);
        Assert.True(!File.Exists(oldest) && File.Exists(newest));
    }

    [Fact(DisplayName = "IMG-10: passes under quota reuse tracked size instead of re-enumerating the directory")]
    public async Task PrunePassUnderQuotaSkipsFullScan()
    {
        var dir = _root.Dir("incremental-under");
        WriteFile(dir, "a.png", 100, accessedAgo: TimeSpan.FromMinutes(30));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1_000);

        store.SchedulePrune(); // size unknown: one full scan establishes the tracked total
        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, store.FullScanCount);

        for (var i = 0; i < 5; i++)
        {
            var path = WriteFile(dir, $"n{i}.png", 100, accessedAgo: TimeSpan.Zero);
            store.NoteWritten(path);
            store.SchedulePrune();
            Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(1, store.FullScanCount); // 600 bytes tracked <= 1000: no re-enumeration
    }

    [Fact(DisplayName = "IMG-10: noted writes that exceed quota trigger a scan that evicts the oldest files")]
    public async Task NotedWritesOverQuotaEvictOldest()
    {
        var dir = _root.Dir("incremental-over");
        var oldest = WriteFile(dir, "oldest.png", 100, accessedAgo: TimeSpan.FromMinutes(30));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 250);
        store.SchedulePrune();
        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(5)));
        Assert.True(File.Exists(oldest));

        var mid = WriteFile(dir, "mid.png", 100, accessedAgo: TimeSpan.FromMinutes(20));
        store.NoteWritten(mid);
        var newest = WriteFile(dir, "newest.png", 100, accessedAgo: TimeSpan.FromMinutes(10));
        store.NoteWritten(newest); // 300 tracked > 250
        store.SchedulePrune();
        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(5)));

        Assert.True(!File.Exists(oldest) && File.Exists(mid) && File.Exists(newest));
        Assert.Equal(2, store.FullScanCount);
    }

    [Fact(DisplayName = "DiskCacheStore instance ClearDirectory empties its directory")]
    public void InstanceClearDirectoryRemovesFiles()
    {
        var dir = _root.Dir("instance-clear");
        var a = WriteFile(dir, "a.png", 10, accessedAgo: TimeSpan.Zero);
        var b = WriteFile(dir, "b.png", 10, accessedAgo: TimeSpan.Zero);

        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1_000);
        store.ClearDirectory();

        Assert.True(!File.Exists(a) && !File.Exists(b));
    }

    private static string WriteFile(string dir, string name, int bytes, TimeSpan accessedAgo)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        var stamp = DateTime.UtcNow - accessedAgo;
        File.SetLastAccessTimeUtc(path, stamp);
        File.SetCreationTimeUtc(path, stamp);
        return path;
    }
}
