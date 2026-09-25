using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Review round 7: atomic-write temp files orphaned by a killed process are swept at start-up and by Clear cache;
/// <see cref="PreviewImageService.HasDiskCachedPreview"/> reports a persisted preview.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class CacheTempFileAndDiskProbeTests : IDisposable
{
    private readonly TempRoot _root = new("r7-cache");

    public void Dispose() => _root.Dispose();

    private static string Temp(string directory, string name, TimeSpan age)
    {
        var path = Path.Combine(directory, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact(DisplayName = "Preview cache start-up deletes stale .pv4 temp files and keeps fresh ones and entries")]
    public async Task PreviewStartup_DeletesStaleTempFiles_KeepsFreshAndEntries()
    {
        var dir = _root.Dir("preview");
        var stale = Temp(dir, "a.pv4", TimeSpan.FromHours(2));
        var fresh = Temp(dir, "b.pv4", TimeSpan.Zero);
        var entry = Path.Combine(dir, "c.pv4");
        File.WriteAllBytes(entry, [1]);
        File.SetLastWriteTimeUtc(entry, DateTime.UtcNow - TimeSpan.FromHours(2));

        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: dir);
        try
        {
            await service.StartupCleanup.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh)); // may belong to a write in flight
            Assert.True(File.Exists(entry));
        }
        finally { await service.ShutdownPersistWorkersAsync(); }
    }

    [Fact(DisplayName = "Thumbnail cache start-up deletes stale .png temp files and keeps fresh ones")]
    public async Task ThumbnailStartup_DeletesStaleTempFiles()
    {
        var dir = _root.Dir("thumbs");
        var stale = Temp(dir, "a.png", TimeSpan.FromHours(2));
        var fresh = Temp(dir, "b.png", TimeSpan.Zero);

        using var cache = new ThumbnailCache(dir);
        await cache.StartupCleanup.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact(DisplayName = "Clear cache also deletes temp files, whatever their age")]
    public async Task ClearDisk_DeletesTempFiles()
    {
        var dir = _root.Dir("clear");
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: dir);
        try
        {
            await service.StartupCleanup.WaitAsync(TimeSpan.FromSeconds(10));
            var fresh = Temp(dir, "a.pv4", TimeSpan.Zero);
            var thumbFresh = Temp(dir, "b.png", TimeSpan.Zero);

            service.ClearDisk();

            Assert.False(File.Exists(fresh));
            Assert.False(File.Exists(thumbFresh));
        }
        finally { await service.ShutdownPersistWorkersAsync(); }
    }

    [Fact(DisplayName = "HasDiskCachedPreview is true once the preview is persisted, false before and with the disk cache off")]
    public async Task HasDiskCachedPreview_ReflectsPersistedEntry()
    {
        var source = Path.Combine(_root.Dir("src"), "opaque.png");
        File.WriteAllBytes(source, TestImages.OpaquePng); // opaque: alpha previews are never persisted
        var dir = _root.Dir("probe");
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: dir);
        var disabled = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: dir,
            disableDiskCacheOverride: true);
        try
        {
            var key = service.GetCurrentCacheKey(source);
            Assert.False(service.HasDiskCachedPreview(key));

            await service.GetPreviewAsync(source, key);
            await service.ShutdownPersistWorkersAsync(); // drains the queued persist deterministically

            Assert.True(service.HasDiskCachedPreview(key));
            Assert.False(disabled.HasDiskCachedPreview(disabled.GetCurrentCacheKey(source)));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
            await disabled.ShutdownPersistWorkersAsync();
            await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        }
    }
}
