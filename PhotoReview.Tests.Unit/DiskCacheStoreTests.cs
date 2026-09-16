using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// DiskCacheStore is the shared atomic-write/quota-prune primitive behind both
/// ThumbnailCache and PreviewImageService's disk cache (PR-029); tested directly here
/// since it has no async/background timing to race against.
/// </summary>
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
        DiskCacheStore.PruneDirectory(missing, "*.png", maxBytes: 10, logContext: "test");
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

    private static BitmapSource DecodeFixture()
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(PreviewImageServiceTests.PreviewPng);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
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

    private string WriteFile(string dir, string name, int bytes, TimeSpan accessedAgo)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        var stamp = DateTime.UtcNow - accessedAgo;
        File.SetLastAccessTimeUtc(path, stamp);
        File.SetCreationTimeUtc(path, stamp);
        return path;
    }
}
