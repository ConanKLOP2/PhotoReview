using System.IO;
using PhotoReview.Imaging.Caching;
using PhotoReview.TestSupport.Windows.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

[Trait("Category", "HotPath")]
public sealed class ThumbnailCacheLifecycleTests : IDisposable
{
    private readonly TempRoot _root = new("thumb-lifecycle");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Disposing while a thumbnail load is in flight: the load ends without an error, nothing is cached, and new requests are refused")]
    public async Task DisposeDuringLoad_EndsCleanly()
    {
        var source = _root.File("a.jpg", EmbeddedThumbnailJpegFixture.CreateWithThumbnail(48, 16));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ThumbnailCache(_root.Dir("disk"), persistNewThumbnails: false, embeddedThumbnailReader: async (path, token) =>
        {
            entered.SetResult();
            await release.Task;
            return EmbeddedThumbnailReader.TryRead(path);
        });

        var pending = cache.GetAsync(source);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cache.Dispose();
        cache.Dispose(); // idempotent
        release.SetResult();

        var image = await pending.WaitAsync(TimeSpan.FromSeconds(10)); // the started load completes normally
        Assert.NotNull(image);
        Assert.Throws<ObjectDisposedException>(() => { _ = cache.GetAsync(source); }); // thrown synchronously
    }

    [Fact(DisplayName = "A caller that cancels its wait does not cancel the shared load: another caller still gets the thumbnail")]
    public async Task CallerCancellation_DoesNotPoisonTheSharedLoad()
    {
        var source = _root.File("b.jpg", EmbeddedThumbnailJpegFixture.CreateWithThumbnail(48, 16));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new ThumbnailCache(_root.Dir("disk2"), persistNewThumbnails: false, embeddedThumbnailReader: async (path, token) =>
        {
            entered.TrySetResult();
            await release.Task;
            return EmbeddedThumbnailReader.TryRead(path);
        });
        using var cts = new CancellationTokenSource();

        var cancelled = cache.GetAsync(source, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var patient = cache.GetAsync(source);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();

        Assert.NotNull(await patient.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact(DisplayName = "A failing embedded-thumbnail reader is not cached as a failure: the next request tries again")]
    public async Task FailedLoad_IsNotCached()
    {
        var source = _root.File("c.jpg", EmbeddedThumbnailJpegFixture.CreateWithThumbnail(48, 16));
        var calls = 0;
        using var cache = new ThumbnailCache(_root.Dir("disk3"), persistNewThumbnails: false, embeddedThumbnailReader: (path, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("transient");
            return Task.FromResult(EmbeddedThumbnailReader.TryRead(path));
        });

        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(source));
        var image = await cache.GetAsync(source);

        Assert.NotNull(image);
        Assert.Equal(2, calls);
    }
}
