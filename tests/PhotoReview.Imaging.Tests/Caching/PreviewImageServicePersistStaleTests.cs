using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>A persist write that went stale (Clear Cache) between the write and the epoch re-check must not leave its file behind.</summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServicePersistStaleTests : IDisposable
{
    private readonly TempRoot _root = new("PersistStale");

    public void Dispose() => _root.Dispose();

    private (string Source, string Dir) Setup()
    {
        var source = Path.Combine(_root.Dir("src"), "opaque.png");
        File.WriteAllBytes(source, TestImages.OpaquePng); // opaque: alpha previews are never persisted
        return (source, _root.Dir("cache"));
    }

    [Fact(DisplayName = "A persist write overtaken by ClearCache is deleted, not left in the disk cache")]
    public async Task Persist_ClearCacheDuringWrite_DeletesStaleFile()
    {
        var (source, dir) = Setup();
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, WpfBitmapSourceCodec.Instance, diskCacheDirectory: dir);
        var hookCalls = 0;
        service.AfterPersistWriteForTests = () =>
        {
            if (Interlocked.Increment(ref hookCalls) == 1) service.ClearCache(); // bumps the epoch; does not touch disk files
        };
        try
        {
            var key = service.GetCurrentCacheKey(source);
            await service.GetPreviewAsync(source, key);
            await service.ShutdownPersistWorkersAsync();

            Assert.Equal(1, Volatile.Read(ref hookCalls));
            Assert.False(service.HasDiskCachedPreview(key));
            Assert.Empty(Directory.GetFiles(dir, "*.pv4"));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
            await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact(DisplayName = "Control: without a concurrent ClearCache the persisted preview stays on disk")]
    public async Task Persist_NoClearCache_KeepsFile()
    {
        var (source, dir) = Setup();
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, WpfBitmapSourceCodec.Instance, diskCacheDirectory: dir);
        var hookCalls = 0;
        service.AfterPersistWriteForTests = () => Interlocked.Increment(ref hookCalls);
        try
        {
            var key = service.GetCurrentCacheKey(source);
            await service.GetPreviewAsync(source, key);
            await service.ShutdownPersistWorkersAsync();

            Assert.Equal(1, Volatile.Read(ref hookCalls));
            Assert.True(service.HasDiskCachedPreview(key));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
            await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        }
    }
}