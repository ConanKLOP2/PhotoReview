using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// C1: two persist workers across cache epochs can target the same cache path. A stale (older-epoch) worker's cleanup must not
/// delete the file a newer-epoch worker wrote for the same key.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServicePersistTwoWorkerTests : IDisposable
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);
    private readonly TempRoot _root = new("PersistTwoWorker");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "A stale worker's cleanup does not delete the file a newer-epoch worker wrote for the same cache path")]
    public async Task StaleWorkerCleanup_AfterNewerWorkerWroteSamePath_KeepsNewerFile()
    {
        var source = Path.Combine(_root.Dir("src"), "opaque.png");
        File.WriteAllBytes(source, TestImages.OpaquePng);
        var dir = _root.Dir("cache");
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, WpfBitmapSourceCodec.Instance, diskCacheDirectory: dir);

        using var firstWriteDone = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        using var secondWriteDone = new ManualResetEventSlim(false);
        var hookCalls = 0;
        var gateTimedOut = false;
        service.AfterPersistWriteForTests = () =>
        {
            if (Interlocked.Increment(ref hookCalls) == 1)
            {
                firstWriteDone.Set(); // worker 1 (epoch 1) has written the file and now parks before its stale re-check
                if (!releaseFirst.Wait(GateTimeout)) gateTimedOut = true;
            }
            else
            {
                secondWriteDone.Set(); // worker 2 (epoch 2) has written the same path
            }
        };
        try
        {
            var key = service.GetCurrentCacheKey(source);
            await service.GetPreviewAsync(source, key);
            Assert.True(firstWriteDone.Wait(GateTimeout));

            service.ClearCache(); // drops the RAM copy so the next load decodes again
            service.ClearDisk(); // epoch 2; removes worker 1's file so the next load misses disk and persists again
            await service.GetPreviewAsync(source, key);
            Assert.True(secondWriteDone.Wait(GateTimeout));
            Assert.True(service.HasDiskCachedPreview(key)); // the newer worker's file is on disk

            releaseFirst.Set(); // worker 1 now notices its epoch is stale and cleans up
            await service.ShutdownPersistWorkersAsync();

            Assert.False(gateTimedOut);
            Assert.Equal(2, Volatile.Read(ref hookCalls));
            Assert.True(service.HasDiskCachedPreview(key));
        }
        finally
        {
            releaseFirst.Set();
            await service.ShutdownPersistWorkersAsync();
            await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        }
    }
}
